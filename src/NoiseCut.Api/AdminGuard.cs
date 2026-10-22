using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NoiseCut.Api;

/// <summary>
/// Decides whether a request may perform admin actions. Everything not admin is read-only (ADR-0010). Three ways in:
/// <list type="bullet">
/// <item>a signed-in admin cookie (browser, via <c>/admin/login</c>);</item>
/// <item>the <c>X-Admin-Key</c> header (scripts and the experiment harness);</item>
/// <item>the local-development bypass (Compose only; never set in a deployment).</item>
/// </list>
/// </summary>
public sealed class AdminGuard(IOptions<ApiOptions> options)
{
    public const string HeaderName = "X-Admin-Key";
    public const string Role = "admin";

    public bool BypassEnabled => options.Value.Admin.LocalBypass;

    public bool IsAdmin(HttpContext context)
    {
        var admin = options.Value.Admin;
        if (admin.LocalBypass || context.User.IsInRole(Role))
        {
            return true;
        }

        if (string.IsNullOrEmpty(admin.Key) || !context.Request.Headers.TryGetValue(HeaderName, out var provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(admin.Key), Encoding.UTF8.GetBytes(provided.ToString()));
    }
}

/// <summary>Endpoint filter for admin-only minimal API routes.</summary>
public sealed class AdminOnlyFilter(AdminGuard guard) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        guard.IsAdmin(context.HttpContext) ? await next(context) : Results.Unauthorized();
}
