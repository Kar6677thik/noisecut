using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.IntegrationTests;
using NoiseCut.Migrator;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace NoiseCut.IntegrationTests;

/// <summary>
/// One real PostgreSQL container for the whole test run (starting one takes a few seconds).
/// Each test class asks for its own freshly migrated database, so tests can't see each other's rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _databases;

    public async ValueTask InitializeAsync()
    {
        PostgresSetup.ConfigureDapper();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Create an empty database (no migrations applied).</summary>
    public async Task<NpgsqlDataSource> CreateEmptyDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}"; // PostgreSQL allows 63 characters
        await using (var admin = NpgsqlDataSource.Create(_container.GetConnectionString()))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>Create a database with the schema and seed data, exactly as the Migrator would on deploy.</summary>
    public async Task<NpgsqlDataSource> CreateMigratedDatabaseAsync()
    {
        var db = await CreateEmptyDatabaseAsync();
        await new MigrationRunner(db, NullLogger.Instance).RunAsync(CancellationToken.None);
        await new Seeder(db, TimeProvider.System, NullLogger.Instance).RunAsync(new Seeder.Options(300, 7, 7), CancellationToken.None);
        return db;
    }
}
