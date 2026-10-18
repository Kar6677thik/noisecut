using System.Text.Json;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Core.Tests;

public class RuleValidationTests
{
    public static TheoryData<string> ReferenceRuleIds => new(ReferenceRules.All.Select(r => r.Id));

    [Theory]
    [MemberData(nameof(ReferenceRuleIds))]
    public void Reference_rules_are_valid(string id) =>
        Assert.Empty(RuleValidator.Validate(ReferenceRules.All.Single(r => r.Id == id)));

    [Theory]
    [MemberData(nameof(ReferenceRuleIds))]
    public void Reference_rules_survive_a_json_round_trip(string id)
    {
        var rule = ReferenceRules.All.Single(r => r.Id == id);
        var json = JsonSerializer.Serialize(rule, NoiseCutJson.Options);
        var back = JsonSerializer.Deserialize<RuleDefinition>(json, NoiseCutJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, NoiseCutJson.Options));
        Assert.Empty(RuleValidator.Validate(back));
    }

    [Fact]
    public void Parses_rule_json_in_the_spec_format()
    {
        const string json = """
            {
              "id": "restaurant-delay-burst", "name": "Restaurant delay burst", "kind": "windowed_count",
              "severity": "high", "entity": "restaurantId",
              "source": { "eventType": "order.delayed", "where": [ { "field": "data.delayMinutes", "op": "gt", "value": 15 } ] },
              "distinctBy": "orderId",
              "window": { "type": "sliding", "size": "PT10M", "slide": "PT1M" },
              "condition": { "op": "gte", "value": 5 },
              "notify": { "channels": ["dashboard", "webhook"], "cooldown": "PT15M", "groupBy": "zoneId", "groupWait": "PT30S",
                          "escalation": [ { "after": "PT10M", "channels": ["email"] } ] }
            }
            """;

        var rule = JsonSerializer.Deserialize<RuleDefinition>(json, NoiseCutJson.Options)!;

        Assert.Equal(RuleKind.WindowedCount, rule.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), rule.Window!.Size);
        Assert.Equal(TimeSpan.FromSeconds(30), rule.Notify.GroupWait);
        Assert.Empty(RuleValidator.Validate(rule));
    }

    [Fact]
    public void Writes_snake_case_kinds_and_iso_durations()
    {
        var json = JsonSerializer.Serialize(ReferenceRules.CancellationSpike, NoiseCutJson.Options);

        Assert.Contains("\"kind\":\"ratio_vs_baseline\"", json, StringComparison.Ordinal);
        Assert.Contains("\"op\":\"gte_multiple\"", json, StringComparison.Ordinal);
        Assert.Contains("\"offset\":\"P7D\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_bad_id() =>
        AssertInvalid(ReferenceRules.DelayBurst with { Id = "Delay Burst!" }, "id:");

    [Fact]
    public void Rejects_a_sliding_window_without_a_slide() =>
        AssertInvalid(ReferenceRules.DelayBurst with { Window = new WindowSpec { Type = WindowType.Sliding, Size = TimeSpan.FromMinutes(10) } }, "window.slide");

    [Fact]
    public void Rejects_a_size_that_is_not_a_multiple_of_the_slide() =>
        AssertInvalid(
            ReferenceRules.DelayBurst with { Window = new WindowSpec { Type = WindowType.Sliding, Size = TimeSpan.FromMinutes(10), Slide = TimeSpan.FromMinutes(3) } },
            "multiple of slide");

    [Fact]
    public void Rejects_windows_that_are_not_whole_panes() =>
        AssertInvalid(
            ReferenceRules.DelayBurst with { Window = new WindowSpec { Type = WindowType.Sliding, Size = TimeSpan.FromSeconds(90), Slide = TimeSpan.FromSeconds(90) } },
            "whole number of minutes");

    [Fact]
    public void Rejects_typos_in_field_names()
    {
        var source = ReferenceRules.DelayBurst.Source! with
        {
            Where = [ReferenceRules.DelayBurst.Source!.Where[0] with { Field = "data.delayMins" }],
        };
        AssertInvalid(ReferenceRules.DelayBurst with { Source = source }, "does not exist on order.delayed");
    }

    [Fact]
    public void Rejects_unknown_event_types() =>
        AssertInvalid(ReferenceRules.DelayBurst with { Source = new EventSelector { EventType = "order.exploded" } }, "unknown event type");

    [Fact]
    public void Rejects_non_monotonic_count_conditions() =>
        AssertInvalid(ReferenceRules.DelayBurst with { Condition = new ConditionSpec { Op = ConditionOp.GteMultiple, Value = 2 } }, "gt/gte");

    [Fact]
    public void Rejects_unknown_channels() =>
        AssertInvalid(ReferenceRules.DelayBurst with { Notify = new NotifySpec { Channels = ["pager"] } }, "unknown channel 'pager'");

    [Fact]
    public void Rejects_escalation_steps_out_of_order() =>
        AssertInvalid(
            ReferenceRules.DelayBurst with
            {
                Notify = new NotifySpec
                {
                    Channels = ["dashboard"],
                    Escalation =
                    [
                        new EscalationStep { After = TimeSpan.FromMinutes(10), Channels = ["email"] },
                        new EscalationStep { After = TimeSpan.FromMinutes(5), Channels = ["email"] },
                    ],
                },
            },
            "strictly increasing");

    [Fact]
    public void Rejects_ratio_rules_that_are_not_per_zone_and_hourly()
    {
        var errors = RuleValidator.Validate(ReferenceRules.CancellationSpike with
        {
            Entity = "restaurantId",
            Window = new WindowSpec { Type = WindowType.Tumbling, Size = TimeSpan.FromMinutes(30) },
        });

        Assert.Contains(errors, e => e.Contains("per zone", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("PT1H", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_absence_rules_without_a_timeout() =>
        AssertInvalid(ReferenceRules.StuckPickup with { Timeout = null }, "timeout");

    private static void AssertInvalid(RuleDefinition rule, string expected)
    {
        var errors = RuleValidator.Validate(rule);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }
}
