using Microsoft.Extensions.Time.Testing;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

public class RuleEngineTests
{
    [Fact]
    public void A_new_rule_version_starts_with_fresh_state()
    {
        var engine = Engine(rules: ReferenceRules.DelayBurst);
        engine.FeedAll(Enumerable.Range(1, 4).Select(i => Delayed($"o-{i}", At(i))));

        engine.SetRules([Rule(ReferenceRules.DelayBurst, version: 2)]);
        var output = engine.Feed(Delayed("o-5", At(5)));

        // Version 1 had four orders; version 2 has seen only one.
        Assert.Empty(output);
    }

    [Fact]
    public void The_same_version_keeps_its_state()
    {
        var engine = Engine(rules: ReferenceRules.DelayBurst);
        engine.FeedAll(Enumerable.Range(1, 4).Select(i => Delayed($"o-{i}", At(i))));

        Assert.False(engine.SetRules([Rule(ReferenceRules.DelayBurst)]));
        Assert.Single(engine.Feed(Delayed("o-5", At(5))));
    }

    [Fact]
    public void Disabled_rules_do_not_run()
    {
        var engine = Engine(rules: ReferenceRules.DelayBurst);

        engine.SetRules([Rule(ReferenceRules.DelayBurst with { Enabled = false })]);

        Assert.Empty(engine.ActiveRules);
        Assert.Empty(engine.FeedAll(Enumerable.Range(1, 5).Select(i => Delayed($"o-{i}", At(i)))));
    }

    [Fact]
    public void The_watermark_trails_by_max_out_of_orderness_and_never_goes_back()
    {
        var engine = new RuleEngine(
            new EvaluationSettings { MaxOutOfOrderness = TimeSpan.FromSeconds(30) },
            NoBaseline.Instance,
            new FakeTimeProvider(T0),
            "test");

        engine.Feed(Tick(At(10)));
        Assert.Equal(At(9.5), engine.Watermark);

        engine.Feed(Tick(At(5)));
        Assert.Equal(At(9.5), engine.Watermark);
    }

    [Fact]
    public void Candidates_carry_record_and_engine_metadata()
    {
        var wall = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 14, 13, 0, TimeSpan.Zero));
        var engine = new RuleEngine(new EvaluationSettings(), NoBaseline.Instance, wall, "engine-a");
        engine.SetRules([Rule(ReferenceRules.DelayBurst, version: 3)]);
        engine.FeedAll(Enumerable.Range(1, 4).Select(i => Delayed($"o-{i}", At(i))));

        var produced = wall.GetUtcNow().AddMilliseconds(-200);
        var last = Delayed("o-5", At(5));
        var candidate = engine.Process(last, last.OccurredAt, new RecordInfo(last.EventId, produced, 4, 918244)).Single();

        Assert.Equal(3, candidate.RuleVersion);
        Assert.StartsWith("restaurant-delay-burst:3:", candidate.AlertKey, StringComparison.Ordinal);
        Assert.Equal(Severity.High, candidate.Severity);
        Assert.Equal(produced, candidate.TriggerProducedAt);
        Assert.Equal(wall.GetUtcNow(), candidate.DetectedAt);
        Assert.Equal(new EngineInfo("engine-a", "order", 4, 918244), candidate.Engine);
    }
}
