using System.Text;
using NoiseCut.Infrastructure.Webhooks;

namespace NoiseCut.IntegrationTests;

public class WebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"alertKey":"a:1:r-017:2026-10-17T19:33:00Z"}""");

    [Fact]
    public void A_signature_verifies_with_the_same_secret()
    {
        var header = WebhookSignature.Compute("s3cret", Body);

        Assert.StartsWith("sha256=", header, StringComparison.Ordinal);
        Assert.True(WebhookSignature.Verify("s3cret", Body, header));
    }

    [Fact]
    public void A_different_secret_or_a_modified_body_fails()
    {
        var header = WebhookSignature.Compute("s3cret", Body);

        Assert.False(WebhookSignature.Verify("other", Body, header));
        Assert.False(WebhookSignature.Verify("s3cret", Encoding.UTF8.GetBytes("""{"alertKey":"forged"}"""), header));
        Assert.False(WebhookSignature.Verify("s3cret", Body, null));
        Assert.False(WebhookSignature.Verify("s3cret", Body, "sha256=00"));
    }
}
