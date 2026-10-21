using System.Collections.Concurrent;

namespace NoiseCut.Api.Security;

/// <summary>
/// Lockout for the admin login (ADR-0010): after <c>maxFailures</c> wrong passwords from one client within the window,
/// that client is refused until the window passes, even with the right password. Combined with the per-IP rate limit on the
/// login route, online guessing becomes hopeless. In memory on purpose: one Api replica, and a restart only resets a lockout.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock, int maxFailures = 5, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);

    public bool IsLockedOut(string client)
    {
        if (!_failures.TryGetValue(client, out var f))
        {
            return false;
        }

        lock (f)
        {
            f.Prune(clock.GetUtcNow() - _window);
            return f.Times.Count >= maxFailures;
        }
    }

    public void RecordFailure(string client)
    {
        var f = _failures.GetOrAdd(client, _ => new Failures());
        lock (f)
        {
            f.Prune(clock.GetUtcNow() - _window);
            f.Times.Enqueue(clock.GetUtcNow());
        }
    }

    public void Reset(string client) => _failures.TryRemove(client, out _);

    private sealed class Failures
    {
        public Queue<DateTimeOffset> Times { get; } = new();

        public void Prune(DateTimeOffset before)
        {
            while (Times.TryPeek(out var t) && t < before)
            {
                Times.Dequeue();
            }
        }
    }
}

public static class ClientAddress
{
    /// <summary>
    /// Behind the Cloudflare tunnel every request arrives from cloudflared, so the real visitor is in <c>CF-Connecting-IP</c>.
    /// It can be spoofed by anyone who reaches the NodePort directly (LAN only): an accepted risk, see ADR-0010.
    /// </summary>
    public static string Of(HttpContext context) =>
        context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() is { Length: > 0 } cf ? cf : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
