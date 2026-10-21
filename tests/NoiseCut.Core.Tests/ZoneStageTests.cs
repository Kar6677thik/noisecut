using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Simulation;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

/// <summary>M3: the zone stage merges per-partition partials (SPEC §6.4, ADR-0004).</summary>
public class ZoneStageTests
{
    private static readonly EvaluationSettings Robust = EvaluationSettings.EventTime(EmitMode.OnTransition);
    private static readonly Rule RuleB = Rule(ReferenceRules.CancellationSpike);

    private static ZoneStageEngine ZoneStage(int sources = 6)
    {
        var stage = new ZoneStageEngine(Robust, new FixedBaseline(0.03), new FakeTimeProvider(T0), "zone-test", sources);
        stage.SetRules([RuleB]);
        return stage;
    }

    private static ZonePartial P(int source, long seq, long num, long den, double wmMinutes, string zone = "z-03", double hour = 0) => new()
    {
        RuleId = RuleB.Id,
        RuleVersion = RuleB.Version,
        ZoneId = zone,
        HourStart = At(hour * 60),
        SourcePartition = source,
        Numerator = num,
        Denominator = den,
        SourceWatermark = At(wmMinutes),
        Seq = seq,
        TriggerProducedAt = T0,
    };

    private static List<AlertCandidate> Feed(ZoneStageEngine stage, IEnumerable<ZonePartial> partials) =>
        partials.SelectMany(p => stage.Process(p, new RecordInfo(null, T0, 0, 0))).ToList();

    [Fact]
    public void Totals_are_summed_across_sources_and_fire_once_every_source_has_reported()
    {
        var stage = ZoneStage(sources: 3);

        // Each source alone has 120 orders (below the 300 guard). Together: 360 orders, 36 cancelled = 10% ≥ 2 × 3%.
        var early = Feed(stage, [P(0, 1, 12, 120, 30), P(1, 1, 12, 120, 30)]);
        Assert.Empty(early); // only 2 of 3 sources have spoken: the zone watermark can't move

        var fired = Feed(stage, [P(2, 1, 12, 120, 30)]);

        var firing = Assert.Single(fired);
        Assert.Equal("z-03", firing.EntityKey);
        Assert.Equal(360, firing.Observed["denominator"]);
        Assert.Equal("zone", firing.Engine.Stage);
    }

    [Fact]
    public void Replays_duplicates_and_stale_partials_change_nothing()
    {
        var stage = ZoneStage(sources: 2);
        Feed(stage, [P(0, 5, 10, 200, 20), P(1, 5, 10, 200, 20)]);
        var first = stage.ZoneHourCounts().Single();

        // A replayed copy (same seq) and an older one (lower seq, smaller counts) must not move the totals.
        Feed(stage, [P(0, 5, 10, 200, 21), P(0, 3, 2, 50, 21), P(1, 4, 1, 10, 21)]);

        Assert.Equal((first.Numerator, first.Denominator), (stage.ZoneHourCounts().Single().Numerator, stage.ZoneHourCounts().Single().Denominator));
        Assert.Equal((20L, 400L), (first.Numerator, first.Denominator));
    }

    [Fact]
    public void The_zone_watermark_is_the_slowest_source()
    {
        var stage = ZoneStage(sources: 2);

        Feed(stage, [P(0, 1, 0, 0, 50), P(1, 1, 0, 0, 10)]);

        Assert.Equal(At(10), stage.Watermark);
    }

    [Fact]
    public void The_hour_resolves_and_is_reported_closed_once_the_cohort_is_final()
    {
        var stage = ZoneStage(sources: 1);
        Assert.Single(Feed(stage, [P(0, 1, 40, 400, 30)]));

        // Final at hour end + 45 min cohort grace + 2 min allowed lateness = 13:47.
        Assert.Empty(Feed(stage, [P(0, 2, 40, 400, 61)]));
        Assert.Empty(Feed(stage, [P(0, 3, 40, 400, 106)]));
        var resolved = Feed(stage, [P(0, 4, 40, 400, 108)]);

        Assert.Equal(CandidateStatus.Resolved, Assert.Single(resolved).Status);
        Assert.Equal(new ClosedHour(RuleB.Id, "z-03", At(0), 40, 400), Assert.Single(stage.LastClosedHours));
    }

    [Fact]
    public void State_survives_a_checkpoint_round_trip()
    {
        var stage = ZoneStage(sources: 2);
        Feed(stage, [P(0, 1, 10, 150, 20)]);

        var restored = ZoneStage(sources: 2);
        restored.LoadState(JsonNode.Parse(stage.SaveState().ToJsonString())!.AsObject());
        var fired = Feed(restored, [P(1, 1, 10, 150, 20)]);

        Assert.Equal(300, Assert.Single(fired).Observed["denominator"]);
    }

    [Fact]
    public void Six_partitioned_engines_plus_the_zone_stage_catch_a_spike_that_each_alone_would_miss()
    {
        // Break #4 → fixed: restaurants spread over 6 partitions; each partition sees about a sixth of the zone.
        var sim = new OrderSimulation(31, new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.Zero), 80); // 16:30 IST, afternoon
        var start = sim.GeneratedUntil;
        sim.Scenarios.Start(ScenarioBook.ZoneCancelSpike, new ScenarioRequest { Target = "z-03" }, start);

        var engines = Enumerable.Range(0, 6).Select(p =>
        {
            var e = new RuleEngine(Robust, NoBaseline.Instance, new FakeTimeProvider(T0), "test", "order", EngineRole.Order, p);
            e.SetRules([RuleB]);
            return e;
        }).ToArray();
        var stage = ZoneStage();
        var candidates = new List<AlertCandidate>();
        var batch = new List<OrderEvent>();
        for (var t = start; t < start + TimeSpan.FromMinutes(70); t += TimeSpan.FromSeconds(10))
        {
            batch.Clear();
            sim.AdvanceTo(t, batch);
            foreach (var evt in batch.Append(Tick(t)))
            {
                var targets = evt.IsTick ? engines : [engines[int.Parse(evt.RestaurantId!.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture) % 6]];
                foreach (var engine in targets)
                {
                    Assert.Empty(engine.Process(evt, evt.OccurredAt, new RecordInfo(evt.EventId, T0, 0, 0)));
                    candidates.AddRange(Feed(stage, engine.LastPartials));
                }
            }
        }

        Assert.All(engines, e => Assert.True(e.ZoneHourCounts().Where(c => c.ZoneId == "z-03").Sum(c => c.Denominator) < 300));
        Assert.Contains(candidates, c => c.EntityKey == "z-03" && c.Status == CandidateStatus.Firing);
        Assert.DoesNotContain(candidates, c => c.EntityKey != "z-03" && c.Status == CandidateStatus.Firing);
    }
}
