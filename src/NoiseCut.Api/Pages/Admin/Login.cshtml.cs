using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NoiseCut.Api.Security;

namespace NoiseCut.Api.Pages.Admin;

/// <summary>
/// Single-admin login (ADR-0010): password checked against a PBKDF2 hash, per-IP lockout after repeated failures, the "login"
/// rate limit, antiforgery (Razor Pages validates the token on every POST), and a Secure + HttpOnly + SameSite=Strict cookie.
/// </summary>
[EnableRateLimiting("login")]
public sealed class LoginModel(IOptions<ApiOptions> options, LoginThrottle throttle, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public bool Enabled => !string.IsNullOrEmpty(options.Value.Admin.PasswordHash);

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var client = ClientAddress.Of(HttpContext);
        if (!Enabled)
        {
            return Page();
        }

        if (throttle.IsLockedOut(client))
        {
            Error = "Too many failed attempts. Try again later.";
            logger.LogWarning("Admin login refused for locked-out client {Client}", client);
            return Page();
        }

        // The hash check takes the same time whether the password is close or not (PBKDF2 + constant-time comparison).
        if (!PasswordHash.Verify(Password, options.Value.Admin.PasswordHash))
        {
            throttle.RecordFailure(client);
            Error = "Wrong password.";
            logger.LogWarning("Failed admin login from {Client}", client);
            return Page();
        }

        throttle.Reset(client);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, AdminGuard.Role)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        logger.LogInformation("Admin signed in from {Client}", client);

        // Only redirect to local paths: an attacker-supplied ReturnUrl must not bounce the admin to another site.
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/admin");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect("/");
    }
}
