using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NoiseCut.Migrator;

/// <summary>
/// A deliberately tiny migration runner, written by hand so there is no magic to learn.
/// <list type="number">
/// <item>Take a PostgreSQL advisory lock, so two migrators starting together can't both apply the same script.</item>
/// <item>Read the journal (<c>schema_migrations</c>) to see which scripts already ran.</item>
/// <item>Apply each new embedded <c>Migrations/*.sql</c> file in name order, each in its own transaction with its journal row.</item>
/// </list>
/// Because the script and its journal row commit together, a crash mid-way leaves no half-applied migration.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource db, ILogger logger)
{
    // Any constant works; it just has to be the same for every migrator process.
    private const long AdvisoryLockId = 0x4E6F697365437574; // "NoiseCut"

    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@AdvisoryLockId)", new { AdvisoryLockId });
        try
        {
            await conn.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    script_name text PRIMARY KEY,
                    applied_at  timestamptz NOT NULL DEFAULT now()
                )
                """);

            var applied = (await conn.QueryAsync<string>("SELECT script_name FROM schema_migrations")).ToHashSet(StringComparer.Ordinal);
            var newlyApplied = new List<string>();

            foreach (var (name, sql) in LoadScripts())
            {
                if (applied.Contains(name))
                {
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations (script_name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);

                logger.LogInformation("Applied migration {Script}", name);
                newlyApplied.Add(name);
            }

            return newlyApplied;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@AdvisoryLockId)", new { AdvisoryLockId });
        }
    }

    public static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        const string prefix = "NoiseCut.Migrator.Migrations.";
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[prefix.Length..], reader.ReadToEnd());
        }
    }
}
