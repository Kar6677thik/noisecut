using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NoiseCut.Infrastructure.Postgres;

public static class PostgresSetup
{
    private static int _initialized;

    /// <summary>Registers a pooled <see cref="NpgsqlDataSource"/> from <c>ConnectionStrings:Postgres</c>.</summary>
    public static IServiceCollection AddNoiseCutPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured");
        ConfigureDapper();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services;
    }

    /// <summary>
    /// Npgsql reads <c>timestamptz</c> as a UTC <see cref="DateTime"/>; our models use <see cref="DateTimeOffset"/>.
    /// This handler bridges the two so every query doesn't have to.
    /// </summary>
    public static void ConfigureDapper()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value.ToUniversalTime();

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            _ => throw new DataException($"cannot convert {value.GetType()} to DateTimeOffset"),
        };
    }
}
