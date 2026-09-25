using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Sends comic emails through the configured SMTP server using MailKit.
/// </summary>
public class SmtpComicEmailSender : IComicEmailSender
{
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SmtpComicEmailSender> _logger;

    public SmtpComicEmailSender(IOptionsMonitor<AppSettings> settings, ILogger<SmtpComicEmailSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured
    {
        get
        {
            var settings = _settings.CurrentValue;
            return !string.IsNullOrWhiteSpace(settings.SmtpHost)
                && EmailAddressUtils.IsValid(settings.EmailFromAddress);
        }
    }

    public async Task SendAsync(ComicEmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = _settings.CurrentValue;
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Email delivery is not configured. Set the SMTP host and the from address in Settings.");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.EmailFromName ?? "ComicMaintainer", settings.EmailFromAddress));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.Body };
        if (!string.IsNullOrWhiteSpace(message.AttachmentPath))
        {
            var contentType = ContentType.Parse(
                string.IsNullOrWhiteSpace(message.AttachmentContentType)
                    ? "application/octet-stream"
                    : message.AttachmentContentType);

            await using var attachmentStream = File.OpenRead(message.AttachmentPath);
            await body.Attachments.AddAsync(
                message.AttachmentFileName ?? Path.GetFileName(message.AttachmentPath),
                attachmentStream,
                contentType,
                cancellationToken);
        }

        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();

        // Implicit TLS when requested, otherwise STARTTLS is *required* so
        // credentials and attachments are never sent in the clear. Plaintext is
        // only possible through the explicit SmtpAllowInsecure opt-in, which is
        // intended for a trusted local relay.
        var socketOptions = settings.SmtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : settings.SmtpAllowInsecure
                ? SecureSocketOptions.StartTlsWhenAvailable
                : SecureSocketOptions.StartTls;

        var host = settings.SmtpHost!;
        await client.ConnectAsync(host, settings.SmtpPort, socketOptions, cancellationToken);

        if (!client.IsSecure)
        {
            _logger.LogWarning(
                "SMTP connection to {Host} is not encrypted because SmtpAllowInsecure is enabled",
                LoggingHelper.SanitizeForLog(host));
        }

        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
        {
            await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogInformation(
            "Sent comic email to {Recipient}",
            LoggingHelper.SanitizeForLog(message.ToAddress));
    }
}
