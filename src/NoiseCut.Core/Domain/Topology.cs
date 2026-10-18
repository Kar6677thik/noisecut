namespace NoiseCut.Core.Domain;

public sealed record Zone(string Id, string Name);

public sealed record Restaurant(string Id, string Name, string ZoneId);

/// <summary>
/// Fixed reference data (SPEC §4.1): 6 zones, 10 restaurants each.
/// Baked into code so the simulator, migrator and dashboard always agree, and seeded into PostgreSQL for SQL joins.
/// </summary>
public static class Topology
{
    public static IReadOnlyList<Zone> Zones { get; } =
    [
        new("z-01", "Koramangala"),
        new("z-02", "Indiranagar"),
        new("z-03", "HSR Layout"),
        new("z-04", "Whitefield"),
        new("z-05", "Jayanagar"),
        new("z-06", "Malleshwaram"),
    ];

    private static readonly string[] BaseNames =
    [
        "Udupi Grand", "Biryani House", "Dosa Corner", "Tandoor Tales", "Wok Express",
        "Pizza Point", "Chaat Street", "Kerala Kitchen", "Burger Barn", "Sweet Spot",
    ];

    // r-001..r-010 are in z-01, r-011..r-020 in z-02, and so on.
    public static IReadOnlyList<Restaurant> Restaurants { get; } =
        Enumerable.Range(1, 60)
            .Select(i =>
            {
                var zone = Zones[(i - 1) / 10];
                return new Restaurant($"r-{i:000}", $"{BaseNames[(i - 1) % 10]} {zone.Name}", zone.Id);
            })
            .ToArray();

    private static readonly Dictionary<string, Restaurant> RestaurantById =
        Restaurants.ToDictionary(r => r.Id, StringComparer.Ordinal);

    private static readonly Dictionary<string, Zone> ZoneById =
        Zones.ToDictionary(z => z.Id, StringComparer.Ordinal);

    public static Restaurant? FindRestaurant(string id) => RestaurantById.GetValueOrDefault(id);

    public static Zone? FindZone(string id) => ZoneById.GetValueOrDefault(id);

    public static IEnumerable<Restaurant> RestaurantsIn(string zoneId) =>
        Restaurants.Where(r => r.ZoneId == zoneId);

    /// <summary>Human-friendly name for any entity key (restaurant, zone or order id).</summary>
    public static string DisplayName(string entityKey) =>
        FindRestaurant(entityKey)?.Name ?? FindZone(entityKey)?.Name ?? entityKey;
}
