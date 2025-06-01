// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Call;
using Nethermind.PatternAnalyzer.Plugin.Types;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public sealed class CallStatsAnalyzerTxTracer : StatsAnalyzerTxTracer<CallData, CallStat, CallAnalyzerTxTrace>

{
    private Address? _currentAddress;
    private Hash256? _currentCodeHash;
    private int? _currentCodeSize;


    public CallStatsAnalyzerTxTracer(ResettableList<CallData> buffer,
        CallStatsAnalyzer callStatsAnalyzer, SortOrder sort, CancellationToken ct) : base(buffer, callStatsAnalyzer,
        sort, ct)

    {
        IsTracingActions = true;
        IsTracingCode  = true;
    }


    public override CallAnalyzerTxTrace BuildResult(long fromBlock = 0, DateTime fromTimeStampDate = default, long toBlock = 0, DateTime toTimeStampDate = default)
    {
        Build();
        CallAnalyzerTxTrace trace = new CallAnalyzerTxTrace{
            InitialBlockNumber = fromBlock,
            InitialBlockDateTime = fromTimeStampDate.ToString(),
            CurrentBlockNumber = toBlock,
            CurrentBlockDateTime = toTimeStampDate.ToString(),
            Entries = new()
        };

        var stats = StatsAnalyzer.Stats(Sort);

        foreach (var stat in stats)
        {

            var entry = new CallAnalyzerTraceEntry
            {
                Address = stat.Address,
                CodeHash = stat.CodeHash,
                CodeSize = stat.CodeSize,
                Count = stat.Count
            };
            trace.Entries.Add(entry);
        }


        return trace;
    }


    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!isPrecompileCall && new[]
                    { ExecutionType.TRANSACTION, ExecutionType.CALL, ExecutionType.STATICCALL, ExecutionType.CALLCODE, ExecutionType.DELEGATECALL, ExecutionType.EOFCALL, ExecutionType.EOFSTATICCALL,ExecutionType.EOFDELEGATECALL  }
                .Contains(callType)) {
            _currentAddress = to;
        }
    }


    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        if (_currentAddress is null)
            return;

        _currentCodeHash = Keccak.Compute(byteCode.Span);

        _currentCodeSize = byteCode.Length;

        Queue?.Enqueue(new CallData(_currentAddress, Keccak.Compute(byteCode.Span), byteCode.Length));
        _currentAddress = null;
        _currentCodeHash = null;
        _currentCodeSize = null;
    }
}
