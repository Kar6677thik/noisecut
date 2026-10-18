using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoiseCut.Core.Serialization;

/// <summary>
/// One JSON configuration for everything that crosses a process boundary (Kafka records, jsonb columns, HTTP).
/// camelCase properties, snake_case enum values ("windowed_count"), ISO-8601 durations ("PT10M").
/// </summary>
public static class NoiseCutJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.Converters.Add(new IsoDurationConverter());
        options.Converters.Add(new NullableIsoDurationConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
