using NoiseCut.Core.Domain;

namespace NoiseCut.Core.Simulation;

/// <summary>Options for starting a scenario. Every field is optional; defaults come from SPEC §9.</summary>
public sealed record ScenarioRequest
{
    /// <summary>Restaurant id (kitchen-meltdown) or zone id (the others). Picked at random when omitted.</summary>
    public string? Target { get; init; }

    /// <summary>Duration in sim minutes.</summary>
    public double? DurationMinutes { get; init; }

    /// <summary>Scenario-specific strength: cancel multiplier, or number of restaurants for alert-storm.</summary>
    public double? Intensity { get; init; }
}

/// <summary>An injected incident. This is the "ground truth" an alert should match.</summary>
public sealed record ActiveScenario(
    Guid Id,
    string Name,
    string Target,
    IReadOnlyList<string> Restaurants,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    double Intensity,
    string ExpectedRule);

public sealed class ScenarioException(string message) : Exception(message);

/// <summary>The four scenarios from SPEC §9 and the questions the simulator asks about them.</summary>
public sealed class ScenarioBook
{
    public const string KitchenMeltdown = "kitchen-meltdown";
    public const string ZoneCancelSpike = "zone-cancel-spike";
    public const string RiderShortage = "rider-shortage";
    public const string AlertStorm = "alert-storm";

    public static IReadOnlyList<string> Names { get; } = [KitchenMeltdown, ZoneCancelSpike, RiderShortage, AlertStorm];

    private readonly List<ActiveScenario> _history = [];
    private readonly Random _rng;

    public ScenarioBook(int seed) => _rng = new Random(seed);

    public IReadOnlyList<ActiveScenario> History => _history;

    public IEnumerable<ActiveScenario> ActiveAt(DateTimeOffset t) => _history.Where(s => s.StartsAt <= t && t < s.EndsAt);

    public ActiveScenario Start(string name, ScenarioRequest request, DateTimeOffset now)
    {
        var scenario = name switch
        {
            KitchenMeltdown => Meltdown(request, now),
            ZoneCancelSpike => ForZone(name, request, now, defaultMinutes: 60, defaultIntensity: 3, expectedRule: "zone-cancellation-spike"),
            RiderShortage => ForZone(name, request, now, defaultMinutes: 20, defaultIntensity: 1, expectedRule: "stuck-pickup"),
            AlertStorm => Storm(request, now),
            _ => throw new ScenarioException($"unknown scenario '{name}'. Known: {string.Join(", ", Names)}"),
        };
        _history.Add(scenario);
        return scenario;
    }

    public bool IsKitchenMeltdown(string restaurantId, DateTimeOffset t) =>
        ActiveAt(t).Any(s => s.Name is KitchenMeltdown or AlertStorm && s.Restaurants.Contains(restaurantId));

    public double CancelMultiplier(string zoneId, DateTimeOffset t) =>
        ActiveAt(t).Where(s => s.Name == ZoneCancelSpike && s.Target == zoneId).Select(s => s.Intensity).DefaultIfEmpty(1).Max();

    public bool IsRiderShortage(string zoneId, DateTimeOffset t) =>
        ActiveAt(t).Any(s => s.Name == RiderShortage && s.Target == zoneId);

    private ActiveScenario Meltdown(ScenarioRequest request, DateTimeOffset now)
    {
        var restaurant = request.Target ?? Topology.Restaurants[_rng.Next(Topology.Restaurants.Count)].Id;
        if (Topology.FindRestaurant(restaurant) is null)
        {
            throw new ScenarioException($"unknown restaurant '{restaurant}'");
        }

        var minutes = Positive(request.DurationMinutes, 15);
        return new ActiveScenario(Guid.CreateVersion7(), KitchenMeltdown, restaurant, [restaurant], now, now.AddMinutes(minutes), 1, "restaurant-delay-burst");
    }

    private ActiveScenario Storm(ScenarioRequest request, DateTimeOffset now)
    {
        var zone = ZoneOrRandom(request.Target);
        var count = (int)Math.Clamp(request.Intensity ?? 6, 1, 10);
        var restaurants = Topology.RestaurantsIn(zone).Select(r => r.Id).OrderBy(_ => _rng.Next()).Take(count).Order(StringComparer.Ordinal).ToArray();
        var minutes = Positive(request.DurationMinutes, 15);
        return new ActiveScenario(Guid.CreateVersion7(), AlertStorm, zone, restaurants, now, now.AddMinutes(minutes), count, "restaurant-delay-burst");
    }

    private ActiveScenario ForZone(string name, ScenarioRequest request, DateTimeOffset now, double defaultMinutes, double defaultIntensity, string expectedRule)
    {
        var zone = ZoneOrRandom(request.Target);
        var minutes = Positive(request.DurationMinutes, defaultMinutes);
        var intensity = Positive(request.Intensity, defaultIntensity);
        var restaurants = Topology.RestaurantsIn(zone).Select(r => r.Id).ToArray();
        return new ActiveScenario(Guid.CreateVersion7(), name, zone, restaurants, now, now.AddMinutes(minutes), intensity, expectedRule);
    }

    private string ZoneOrRandom(string? target)
    {
        var zone = target ?? Topology.Zones[_rng.Next(Topology.Zones.Count)].Id;
        return Topology.FindZone(zone) is null ? throw new ScenarioException($"unknown zone '{zone}'") : zone;
    }

    private static double Positive(double? value, double fallback) => value is > 0 ? value.Value : fallback;
}
