using Microsoft.Extensions.Time.Testing;
using NoiseCut.Api.Security;

namespace NoiseCut.IntegrationTests;

/// <summary>M4: the admin password hash and the login lockout (ADR-0010). No database needed.</summary>
public class AdminSecurityTests
{
    [Fact]
    public void A_hash_verifies_only_the_password_it_was_made_from()
    {
        var hash = PasswordHash.Create("correct horse battery staple");

        Assert.StartsWith("pbkdf2-sha256$600000$", hash, StringComparison.Ordinal);
        Assert.True(PasswordHash.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHash.Verify("correct horse battery stapl", hash));
        Assert.False(PasswordHash.Verify("", hash));
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A random salt per hash: two admins with the same password (or two deployments) never share a hash.
        Assert.NotEqual(PasswordHash.Create("pw"), PasswordHash.Create("pw"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$5$c2FsdA==$aGFzaA==")]   // too few iterations to be a real hash
    [InlineData("pbkdf2-sha256$600000$not-base64$xx")]
    [InlineData("md5$1$x$y")]
    public void Malformed_or_weak_hashes_never_verify(string? stored) => Assert.False(PasswordHash.Verify("anything", stored));

    [Fact]
    public void Five_failures_lock_a_client_out_until_the_window_passes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new LoginThrottle(clock, maxFailures: 5, window: TimeSpan.FromMinutes(15));

        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        Assert.False(throttle.IsLockedOut("203.0.113.7"));
        throttle.RecordFailure("203.0.113.7");
        Assert.True(throttle.IsLockedOut("203.0.113.7"));
        Assert.False(throttle.IsLockedOut("198.51.100.1")); // other clients are unaffected

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(throttle.IsLockedOut("203.0.113.7"));
    }
}
