using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerTraceEntry
{
    [JsonPropertyName("address")]
    public required Address Address { get; set; }

    [JsonPropertyName("code_hash")]
    public required Hash256 CodeHash { get; set; }

    [JsonPropertyName("code_size")]
    public required int CodeSize { get; set; }

    [JsonPropertyName("count")]
    [JsonConverter(typeof(ULongConverter))]
    public required ulong Count { get; set; }
}
