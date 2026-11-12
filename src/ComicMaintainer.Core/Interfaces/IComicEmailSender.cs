using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Low-level SMTP transport used to deliver comic emails. Abstracted so the
/// delivery pipeline can be tested without a mail server.
/// </summary>
public interface IComicEmailSender
{
    /// <summary>
    /// True when enough SMTP settings (host + from address) are configured for
    /// sending to be attempted.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a single message, throwing on failure. A failure that may go
    /// away by itself — the mail server answered with a temporary (4xx)
    /// reply, the connection dropped or timed out — is thrown as a
    /// <see cref="TransientEmailSendException"/> so the caller can try again
    /// later; anything else (a permanent 5xx rejection, bad credentials, a TLS
    /// or configuration problem) is not worth retrying.
    /// </summary>
    Task SendAsync(ComicEmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised by <see cref="IComicEmailSender.SendAsync"/> when sending failed for
/// a reason that is expected to clear up by itself, such as a temporary SMTP
/// reply (421 service unavailable, 450/451 mailbox busy, 452 insufficient
/// storage), a dropped connection or a timeout. The message was not accepted
/// and sending it again later may succeed.
/// </summary>
public sealed class TransientEmailSendException : Exception
{
    public TransientEmailSendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised by <see cref="IComicEmailService.SendTestEmailAsync"/> when the test
/// message could not be delivered: the mail server was unreachable or refused
/// the login or the message. Its message is a fixed description of the failure
/// and is safe to return to the client, unlike the transport's own exception
/// text, which can carry host names and the server's wording.
/// </summary>
public sealed class EmailTestFailedException : Exception
{
    public EmailTestFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An outgoing email with at most one comic attachment.</summary>
public record ComicEmailMessage(
    string ToAddress,
    string Subject,
    string Body,
    string? AttachmentPath = null,
    string? AttachmentFileName = null,
    string? AttachmentContentType = null);
