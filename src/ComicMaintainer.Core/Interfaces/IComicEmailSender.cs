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

    /// <summary>Sends a single message, throwing on failure.</summary>
    Task SendAsync(ComicEmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>An outgoing email with at most one comic attachment.</summary>
public record ComicEmailMessage(
    string ToAddress,
    string Subject,
    string Body,
    string? AttachmentPath = null,
    string? AttachmentFileName = null,
    string? AttachmentContentType = null);
