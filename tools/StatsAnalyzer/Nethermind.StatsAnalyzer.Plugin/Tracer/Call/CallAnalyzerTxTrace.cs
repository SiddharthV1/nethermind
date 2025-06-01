// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

[JsonConverter(typeof(CallAnalyzerTxTraceConvertor))]
public class CallAnalyzerTxTrace
{
    [JsonPropertyName("initialBlockNumber")]
    public required long InitialBlockNumber { get; set; }


    [JsonPropertyName("initial_block_date_time")]
    public required string InitialBlockDateTime { get; set; }

    [JsonPropertyName("currentBlockNumber")]
    public required long CurrentBlockNumber { get; set; }

    [JsonPropertyName("current_block_date_time")]
    public required string CurrentBlockDateTime { get; set; }

    [JsonPropertyName("stats")]
    public required List<CallAnalyzerTraceEntry> Entries { get; set; }
}
