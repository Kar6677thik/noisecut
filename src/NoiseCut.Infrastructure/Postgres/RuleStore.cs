using System.Text.Json;
using Dapper;
using Npgsql;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Infrastructure.Postgres;

/// <summary>Reads rules from <c>rules</c>. Rows that fail to parse or validate are skipped and reported, never half-applied.</summary>
public sealed class RuleStore(NpgsqlDataSource db)
{
    public sealed record LoadResult(IReadOnlyList<Rule> Rules, IReadOnlyList<string> Problems);

    public async Task<LoadResult> LoadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Id, int Version, bool Enabled, string Definition)>(
            new CommandDefinition("SELECT id, version, enabled, definition::text FROM rules ORDER BY id", cancellationToken: ct));

        var rules = new List<Rule>();
        var problems = new List<string>();
        foreach (var row in rows)
        {
            RuleDefinition? def;
            try
            {
                def = JsonSerializer.Deserialize<RuleDefinition>(row.Definition, NoiseCutJson.Options);
            }
            catch (JsonException ex)
            {
                problems.Add($"{row.Id} v{row.Version}: {ex.Message}");
                continue;
            }

            if (def is null)
            {
                continue;
            }

            // The enabled column is the switch the Api flips; the JSON copy follows it.
            def = def with { Id = row.Id, Enabled = row.Enabled };
            var errors = RuleValidator.Validate(def);
            if (errors.Count > 0)
            {
                problems.Add($"{row.Id} v{row.Version}: {string.Join("; ", errors)}");
                continue;
            }

            rules.Add(new Rule(def, row.Version));
        }

        return new LoadResult(rules, problems);
    }

    /// <summary>Insert a rule if it does not exist yet. Used for seeding; never overwrites an edited rule.</summary>
    public static async Task<bool> InsertIfMissingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RuleDefinition def)
    {
        var json = JsonSerializer.Serialize(def, NoiseCutJson.Options);
        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO rules (id, version, enabled, definition) VALUES (@Id, 1, @Enabled, @Json::jsonb)
            ON CONFLICT (id) DO NOTHING
            """,
            new { def.Id, def.Enabled, Json = json },
            tx);
        if (inserted == 1)
        {
            await conn.ExecuteAsync(
                "INSERT INTO rule_history (id, version, definition) VALUES (@Id, 1, @Json::jsonb) ON CONFLICT DO NOTHING",
                new { def.Id, Json = json },
                tx);
        }

        return inserted == 1;
    }
}
