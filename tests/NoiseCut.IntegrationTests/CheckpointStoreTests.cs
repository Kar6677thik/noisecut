using System.Text.Json.Nodes;
using NoiseCut.Infrastructure.Postgres;

namespace NoiseCut.IntegrationTests;

public class CheckpointStoreTests(PostgresFixture postgres)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_fresh_partition_has_no_checkpoint_and_starts_at_epoch_one()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var store = new CheckpointStore(db);

        var cp = await store.AcquireAsync("order", "order-events", 3, "engine-a", Ct);

        Assert.Equal(new Checkpoint(1, null, null), cp);
    }

    [Fact]
    public async Task State_and_offset_are_saved_together_and_handed_to_the_next_owner()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var store = new CheckpointStore(db);
        var a = await store.AcquireAsync("order", "order-events", 0, "engine-a", Ct);

        var fenced = await store.SaveAsync("order", "order-events",
            [new CheckpointWrite(0, a.Epoch, 918244, DateTimeOffset.UtcNow, new JsonObject { ["v"] = 1, ["n"] = 42 })], Ct);
        var b = await store.AcquireAsync("order", "order-events", 0, "engine-b", Ct);

        Assert.Empty(fenced);
        Assert.Equal(2, b.Epoch);
        Assert.Equal(918244, b.NextOffset);
        Assert.Equal(42, b.State!["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_zombie_with_an_old_epoch_cannot_overwrite_the_new_owners_checkpoint()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var store = new CheckpointStore(db);
        var zombie = await store.AcquireAsync("order", "order-events", 1, "engine-a", Ct);
        var owner = await store.AcquireAsync("order", "order-events", 1, "engine-b", Ct); // rebalance: b takes over

        await store.SaveAsync("order", "order-events", [new CheckpointWrite(1, owner.Epoch, 500, null, new JsonObject { ["by"] = "b" })], Ct);
        var fenced = await store.SaveAsync("order", "order-events", [new CheckpointWrite(1, zombie.Epoch, 100, null, new JsonObject { ["by"] = "a" })], Ct);

        Assert.Equal([1], fenced);
        var now = await store.AcquireAsync("order", "order-events", 1, "engine-c", Ct);
        Assert.Equal((500L, "b"), (now.NextOffset!.Value, now.State!["by"]!.GetValue<string>()));
    }
}
