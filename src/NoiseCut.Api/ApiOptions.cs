namespace NoiseCut.Api;

public sealed class ApiOptions
{
    public const string Section = "Api";

    /// <summary>The simulator's internal control API. Only the Api talks to it; it is never exposed publicly.</summary>
    public string SimulatorUrl { get; set; } = "http://localhost:8081";

    /// <summary>Shared HMAC secret for <c>POST /sinks/webhook</c>. Empty → the sink refuses everything.</summary>
    public string WebhookSecret { get; set; } = "";

    public string RepositoryUrl { get; set; } = "https://github.com/Kar6677thik/noisecut";

    /// <summary>Link to Grafana, shown on the About page once observability lands (M2).</summary>
    public string? GrafanaUrl { get; set; }

    public AdminOptions Admin { get; set; } = new();
}

/// <summary>
/// M1 admin access (ADR-0010 is implemented in full in M4): an <c>X-Admin-Key</c> header, or a local-development bypass.
/// The bypass exists so the local stack is usable without login; it must never be on in a deployed environment.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>Header key for scripts. Empty = header access disabled.</summary>
    public string Key { get; set; } = "";

    /// <summary>PBKDF2 hash of the admin password (see <see cref="Security.PasswordHash"/>). Empty = browser login disabled.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Mark the admin cookie Secure (HTTPS only). On in deployment, where visitors reach us through Cloudflare's HTTPS.</summary>
    public bool SecureCookies { get; set; } = true;

    public bool LocalBypass { get; set; }
}
