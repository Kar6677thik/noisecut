using System.Xml;

namespace NoiseCut.Core.Time;

/// <summary>ISO-8601 durations ("PT10M", "P7D"). The BCL already implements them for XML Schema, so we reuse that.</summary>
public static class IsoDuration
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text[0] != 'P')
        {
            return false;
        }

        try
        {
            value = XmlConvert.ToTimeSpan(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Format(TimeSpan value) => XmlConvert.ToString(value);
}
