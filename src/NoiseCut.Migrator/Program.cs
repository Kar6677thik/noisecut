// NoiseCut.Migrator: a one-shot process. It applies SQL migrations, seeds data, then exits 0.
// Compose runs it before the services start (depends_on: service_completed_successfully); k3s runs it as a Job.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Migrator;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("Migrator");

var connectionString = configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is not set");

PostgresSetup.ConfigureDapper();
await using var db = NpgsqlDataSource.Create(connectionString);
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

// PostgreSQL may still be starting. Retry for a while instead of failing the deploy on a race.
for (var attempt = 1; ; attempt++)
{
    try
    {
        await using var probe = await db.OpenConnectionAsync(cts.Token);
        break;
    }
    catch (NpgsqlException ex) when (attempt < 30)
    {
        logger.LogWarning("PostgreSQL not reachable yet (attempt {Attempt}): {Message}", attempt, ex.Message);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
    }
}

var applied = await new MigrationRunner(db, logger).RunAsync(cts.Token);
logger.LogInformation("{Count} new migration(s) applied", applied.Count);

var seedOptions = new Seeder.Options(
    OrdersPerSimMinute: configuration.GetValue("Seed:OrdersPerSimMinute", 300.0),
    Seed: configuration.GetValue("Seed:RandomSeed", 7),
    BaselineDays: configuration.GetValue("Seed:BaselineDays", 7));
await new Seeder(db, TimeProvider.System, logger).RunAsync(seedOptions, cts.Token);

logger.LogInformation("Migrator finished");
