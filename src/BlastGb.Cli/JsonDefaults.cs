using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BlastGb.Serialization;

namespace BlastGb.Cli;

/// <summary>The stable wire format used by both the CLI and its MCP transport.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new IntegerConverter());
        options.Converters.Add(new NumericByteArrayConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class IntegerConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonInteger.TryGetInt32(ref reader, out var value) ? value : throw new JsonException("Expected an integer within the 32-bit range.");

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class NumericByteArrayConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Bytes must be an array of integers from 0 to 255.");
            var bytes = new List<byte>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return [.. bytes];
                if (!JsonInteger.TryGetInt32(ref reader, out var value) || value is < 0 or > 255)
                    throw new JsonException("Bytes must be integers from 0 to 255.");
                bytes.Add((byte)value);
            }
            throw new JsonException("Incomplete byte array.");
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteNumberValue(item);
            writer.WriteEndArray();
        }
    }
}
