using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;

namespace NoiseCut.Notifier;

/// <summary>
/// Email over SMTP (SPEC §7.4) via MailKit. Locally it goes to Mailpit, which catches everything and shows it in a web inbox
/// (http://localhost:8025). The Message-Id is derived from the notification key, so a resent email is recognisable as the
/// same message, which is the email equivalent of the webhook's Idempotency-Key.
/// </summary>
public sealed class EmailChannel(IOptions<NotifierOptions> options) : IChannel
{
    public string Name => "email";

    public async Task SendAsync(DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var email = options.Value.Email;
        using var doc = JsonDocument.Parse(delivery.Payload);
        var root = doc.RootElement;
        var title = (root.TryGetProperty("title", out var t) ? t.GetString() : null) ?? delivery.Kind;
        var detail = (root.TryGetProperty("detail", out var d) ? d.GetString() : null) ?? "";

        var message = new MimeMessage
        {
            Subject = title,
            MessageId = $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(delivery.NotificationKey)))[..32]}@noisecut.local",
            Body = new TextPart("plain") { Text = $"{title}\n\n{detail}\n\nAlert: {delivery.AlertKey}\n" },
        };
        message.From.Add(MailboxAddress.Parse(email.From));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Headers.Add("X-NoiseCut-Notification-Key", delivery.NotificationKey);

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(email.SmtpHost, email.SmtpPort, email.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(email.Username))
        {
            await smtp.AuthenticateAsync(email.Username, email.Password ?? "", ct);
        }

        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);
    }
}
