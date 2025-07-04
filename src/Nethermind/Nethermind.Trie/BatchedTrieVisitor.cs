// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// An alternate trie visitor that try to optimize for read io by grouping/sorting node to be processed (as Job) in order
/// of its key. This works by having a list of partition, each is a stack of Job where each partition holds only Jobs for
/// a portion of the address space. The partition are ordered in increasing order. On run, each worker will go through
/// the partitions one by one in ascending order. The pointer to which partition to process next is shared with all worker
/// so that all worker works on a small common range of the address space.
///
/// The working set, which is the number of Job waiting to be processed is adjusted by increasing the partition number
/// and the max batch size (num of job to be processed on each worker visit to a partition).
/// If the size of working set is large enough, the required data between Job in a batch is available near each other
/// which increases cache hit. Additionally, by utilizing readahead flag, rocksdb can be instructed to pre-fetch a certain
/// amount of data ahead of the current read. As the job is processed in ascending key, the next job is likely to get pre-fetched.
/// This significantly reduce iops, and improve response time, although it might increase read amplification as some of
/// the pre-fetched data is wasted read, but overall, it significantly improve throughput.
///
/// The exact minimum size of working set depends on the size of the database, which seems
/// to increase linearly. For mainnet it seems to be 1Gb. Increasing the working
/// set decreases reads, however up to a certain point, the time taken for writes is much higher than read, so no
/// point in increasing memory budget further. For mainnet, that seems to be 8Gb.
/// </summary>
public class BatchedTrieVisitor<TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    // Not using shared pool so GC can reclaim them later.
    private readonly ArrayPool<Job> _jobArrayPool = ArrayPool<Job>.Create();
    private static readonly AccountDecoder _accountDecoder = new();

    private readonly ArrayPool<(TrieNode, TNodeContext, SmallTrieVisitContext)> _trieNodePool =
        ArrayPool<(TrieNode, TNodeContext, SmallTrieVisitContext)>.Create();

    private readonly int _maxBatchSize;
    private readonly long _partitionCount;
    private readonly CompactStack<Job>[] _partitions;
    private readonly long _targetCurrentItems;

    private long _activeJobs;
    private long _queuedJobs;
    private bool _failed;
    private long _currentPointer;
    private readonly long _readAheadThreshold;

    private readonly ITrieNodeResolver _resolver;
    private readonly ITreeVisitor<TNodeContext> _visitor;

    public BatchedTrieVisitor(
        ITreeVisitor<TNodeContext> visitor,
        ITrieNodeResolver resolver,
        VisitingOptions visitingOptions)
    {
        _visitor = visitor;
        _resolver = resolver;
        if (resolver.Scheme != INodeStorage.KeyScheme.Hash)
            throw new InvalidOperationException("BatchedTrieVisitor can only be used with Hash database.");

        _maxBatchSize = 128;

        // The keccak + context itself should be 40 byte. But the measured byte seems to be 52 from GC stats POV.
        // The * 2 is just margin. RSS is still higher though, but that could be due to more deserialization.
        long recordSize = (52 + Unsafe.SizeOf<TNodeContext>()) * 2;
        long recordCount = visitingOptions.FullScanMemoryBudget / recordSize;
        if (recordCount == 0) recordCount = 1;

        // Generally, at first, we want to attempt to maximize number of partition. This tend to increase throughput
        // compared to increasing batch size.
        _partitionCount = recordCount / _maxBatchSize;
        if (_partitionCount == 0) _partitionCount = 1;

        long expectedDbSize = 240.GiB(); // Unpruned size

        // Get estimated num of file (expected db size / 64MiB), multiplied by a reasonable num of thread we want to
        // confine to a file. If its too high, the overhead of looping through the stack can get a bit high at the end
        // of the visit. But then again its probably not much.
        long maxPartitionCount = (expectedDbSize / 64.MiB()) * Math.Min(4, visitingOptions.MaxDegreeOfParallelism);

        if (_partitionCount > maxPartitionCount)
        {
            _partitionCount = maxPartitionCount;
            _maxBatchSize = (int)(recordCount / _partitionCount);
            if (_maxBatchSize == 0) _maxBatchSize = 1;
        }

        // Estimated readahead threshold used to determine how many node in a partition to enable readahead.
        long estimatedPartitionAddressSpaceSize = expectedDbSize / _partitionCount;
        long toleratedPerNodeReadAmp =
            16.KiB(); // If the estimated per-node read is above this, don't enable readahead.
        _readAheadThreshold = estimatedPartitionAddressSpaceSize / toleratedPerNodeReadAmp;

        // Calculating estimated pool margin at about 5% of total working size. The working set size fluctuate a bit so
        // this is to reduce allocation.
        long estimatedPoolMargin = (long)(((double)recordCount / 128) * 0.05);
        ObjectPool<CompactStack<Job>.Node> jobPool = new DefaultObjectPool<CompactStack<Job>.Node>(
            new CompactStack<Job>.ObjectPoolPolicy(128), (int)estimatedPoolMargin);

        _currentPointer = 0;
        _queuedJobs = 0;
        _activeJobs = 0;
        _targetCurrentItems = _partitionCount * _maxBatchSize;

        // This need to be very small
        _partitions = new CompactStack<Job>[_partitionCount];
        for (int i = 0; i < _partitionCount; i++)
        {
            _partitions[i] = new CompactStack<Job>(jobPool);
        }
    }

    // Determine the locality of the key. I guess if you use paprika or something, you'd need to modify this.
    int CalculatePartitionIdx(ValueHash256 key)
    {
        uint number = BinaryPrimitives.ReadUInt32BigEndian(key.Bytes);
        return (int)(number * (ulong)_partitionCount / uint.MaxValue);
    }

    public void Start(
        ValueHash256 root,
        TrieVisitContext trieVisitContext)
    {
        // Start with the root
        SmallTrieVisitContext rootContext = new(trieVisitContext);
        _partitions[CalculatePartitionIdx(root)].Push(new Job(root, default, rootContext));
        _activeJobs = 1;
        _queuedJobs = 1;

        try
        {
            using ArrayPoolList<Task> tasks = Enumerable.Range(0, trieVisitContext.MaxDegreeOfParallelism)
                .Select(_ => Task.Run(BatchedThread))
                .ToPooledList(trieVisitContext.MaxDegreeOfParallelism);

            Task.WaitAll(tasks.AsSpan());
        }
        catch (Exception)
        {
            _failed = true;
            throw;
        }
    }

    ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)>? GetNextBatch()
    {
        CompactStack<Job>? theStack;
        do
        {
            long partitionIdx = Interlocked.Increment(ref _currentPointer);
            if (partitionIdx == _partitionCount)
            {
                Interlocked.Add(ref _currentPointer, -_partitionCount);

                GC.Collect(); // Simulate GC collect of standard visitor
            }

            partitionIdx %= _partitionCount;

            if (_activeJobs == 0 || _failed)
            {
                // Its finished
                return null;
            }

            if (_queuedJobs == 0)
            {
                // Just a small timeout to prevent threads from loading CPU
                // Note, there are other threads also going through the stacks, so its fine to have this high.
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }

            theStack = _partitions[partitionIdx];
            lock (theStack)
            {
                if (!theStack.IsEmpty) break;
            }
        } while (true);

        ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)> finalBatch = new(_trieNodePool, _maxBatchSize);

        if (_activeJobs < _targetCurrentItems)
        {
            lock (theStack)
            {
                for (int i = 0; i < _maxBatchSize; i++)
                {
                    if (!theStack.TryPop(out Job item)) break;
                    finalBatch.Add((_resolver.FindCachedOrUnknown(TreePath.Empty, item.Key.ToCommitment()), item.NodeContext,
                        item.Context));
                    Interlocked.Decrement(ref _queuedJobs);
                }
            }
        }
        else
        {
            // So we get more than the batch size, then we sort it by level, and take only the maxNodeBatch nodes with
            // the higher level. This is so that higher level is processed first to reduce memory usage. Its inaccurate,
            // and hacky, but it works.
            using ArrayPoolList<Job> preSort = new(_jobArrayPool, _maxBatchSize * 4);
            lock (theStack)
            {
                for (int i = 0; i < _maxBatchSize * 4; i++)
                {
                    if (!theStack.TryPop(out Job item)) break;
                    preSort.Add(item);
                    Interlocked.Decrement(ref _queuedJobs);
                }
            }

            // Sort by level
            if (_activeJobs > _targetCurrentItems)
            {
                preSort.AsSpan().Sort(static (item1, item2) => item1.Context.Level.CompareTo(item2.Context.Level) * -1);
            }

            int endIdx = Math.Min(_maxBatchSize, preSort.Count);

            for (int i = 0; i < endIdx; i++)
            {
                Job job = preSort[i];

                TrieNode node = _resolver.FindCachedOrUnknown(TreePath.Empty, job.Key.ToCommitment());
                finalBatch.Add((node, job.NodeContext, job.Context));
            }

            // Add back what we won't process. In reverse order.
            lock (theStack)
            {
                for (int i = preSort.Count - 1; i >= endIdx; i--)
                {
                    theStack.Push(preSort[i]);
                    Interlocked.Increment(ref _queuedJobs);
                }
            }
        }

        return finalBatch;
    }


    void QueueNextNodes(ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)> batchResult)
    {
        // Reverse order is important so that higher level appear at the end of the stack.
        TreePath emptyPath = TreePath.Empty;
        for (int i = batchResult.Count - 1; i >= 0; i--)
        {
            (TrieNode trieNode, TNodeContext nodeContext, SmallTrieVisitContext ctx) = batchResult[i];
            if (trieNode.NodeType == NodeType.Unknown && trieNode.FullRlp.IsNotNull)
            {
                // Inline node. Seems rare, so its fine to create new list for this. Does not have a keccak
                // to queue, so we'll just process it inline.
                using ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)> recursiveResult = new(1);
                trieNode.ResolveNode(_resolver, emptyPath);
                Interlocked.Increment(ref _activeJobs);
                AcceptResolvedNode(trieNode, nodeContext, _resolver, ctx, recursiveResult);
                QueueNextNodes(recursiveResult);
                continue;
            }

            ValueHash256 keccak = trieNode.Keccak;
            int partitionIdx = CalculatePartitionIdx(keccak);
            Interlocked.Increment(ref _activeJobs);
            Interlocked.Increment(ref _queuedJobs);

            var theStack = _partitions[partitionIdx];
            lock (theStack)
            {
                theStack.Push(new Job(keccak, nodeContext, ctx));
            }
        }

        Interlocked.Decrement(ref _activeJobs);
    }


    private void BatchedThread()
    {
        using ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)> nextToProcesses = new(_maxBatchSize);
        using ArrayPoolList<int> resolveOrdering = new(_maxBatchSize);
        ArrayPoolList<(TrieNode, TNodeContext, SmallTrieVisitContext)>? currentBatch;
        TreePath emptyPath = TreePath.Empty;
        while ((currentBatch = GetNextBatch()) is not null)
        {
            // Storing the idx separately as the ordering is important to reduce memory (approximate dfs ordering)
            // but the path ordering is important for read amplification
            resolveOrdering.Clear();
            for (int i = 0; i < currentBatch.Count; i++)
            {
                (TrieNode? cur, TNodeContext _, SmallTrieVisitContext ctx) = currentBatch[i];

                cur.ResolveKey(_resolver, ref emptyPath, false);

                if (cur.FullRlp.IsNotNull) continue;
                if (cur.Keccak is null)
                    ThrowUnableToResolve(ctx);

                resolveOrdering.Add(i);
            }

            // This innocent looking sort is surprisingly effective when batch size is large enough. The sort itself
            // take about 0.1% of the time, so not very cpu intensive in this case.
            resolveOrdering
                .AsSpan()
                .Sort((item1, item2) => currentBatch[item1].Item1.Keccak.CompareTo(currentBatch[item2].Item1.Keccak));

            ReadFlags flags = ReadFlags.None;
            if (resolveOrdering.Count > _readAheadThreshold)
            {
                flags = ReadFlags.HintReadAhead;
            }

            // This loop is about 60 to 70% of the time spent. If you set very high memory budget, this drop to about 50MB.
            for (int i = 0; i < resolveOrdering.Count; i++)
            {
                int idx = resolveOrdering[i];

                (TrieNode nodeToResolve, TNodeContext nodeContext, SmallTrieVisitContext ctx) = currentBatch[idx];
                try
                {
                    Hash256 theKeccak = nodeToResolve.Keccak;
                    nodeToResolve.ResolveNode(_resolver, emptyPath, flags);
                    nodeToResolve.Keccak = theKeccak; // The resolve may set a key which clear the keccak
                }
                catch (TrieException)
                {
                    _visitor.VisitMissingNode(nodeContext, nodeToResolve.Keccak);
                }
            }

            // Going in reverse to reduce memory
            for (int i = currentBatch.Count - 1; i >= 0; i--)
            {
                (TrieNode nodeToResolve, TNodeContext nodeContext, SmallTrieVisitContext ctx) = currentBatch[i];

                nextToProcesses.Clear();
                if (nodeToResolve.FullRlp.IsNull)
                {
                    // Still need to decrement counter
                    QueueNextNodes(nextToProcesses);
                    return; // missing node
                }

                AcceptResolvedNode(nodeToResolve, nodeContext, _resolver, ctx, nextToProcesses);
                QueueNextNodes(nextToProcesses);
            }

            currentBatch.Dispose();
        }

        return;

        static void ThrowUnableToResolve(in SmallTrieVisitContext ctx)
        {
            throw new TrieException(
                $"Unable to resolve node without Keccak. ctx: {ctx.Level}, {ctx.ExpectAccounts}, {ctx.IsStorage}");
        }
    }

    /// <summary>
    /// Like `Accept`, but does not execute its children. Instead it return the next trie to visit in the list
    /// `nextToVisit`. Also, it assume the node is already resolved.
    /// </summary>
    internal void AcceptResolvedNode(TrieNode node, in TNodeContext nodeContext, ITrieNodeResolver nodeResolver, SmallTrieVisitContext trieVisitContext, IList<(TrieNode, TNodeContext, SmallTrieVisitContext)> nextToVisit)
    {
        // Note: The path is not maintained here, its just for a placeholder. This code is only used for BatchedTrieVisitor
        // which should only be used with hash keys.
        TreePath emptyPath = TreePath.Empty;
        switch (node.NodeType)
        {
            case NodeType.Branch:
                {
                    _visitor.VisitBranch(nodeContext, node);
                    trieVisitContext.Level++;

                    for (int i = 0; i < TrieNode.BranchesCount; i++)
                    {
                        TrieNode child = node.GetChild(nodeResolver, ref emptyPath, i);
                        if (child is not null)
                        {
                            child.ResolveKey(nodeResolver, ref emptyPath, false);
                            TNodeContext childContext = nodeContext.Add((byte)i);

                            if (_visitor.ShouldVisit(childContext, child.Keccak!))
                            {
                                SmallTrieVisitContext childCtx = trieVisitContext; // Copy
                                nextToVisit.Add((child, childContext, childCtx));
                            }

                            if (child.IsPersisted)
                            {
                                node.UnresolveChild(i);
                            }
                        }
                    }

                    break;
                }
            case NodeType.Extension:
                {
                    _visitor.VisitExtension(nodeContext, node);
                    TrieNode child = node.GetChild(nodeResolver, ref emptyPath, 0) ?? throw new InvalidDataException($"Child of an extension {node.Key} should not be null.");
                    child.ResolveKey(nodeResolver, ref emptyPath, false);
                    TNodeContext childContext = nodeContext.Add(node.Key!);
                    if (_visitor.ShouldVisit(childContext, child.Keccak!))
                    {
                        trieVisitContext.Level++;


                        nextToVisit.Add((child, childContext, trieVisitContext));
                    }

                    break;
                }

            case NodeType.Leaf:
                {
                    _visitor.VisitLeaf(nodeContext, node);

                    if (!trieVisitContext.IsStorage && trieVisitContext.ExpectAccounts) // can combine these conditions
                    {
                        TNodeContext childContext = nodeContext.Add(node.Key!);

                        Rlp.ValueDecoderContext decoderContext = new Rlp.ValueDecoderContext(node.Value.Span);
                        if (!_accountDecoder.TryDecodeStruct(ref decoderContext, out AccountStruct account))
                        {
                            throw new InvalidDataException("Non storage leaf should be an account");
                        }
                        _visitor.VisitAccount(childContext, node, account);

                        if (account.HasStorage && _visitor.ShouldVisit(childContext, account.StorageRoot))
                        {
                            trieVisitContext.IsStorage = true;
                            TNodeContext storageContext = childContext.AddStorage(account.StorageRoot);
                            trieVisitContext.Level++;

                            if (node.TryResolveStorageRoot(nodeResolver, ref emptyPath, out TrieNode? storageRoot))
                            {
                                nextToVisit.Add((storageRoot!, storageContext, trieVisitContext));
                            }
                            else
                            {
                                _visitor.VisitMissingNode(storageContext, account.StorageRoot);
                            }

                            trieVisitContext.IsStorage = false;
                        }
                    }

                    break;
                }

            default:
                throw new TrieException($"An attempt was made to visit a node {node.Keccak} of type {node.NodeType}");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Job
    {
        public readonly ValueHash256 Key;
        public readonly TNodeContext NodeContext;
        public readonly SmallTrieVisitContext Context;

        public Job(ValueHash256 key, TNodeContext nodeContext, SmallTrieVisitContext context)
        {
            Key = key;
            NodeContext = nodeContext;
            Context = context;
        }
    }
}

public readonly struct EmptyContext : INodeContext<EmptyContext>
{
    public EmptyContext Add(ReadOnlySpan<byte> nibblePath) => this;
    public EmptyContext Add(byte nibble) => this;
    public EmptyContext AddStorage(in ValueHash256 storage) => this;
}

public struct TreePathContext : INodeContext<TreePathContext>
{
    public TreePath Path = TreePath.Empty;

    public TreePathContext()
    {
    }

    public TreePathContext Add(ReadOnlySpan<byte> nibblePath)
    {
        return new TreePathContext()
        {
            Path = Path.Append(nibblePath)
        };
    }

    public TreePathContext Add(byte nibble)
    {
        return new TreePathContext()
        {
            Path = Path.Append(nibble)
        };
    }

    public readonly TreePathContext AddStorage(in ValueHash256 storage)
    {
        return new TreePathContext();
    }
}

public interface ITreePathContextWithStorage
{
    public TreePath Path { get; }
    public Hash256? Storage { get; }
}

public readonly struct TreePathContextWithStorage : ITreePathContextWithStorage, INodeContext<TreePathContextWithStorage>
{
    public TreePath Path { get; init; } = TreePath.Empty;
    public Hash256? Storage { get; init; } = null; // Not using ValueHash as value is shared with many context.

    public TreePathContextWithStorage()
    {
    }

    public TreePathContextWithStorage Add(ReadOnlySpan<byte> nibblePath)
    {
        return new TreePathContextWithStorage()
        {
            Path = Path.Append(nibblePath),
            Storage = Storage
        };
    }

    public TreePathContextWithStorage Add(byte nibble)
    {
        return new TreePathContextWithStorage()
        {
            Path = Path.Append(nibble),
            Storage = Storage
        };
    }

    public readonly TreePathContextWithStorage AddStorage(in ValueHash256 storage)
    {
        return new TreePathContextWithStorage()
        {
            Path = TreePath.Empty,
            Storage = Path.Path.ToCommitment(),
        };
    }
}

/// <summary>
/// Used as a substitute for TreePathContextWithStorage but does not actually keep track of path and storage.
/// Used for hash only database pruning to reduce memory usage.
/// </summary>
public struct NoopTreePathContextWithStorage : ITreePathContextWithStorage, INodeContext<NoopTreePathContextWithStorage>
{
    public readonly NoopTreePathContextWithStorage Add(ReadOnlySpan<byte> nibblePath)
    {
        return this;
    }

    public readonly NoopTreePathContextWithStorage Add(byte nibble)
    {
        return this;
    }

    public readonly NoopTreePathContextWithStorage AddStorage(in ValueHash256 storage)
    {
        return this;
    }

    public readonly TreePath Path => TreePath.Empty;
    public readonly Hash256? Storage => null;
}


public interface INodeContext<out TNodeContext>
    // The context needs to be the struct so that it's passed nicely via in and returned from the methods.
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    TNodeContext Add(ReadOnlySpan<byte> nibblePath);

    TNodeContext Add(byte nibble);
    TNodeContext AddStorage(in ValueHash256 storage);
}
