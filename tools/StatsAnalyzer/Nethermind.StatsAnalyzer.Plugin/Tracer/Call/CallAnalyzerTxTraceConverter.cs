using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.StatsAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerTxTraceConvertor : JsonConverter<CallAnalyzerTxTrace>
{
    public override CallAnalyzerTxTrace Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, CallAnalyzerTxTrace value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("initialBlockNumber"u8);
        JsonSerializer.Serialize(writer, value.InitialBlockNumber, options);
        writer.WritePropertyName("initial_block_date_time"u8);
        JsonSerializer.Serialize(writer, value.InitialBlockDateTime.ToString(), options);
        writer.WritePropertyName("currentBlockNumber"u8);
        JsonSerializer.Serialize(writer, value.CurrentBlockNumber, options);
        writer.WritePropertyName("current_block_date_time"u8);
        JsonSerializer.Serialize(writer, value.CurrentBlockDateTime.ToString(), options);

        if (value.Entries is not null)
        {
            writer.WritePropertyName("stats"u8);
            writer.WriteStartArray();
            foreach (var callStats in value.Entries)
            {

                writer.WriteStartObject();
                JsonConverter<Address> addressConverter = (JsonConverter<Address>)options.GetConverter(typeof(Address));
                writer.WritePropertyName("address"u8);
                addressConverter.Write(writer, callStats.Address, options);

                JsonConverter<Hash256> hashConverter = (JsonConverter<Hash256>)options.GetConverter(typeof(Hash256));
                writer.WritePropertyName("code_hash"u8);
                hashConverter.Write(writer, callStats.CodeHash, options);

                writer.WritePropertyName("code_size"u8);
                JsonSerializer.Serialize(writer, callStats.CodeSize, options);

                writer.WritePropertyName("count"u8);
                JsonSerializer.Serialize(writer, callStats.Count, options);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
