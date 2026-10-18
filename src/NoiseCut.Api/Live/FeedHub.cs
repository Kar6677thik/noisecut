using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;

namespace NoiseCut.Api.Live;

/// <summary>
/// Bridges PostgreSQL <c>LISTEN alerts_feed</c> to browsers. One database connection listens; every connected browser
/// gets its own small in-memory queue. A slow browser only drops its own messages (the queue is bounded), it never
/// blocks the listener or other browsers.
/// </summary>
public sealed class FeedHub(NpgsqlDataSource db, ILogger<FeedHub> logger) : BackgroundService
{
    public const string ChannelName = "alerts_feed";

    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Writer.TryWrite(e.Payload);
                    }
                };

                await using (var cmd = new NpgsqlCommand($"LISTEN {ChannelName}", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                logger.LogInformation("Listening on {Channel}", ChannelName);
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Blocks until a notification arrives; the Notification event above fires during this call.
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Feed listener lost its connection; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
