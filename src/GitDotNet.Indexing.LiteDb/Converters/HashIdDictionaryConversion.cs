using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GitDotNet.Indexing.LiteDb.Converters;

internal sealed class HashIdDictionaryConversion : ValueConverter<Dictionary<string, HashId>, byte[]>
{
    private static JsonSerializerOptions JsonSerializerSupportingHashIdOptions { get; } = new()
    {
        Converters = { new HashIdJsonConverter() }
    };

    public HashIdDictionaryConversion()
        : base(
            v => JsonSerializer.SerializeToUtf8Bytes(v, JsonSerializerSupportingHashIdOptions),
            v => JsonSerializer.Deserialize<Dictionary<string, HashId>>(v, JsonSerializerSupportingHashIdOptions)!)
    {
    }

    private class HashIdJsonConverter : JsonConverter<HashId>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(HashId);
        public override HashId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new HashId(reader.GetBytesFromBase64());
        }
        public override void Write(Utf8JsonWriter writer, HashId value, JsonSerializerOptions options)
        {
            writer.WriteBase64StringValue(value.Hash.ToArray());
        }
    }
}