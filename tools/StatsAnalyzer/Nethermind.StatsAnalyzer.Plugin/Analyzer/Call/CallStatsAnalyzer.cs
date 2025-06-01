using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.PatternAnalyzer.Plugin.Types;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;

public readonly record struct CallData(Address Address, Hash256 CodeHash, int CodeSize);
public readonly record struct CallStat(Address Address, Hash256 CodeHash, int CodeSize, ulong Count);

public class CallStatsAnalyzer(int topN) : TopNAnalyzer<CallData, CallData, CallStat>(topN)
{
    private readonly Dictionary<Address, ulong> _counts = new();


    public override void Add(IEnumerable<CallData> calls)
    {
        TopNQueue.Clear();
        foreach (var call in calls)
        {
            var callCount = 1 + CollectionsMarshal.GetValueRefOrAddDefault(_counts, call.Address, out _);
            _counts[call.Address] = callCount;
            if (callCount >= MinSupport) TopNMap[call] = callCount;
            else TopNMap.Remove(call);
        }

        ProcessTopN();
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Add(CallData call)
    {
        Add([call]);
    }


    public override IEnumerable<CallStat> Stats(SortOrder order)
    {

        switch (order)
        {
            case SortOrder.Unordered:
                foreach (var (call, count) in TopNQueue.UnorderedItems)
                    yield return new CallStat(call.Address, call.CodeHash, call.CodeSize, count);
                break;
            case SortOrder.Ascending:
                var queue = new PriorityQueue<CallData, ulong>();
                foreach (var (call, count) in TopNQueue.UnorderedItems)
                    queue.Enqueue(call, count);
                while (queue.Count > 0)
                    if (queue.TryDequeue(out var call, out var count))
                        yield return new CallStat(call.Address, call.CodeHash, call.CodeSize, count);
                break;
            case SortOrder.Descending:
                var queueDecending =
                    new PriorityQueue<CallData, ulong>(TopN, Comparer<ulong>.Create((x, y) => y.CompareTo(x)));
                foreach (var (call, count) in TopNQueue.UnorderedItems) queueDecending.Enqueue(call, count);
                while (queueDecending.Count > 0)
                    if (queueDecending.TryDequeue(out var call, out var count))
                        yield return new CallStat(call.Address, call.CodeHash, call.CodeSize, count);
                break;
        }
    }
}
