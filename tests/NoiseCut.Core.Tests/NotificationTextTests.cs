using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Notifications;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Tests.TestSupport;

namespace NoiseCut.Core.Tests;

public class NotificationTextTests
{
    private static AlertCandidate Candidate(string entityType, string entityKey, string groupKey, Dictionary<string, double> observed, Threshold threshold, CandidateStatus status = CandidateStatus.Firing) => new()
    {
        CandidateId = Guid.CreateVersion7(),
        AlertKey = $"x:1:{entityKey}:t",
        Status = status,
        RuleId = "x",
        RuleVersion = 1,
        Severity = Severity.High,
        EntityType = entityType,
        EntityKey = entityKey,
        GroupKey = groupKey,
        EpisodeStart = Ev.T0,
        Observed = observed,
        Threshold = threshold,
        TriggerProducedAt = Ev.T0,
        DetectedAt = Ev.T0,
        Engine = new EngineInfo("e", "order", 0, 0),
    };

    [Fact]
    public void Restaurant_alerts_name_the_restaurant()
    {
        var c = Candidate("restaurantId", "r-017", "z-02", new() { ["count"] = 6 }, new Threshold("gte", 5));

        Assert.Equal("[HIGH] Delay burst · Chaat Street Indiranagar (r-017)", NotificationText.Title(c, "Delay burst"));
        Assert.Equal("6 matching orders in the window (threshold gte 5)", NotificationText.Detail(c));
    }

    [Fact]
    public void Order_alerts_say_which_restaurant_the_order_is_at()
    {
        var c = Candidate("orderId", "o-000000196", "r-033", new() { ["waitedMinutes"] = 20 }, new Threshold("gte", 20));

        Assert.Equal("[HIGH] Stuck pickup · o-000000196 at Dosa Corner Whitefield", NotificationText.Title(c, "Stuck pickup"));
        Assert.Equal("not picked up 20 min after food was ready (threshold 20 min)", NotificationText.Detail(c));
    }

    [Fact]
    public void Ratio_alerts_show_rate_and_baseline()
    {
        var c = Candidate(
            "zoneId", "z-02", "z-02",
            new() { ["numerator"] = 36, ["denominator"] = 400, ["rate"] = 0.09, ["baselineRate"] = 0.03 },
            new Threshold("gte_multiple", 2),
            CandidateStatus.Resolved);

        Assert.Equal("[RESOLVED] Spike · Indiranagar (z-02)", NotificationText.Title(c, "Spike"));
        Assert.Equal("cancellation rate 9.0% vs baseline 3.0% (36/400 orders), threshold 2x", NotificationText.Detail(c));
    }
}
