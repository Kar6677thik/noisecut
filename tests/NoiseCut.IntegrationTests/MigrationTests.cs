using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using NoiseCut.Migrator;

namespace NoiseCut.IntegrationTests;

public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_once_and_are_recorded_in_the_journal()
    {
        await using var db = await postgres.CreateEmptyDatabaseAsync();
        var runner = new MigrationRunner(db, NullLogger.Instance);

        var first = await runner.RunAsync(CancellationToken.None);
        var second = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(MigrationRunner.LoadScripts().Select(s => s.Name), first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Concurrent_migrators_do_not_apply_a_script_twice()
    {
        await using var db = await postgres.CreateEmptyDatabaseAsync();

        // The advisory lock makes the second runner wait, then find nothing left to do.
        var results = await Task.WhenAll(
            new MigrationRunner(db, NullLogger.Instance).RunAsync(CancellationToken.None),
            new MigrationRunner(db, NullLogger.Instance).RunAsync(CancellationToken.None));

        Assert.Equal(MigrationRunner.LoadScripts().Count(), results.Sum(r => r.Count));
    }

    [Fact]
    public async Task Seeding_is_idempotent()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await new Seeder(db, TimeProvider.System, NullLogger.Instance).RunAsync(new Seeder.Options(300, 7, 7), CancellationToken.None);

        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(6, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM zones"));
        Assert.Equal(60, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM restaurants"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM rules"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM rule_history"));
        Assert.Equal(7 * 24 * 6, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM zone_hourly_stats WHERE source = 'seed'"));
    }

    [Fact]
    public async Task Seeding_never_overwrites_an_edited_rule()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ExecuteAsync("UPDATE rules SET version = 7, enabled = false WHERE id = 'stuck-pickup'");

        await new Seeder(db, TimeProvider.System, NullLogger.Instance).RunAsync(new Seeder.Options(300, 7, 7), CancellationToken.None);

        var (version, enabled) = await conn.QuerySingleAsync<(int, bool)>("SELECT version, enabled FROM rules WHERE id = 'stuck-pickup'");
        Assert.Equal(7, version);
        Assert.False(enabled);
    }
}
