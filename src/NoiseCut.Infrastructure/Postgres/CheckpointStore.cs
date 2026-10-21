using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace NoiseCut.Infrastructure.Postgres;

/// <summary>What an engine gets when it takes a partition: its fencing epoch and the last checkpoint (if any).</summary>
public sealed record Checkpoint(long Epoch, long? NextOffset, JsonObject? State);

/// <summary>One partition's state to persist.</summary>
public sealed record CheckpointWrite(int Partition, long Epoch, long NextOffset, DateTimeOffset? Watermark, JsonObject State);

/// <summary>
/// Engine checkpoints in PostgreSQL (SPEC §6.3, ADR-0005). PostgreSQL, not Kafka, is the source of truth for
/// "where do I resume", because only PostgreSQL can store the offset <b>and</b> the window state in one atomic write.
///
/// Fencing: <see cref="AcquireAsync"/> bumps the partition's epoch and hands it to the new owner. <see cref="SaveAsync"/>
/// only writes rows whose epoch still matches. A process that lost a partition without noticing (a "zombie") writes nothing.
/// </summary>
public sealed class CheckpointStore(NpgsqlDataSource db)
{
    public async Task<Checkpoint> AcquireAsync(string stage, string topic, int partition, string owner, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<(long Epoch, long? NextOffset, string? State)>(new CommandDefinition(
            """
            INSERT INTO engine_checkpoints (stage, topic, partition, epoch, owner)
            VALUES (@stage, @topic, @partition, 1, @owner)
            ON CONFLICT (stage, topic, partition) DO UPDATE
                SET epoch = engine_checkpoints.epoch + 1, owner = EXCLUDED.owner, updated_at = now()
            RETURNING epoch, next_offset, state::text
            """,
            new { stage, topic, partition, owner },
            cancellationToken: ct));
        return new Checkpoint(row.Epoch, row.NextOffset, row.State is null ? null : JsonNode.Parse(row.State)!.AsObject());
    }

    /// <summary>Saves every partition in one transaction. Returns the partitions whose write was rejected because they were fenced.</summary>
    public async Task<IReadOnlyList<int>> SaveAsync(string stage, string topic, IReadOnlyList<CheckpointWrite> writes, CancellationToken ct)
    {
        if (writes.Count == 0)
        {
            return [];
        }

        var fenced = new List<int>();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var w in writes)
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE engine_checkpoints
                SET next_offset = @NextOffset, watermark = @Watermark, state = @State::jsonb, updated_at = now()
                WHERE stage = @stage AND topic = @topic AND partition = @Partition AND epoch = @Epoch
                """,
                new { stage, topic, w.Partition, w.Epoch, w.NextOffset, w.Watermark, State = w.State.ToJsonString() },
                tx,
                cancellationToken: ct));
            if (updated == 0)
            {
                fenced.Add(w.Partition);
            }
        }

        await tx.CommitAsync(ct);
        return fenced;
    }
}
