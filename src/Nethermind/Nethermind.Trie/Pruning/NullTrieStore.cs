// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieStore : IScopedTrieStore
    {
        private NullTrieStore() { }

        public static NullTrieStore Instance { get; } = new();

        public TrieNode FindCachedOrUnknown(in TreePath treePath, Hash256 hash) => new(NodeType.Unknown, hash);

        public byte[] LoadRlp(in TreePath treePath, Hash256 hash, ReadFlags flags = ReadFlags.None) => [];

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => [];

        public ICommitter BeginCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new NullCommitter();

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => true;

        public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp) { }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 storageRoot) => this;

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

        internal class NullCommitter : ICommitter
        {
            public void Dispose() { }

            public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo) { }
        }
    }
}
