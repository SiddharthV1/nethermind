// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Metric;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

public class JsonRpcLocalStats(ITimestamper timestamper, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
    : IJsonRpcLocalStats
{
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly TimeSpan _reportingInterval = TimeSpan.FromSeconds(jsonRpcConfig.ReportIntervalSeconds);
    private readonly bool _enablePerMethodMetrics = jsonRpcConfig.EnablePerMethodMetrics;
    private ConcurrentDictionary<string, MethodStats> _currentStats = new();
    private ConcurrentDictionary<string, MethodStats> _previousStats = new();
    private readonly ConcurrentDictionary<string, MethodStats> _allTimeStats = new();
    private DateTime _lastReport = timestamper.UtcNow;
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly StringBuilder _reportStringBuilder = new();

    public MethodStats GetMethodStats(string methodName) => _allTimeStats.GetValueOrDefault(methodName, new MethodStats());

    public Task ReportCall(string method, long handlingTimeMicroseconds, bool success) =>
        ReportCall(new RpcReport(method, handlingTimeMicroseconds, success));

    public Task ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null)
    {
        if (string.IsNullOrWhiteSpace(report.Method))
        {
            return Task.CompletedTask;
        }

        if (!_logger.IsInfo)
        {
            return Task.CompletedTask;
        }

        return ReportCallInternal(report, elapsedMicroseconds, size);
    }

    private record MetricLabel(string method, bool success) : IMetricLabels
    {
        public string[] Labels => [method, success ? "success" : "fail"];
    }

    private async Task ReportCallInternal(RpcReport report, long elapsedMicroseconds, long? size)
    {
        // we don't want to block RPC calls any longer than required
        await Task.Yield();

        if (_enablePerMethodMetrics)
        {
            Metrics.JsonRpcCallLatencyMicros.Observe(elapsedMicroseconds, new MetricLabel(report.Method, report.Success));
        }

        BuildReport();

        MethodStats methodStats = _currentStats.GetOrAdd(report.Method, static _ => new MethodStats());
        MethodStats allTimeMethodStats = _allTimeStats.GetOrAdd(report.Method, static _ => new MethodStats());

        long reportHandlingTimeMicroseconds = elapsedMicroseconds == 0 ? report.HandlingTimeMicroseconds : elapsedMicroseconds;

        decimal sizeDec = size ?? 0;

        lock (methodStats)
        {
            if (report.Success)
            {
                methodStats.AvgTimeOfSuccesses =
                    (methodStats.Successes * methodStats.AvgTimeOfSuccesses + reportHandlingTimeMicroseconds) /
                    ++methodStats.Successes;
                methodStats.MaxTimeOfSuccess =
                    Math.Max(methodStats.MaxTimeOfSuccess, reportHandlingTimeMicroseconds);

                allTimeMethodStats.AvgTimeOfSuccesses =
                    (allTimeMethodStats.Successes * allTimeMethodStats.AvgTimeOfSuccesses +
                        reportHandlingTimeMicroseconds) /
                    ++allTimeMethodStats.Successes;
                allTimeMethodStats.MaxTimeOfSuccess =
                    Math.Max(allTimeMethodStats.MaxTimeOfSuccess, reportHandlingTimeMicroseconds);
            }
            else
            {
                methodStats.AvgTimeOfErrors =
                    (methodStats.Errors * methodStats.AvgTimeOfErrors + reportHandlingTimeMicroseconds) /
                    ++methodStats.Errors;
                methodStats.MaxTimeOfError = Math.Max(methodStats.MaxTimeOfError, reportHandlingTimeMicroseconds);

                allTimeMethodStats.AvgTimeOfErrors =
                    (allTimeMethodStats.Errors * allTimeMethodStats.AvgTimeOfErrors + reportHandlingTimeMicroseconds) /
                    ++allTimeMethodStats.Errors;
                allTimeMethodStats.MaxTimeOfError = Math.Max(allTimeMethodStats.MaxTimeOfError, reportHandlingTimeMicroseconds);
            }

            methodStats.TotalSize += sizeDec;
            allTimeMethodStats.TotalSize += sizeDec;
        }
    }

    private const string ReportHeader = "method                                  | " +
                                        "successes | " +
                                        "  avg (ms) | " +
                                        "  max (ms) | " +
                                        "   errors | " +
                                        "  avg (ms) | " +
                                        "  max (ms) |" +
                                        " avg size B |" +
                                        " total (kB) |";

    private static readonly string _divider = new('-', ReportHeader.Length);

    private void BuildReport()
    {
        DateTime thisTime = _timestamper.UtcNow;
        if (thisTime - _lastReport <= _reportingInterval)
        {
            return;
        }

        lock (_logger.UnderlyingLogger)
        {
            if (thisTime - _lastReport <= _reportingInterval)
            {
                return;
            }

            _lastReport = thisTime;

            Swap();

            if (_previousStats.IsEmpty)
            {
                return;
            }

            _reportStringBuilder.AppendLine("***** JSON RPC report *****");
            _reportStringBuilder.AppendLine(_divider);
            _reportStringBuilder.AppendLine(ReportHeader);
            _reportStringBuilder.AppendLine(_divider);
            MethodStats total = new();
            foreach (KeyValuePair<string, MethodStats> methodStats in _previousStats.OrderBy(static kv => kv.Key))
            {
                total.AvgTimeOfSuccesses = total.Successes + methodStats.Value.Successes == 0
                    ? 0
                    : (total.AvgTimeOfSuccesses * total.Successes + methodStats.Value.Successes * methodStats.Value.AvgTimeOfSuccesses)
                      / (total.Successes + methodStats.Value.Successes);
                total.AvgTimeOfErrors = total.Errors + methodStats.Value.Errors == 0
                    ? 0
                    : (total.AvgTimeOfErrors * total.Errors + methodStats.Value.Errors * methodStats.Value.AvgTimeOfErrors)
                      / (total.Errors + methodStats.Value.Errors);
                total.Successes += methodStats.Value.Successes;
                total.Errors += methodStats.Value.Errors;
                total.MaxTimeOfSuccess = Math.Max(total.MaxTimeOfSuccess, methodStats.Value.MaxTimeOfSuccess);
                total.MaxTimeOfError = Math.Max(total.MaxTimeOfError, methodStats.Value.MaxTimeOfError);
                total.TotalSize += methodStats.Value.TotalSize;
                _reportStringBuilder.AppendLine(PrepareReportLine(methodStats.Key, methodStats.Value));
            }

            _reportStringBuilder.AppendLine(_divider);
            _reportStringBuilder.AppendLine(PrepareReportLine("TOTAL", total));
            _reportStringBuilder.AppendLine(_divider);

            _logger.Info(_reportStringBuilder.ToString());
            _reportStringBuilder.Clear();
            _previousStats.Clear();
        }
    }

    private void Swap()
    {
        (_currentStats, _previousStats) = (_previousStats, _currentStats);
    }

    [Pure]
    private static string PrepareReportLine(in string key, MethodStats methodStats) =>
        $"{key,-40}| " +
        $"{methodStats.Successes,9} | " +
        $"{((double)methodStats.AvgTimeOfSuccesses / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.MaxTimeOfSuccess / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{methodStats.Errors,9} | " +
        $"{((double)methodStats.AvgTimeOfErrors / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.MaxTimeOfError / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),10} | " +
        $"{methodStats.AvgSize.ToString("0", CultureInfo.InvariantCulture),10} | " +
        $"{((double)methodStats.TotalSize / 1024.0).ToString("0.00", CultureInfo.InvariantCulture),10} | ";
}
