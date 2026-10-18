using Microsoft.Extensions.Configuration;

namespace NoiseCut.Infrastructure;

/// <summary>
/// <c>NoiseCut:Profile=naive|robust</c> (SPEC §12, ADR-0008). A profile is a set of default values inserted <i>below</i>
/// every other configuration source, so any individual key (appsettings, env var) still overrides it.
/// That is what lets an experiment switch on one fix at a time.
/// </summary>
public static class Profiles
{
    public const string Naive = "naive";
    public const string Robust = "robust";

    private static readonly Dictionary<string, Dictionary<string, string?>> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        [Naive] = new()
        {
            ["Engine:StateMode"] = "InMemory",
            ["Engine:TimeMode"] = "ProcessingTime",
            ["Engine:EmitMode"] = "EveryMatch",
            ["Notifier:Dedup"] = "false",
            ["Notifier:Cooldown"] = "false",
            ["Notifier:Grouping"] = "false",
            ["Notifier:Escalation"] = "false",
        },
        [Robust] = new()
        {
            ["Engine:StateMode"] = "Checkpointed",
            ["Engine:TimeMode"] = "EventTime",
            ["Engine:EmitMode"] = "OnTransition",
            ["Notifier:Dedup"] = "true",
            ["Notifier:Cooldown"] = "true",
            ["Notifier:Grouping"] = "true",
            ["Notifier:Escalation"] = "true",
        },
    };

    /// <summary>Call first thing in Program.cs, before options are bound.</summary>
    public static string ApplyProfile(this IConfigurationManager configuration)
    {
        var profile = configuration["NoiseCut:Profile"] ?? Naive;
        if (!Presets.TryGetValue(profile, out var preset))
        {
            throw new InvalidOperationException($"Unknown NoiseCut:Profile '{profile}'. Use '{Naive}' or '{Robust}'.");
        }

        configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = preset });
        return profile.ToLowerInvariant();
    }
}
