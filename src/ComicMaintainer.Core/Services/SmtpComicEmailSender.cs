using System.Net.Sockets;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.IO;

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

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.EmailFromName ?? "ComicMaintainer", settings.EmailFromAddress!));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.Body };

        // The attachment is streamed from disk while the message is written
        // rather than copied into memory first, so it stays open until sent.
        await using var attachmentStream = string.IsNullOrWhiteSpace(message.AttachmentPath)
            ? null
            : File.OpenRead(message.AttachmentPath);
        if (attachmentStream is not null)
        {
            var contentType = ContentType.Parse(
                string.IsNullOrWhiteSpace(message.AttachmentContentType)
                    ? "application/octet-stream"
                    : message.AttachmentContentType);

            body.Attachments.Add(new MimePart(contentType)
            {
                Content = new MimeContent(attachmentStream),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = message.AttachmentFileName ?? Path.GetFileName(message.AttachmentPath)
            });
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
        try
        {
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

            // The advertised limit can change once authenticated, so it is read
            // only now. Checking it here explains the rejection before the
            // whole attachment is uploaded.
            await EnsureWithinServerLimitAsync(client, mime, cancellationToken);

            await client.SendAsync(mime, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            throw new TransientEmailSendException(DescribeTransientFailure(ex), ex);
        }

        // The server has accepted the message. Failing to say goodbye must not
        // be reported as a failed send, or the caller would send it again.
        try
        {
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "The mail server accepted the message but closing the connection to {Host} failed",
                LoggingHelper.SanitizeForLog(host));
        }

        _logger.LogInformation(
            "Sent comic email to {Recipient}",
            LoggingHelper.SanitizeForLog(message.ToAddress));
    }

    /// <summary>
    /// Fails with a clear message when the server advertises a message size
    /// limit (the SMTP SIZE extension) that the encoded message exceeds. The
    /// server would reject it with a 552 anyway, but only after the upload.
    /// </summary>
    private static async Task EnsureWithinServerLimitAsync(
        SmtpClient client,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        if ((client.Capabilities & SmtpCapabilities.Size) == 0 || client.MaxSize == 0)
        {
            return;
        }

        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        using var measure = new MeasuringStream();
        await message.WriteToAsync(options, measure, cancellationToken);
        if (measure.Length <= client.MaxSize)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The email is {FormatMegabytes(measure.Length)} once encoded, but the mail server accepts at most " +
            $"{FormatMegabytes(client.MaxSize)}. Lower the maximum email size in Settings to " +
            $"{Math.Max(1, (long)client.MaxSize / (1024 * 1024))} MB or less so books are compressed to fit.");
    }

    private static string FormatMegabytes(long bytes) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.#} MB");

    /// <summary>
    /// Whether a failure to connect, authenticate or send is worth retrying
    /// later: a temporary (4xx) SMTP reply, a dropped or refused connection, or
    /// a timeout. Permanent (5xx) replies, rejected credentials and TLS
    /// failures are configuration or recipient problems that would fail the
    /// same way again.
    /// </summary>
    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        SmtpCommandException command => IsTemporaryReply(command),

        // MailKit reports every refused login this way, keeping the server's
        // reply inside. A temporary refusal such as "454 too many login
        // attempts" is the login throttling a batch of sends runs into.
        AuthenticationException { InnerException: SmtpCommandException command } => IsTemporaryReply(command),
        AuthenticationException or SslHandshakeException => false,
        System.Security.Authentication.AuthenticationException => false,
        SmtpProtocolException or ServiceNotConnectedException => true,
        TimeoutException or SocketException or IOException => true,

        // A cancellation the caller did not ask for is MailKit's own timeout.
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    private static bool IsTemporaryReply(SmtpCommandException command) =>
        (int)command.StatusCode is >= 400 and < 500;

    private static string DescribeTransientFailure(Exception exception) => exception switch
    {
        SmtpCommandException command =>
            $"The mail server temporarily refused the message ({(int)command.StatusCode}): {command.Message}",
        AuthenticationException { InnerException: SmtpCommandException command } =>
            $"The mail server temporarily refused the login ({(int)command.StatusCode}): {command.Message}",
        OperationCanceledException or TimeoutException =>
            "The mail server did not respond in time.",
        _ => $"Could not reach the mail server: {exception.Message}",
    };
}
