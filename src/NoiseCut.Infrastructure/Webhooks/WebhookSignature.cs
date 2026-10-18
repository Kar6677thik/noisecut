using System.Security.Cryptography;
using System.Text;

namespace NoiseCut.Infrastructure.Webhooks;

/// <summary>
/// <c>X-NoiseCut-Signature: sha256=&lt;hex HMAC of the raw body&gt;</c> (SPEC §7.4), the same scheme GitHub and Stripe use.
/// The sink recomputes the HMAC with the shared secret, so a forged or modified request is rejected.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-NoiseCut-Signature";
    public const string IdempotencyHeader = "Idempotency-Key";

    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, body));
        var actual = Encoding.ASCII.GetBytes(header);

        // Constant-time comparison: a normal == leaks, through timing, how many leading characters matched.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
