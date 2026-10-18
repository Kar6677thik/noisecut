namespace NoiseCut.Notifier;

public sealed class NotifierOptions
{
    public const string Section = "Notifier";

    /// <summary>Drop a candidate whose (alertKey, status) was already processed (SPEC §7.1).</summary>
    public bool Dedup { get; set; }

    public bool Cooldown { get; set; }

    public bool Grouping { get; set; }

    public bool Escalation { get; set; }

    public string GroupId { get; set; } = "noisecut-notifier";

    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    public WebhookOptions Webhook { get; set; } = new();

    public DispatcherOptions Dispatcher { get; set; } = new();

    /// <summary>Cooldown, grouping and escalation are M3 work. Refuse to start rather than silently ignore them.</summary>
    public void EnsureSupported()
    {
        var unsupported = new[] { (Cooldown, "Cooldown"), (Grouping, "Grouping"), (Escalation, "Escalation") }
            .Where(f => f.Item1)
            .Select(f => f.Item2)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new NotSupportedException($"Notifier:{string.Join("/", unsupported)} not implemented yet (milestone M3). Set them to false.");
        }
    }
}

public sealed class WebhookOptions
{
    /// <summary>Default: the Api's built-in sink. Point it at a Slack incoming-webhook URL (with Format=slack) to get real messages.</summary>
    public string Url { get; set; } = "http://localhost:8080/sinks/webhook";

    /// <summary>HMAC secret shared with the sink. Comes from .env / a Kubernetes Secret, never from the repo.</summary>
    public string Secret { get; set; } = "";

    /// <summary><c>generic</c> (full JSON payload) or <c>slack</c> (<c>{"text": …}</c>).</summary>
    public string Format { get; set; } = "generic";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class DispatcherOptions
{
    public int BatchSize { get; set; } = 50;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>After this many failed attempts a delivery is marked dead (SPEC §7.3).</summary>
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}
