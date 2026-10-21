// NoiseCut.Api: the dashboard (Razor Pages), JSON/SSE APIs, the webhook sink and admin forwarding to the simulator.
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NoiseCut.Api;
using NoiseCut.Api.Data;
using NoiseCut.Api.Live;
using NoiseCut.Api.Security;
using NoiseCut.Infrastructure;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

// `dotnet NoiseCut.Api.dll hash-password` reads a password from stdin and prints its hash for Api__Admin__PasswordHash.
if (args.FirstOrDefault() == "hash-password")
{
    var password = Console.In.ReadLine();
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("usage: echo <password> | dotnet NoiseCut.Api.dll hash-password");
        return 2;
    }

    Console.WriteLine(PasswordHash.Create(password));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
var profile = builder.Configuration.ApplyProfile();

builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.AddNoiseCutPostgres(builder.Configuration);
builder.Services.AddNoiseCutTelemetry(builder.Configuration, "noisecut-api");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DashboardQueries>();
builder.Services.AddSingleton<AdminGuard>();
builder.Services.AddSingleton<LoginThrottle>(sp => new LoginThrottle(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<FeedHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeedHub>());
builder.Services.AddHttpClient<SimulatorClient>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiOptions>>().Value.SimulatorUrl);
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton(new ProfileInfo(profile));
builder.Services.AddRazorPages();

// --- admin authentication (ADR-0010) ----------------------------------------------------------------------------------
var secureCookies = builder.Configuration.GetValue("Api:Admin:SecureCookies", true);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.Cookie.Name = "noisecut_admin";
        o.Cookie.HttpOnly = true;                     // not readable from JavaScript
        o.Cookie.SameSite = SameSiteMode.Strict;      // never sent on cross-site requests (CSRF defence in depth)
        o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// --- rate limiting: the showcase is public, so every visitor gets a fair share (ADR-0010) ----------------------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per client IP: 240 requests a minute for everything public. The internal webhook sink and health probes are exempt:
    // they come from inside the cluster, in bursts, and are not visitor traffic.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/sinks") || ctx.Request.Path.StartsWithSegments("/healthz") || ctx.Request.Path.StartsWithSegments("/readyz")
            ? RateLimitPartition.GetNoLimiter("internal")
            : RateLimitPartition.GetFixedWindowLimiter(ClientAddress.Of(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Login: 10 attempts a minute per IP (on top of the lockout after 5 failures).
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientAddress.Of(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
    }));

    // Live feed: at most 3 open SSE streams per IP, so one visitor can't hold hundreds of connections.
    o.AddPolicy("stream", ctx => RateLimitPartition.GetConcurrencyLimiter(ClientAddress.Of(ctx), _ => new ConcurrencyLimiterOptions
    {
        PermitLimit = 3,
        QueueLimit = 0,
    }));
});

var app = builder.Build();

if (app.Services.GetRequiredService<AdminGuard>().BypassEnabled)
{
    app.Logger.LogWarning("Admin bypass is ON: every visitor is treated as admin. Local development only.");
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapRazorPages();
app.MapNoiseCutEndpoints();

app.Run();
return 0;

/// <summary>Which config profile is active; shown in the page footer so nobody mistakes naive mode for the real thing.</summary>
public sealed record ProfileInfo(string Name);
