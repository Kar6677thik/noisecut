using System.Text.Json;
using System.Text.Json.Serialization;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Serialization;

/// <summary>Reads and writes <see cref="TimeSpan"/> as an ISO-8601 duration such as "PT10M" or "P7D".</summary>
public sealed class IsoDurationConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return IsoDuration.TryParse(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not an ISO-8601 duration (expected something like PT10M or P7D).");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(IsoDuration.Format(value));
}

public sealed class NullableIsoDurationConverter : JsonConverter<TimeSpan?>
{
    private static readonly IsoDurationConverter Inner = new();

    public override bool HandleNull => true;

    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(TimeSpan), options);

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            Inner.Write(writer, value.Value, options);
        }
    }
}
