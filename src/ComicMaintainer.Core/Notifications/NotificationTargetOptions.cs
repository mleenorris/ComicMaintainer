using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.Core.Notifications;

/// <summary>What the settings say about one notification target.</summary>
/// <param name="Target">The target, one of <see cref="NotificationTarget"/>.</param>
/// <param name="Enabled">Whether the target is switched on.</param>
/// <param name="Url">The server's base URL or, for Discord and the webhook, the full webhook URL.</param>
/// <param name="Topic">The ntfy topic.</param>
/// <param name="Token">The token or API key.</param>
/// <param name="LibraryId">The library a rescan target scans; every library when empty.</param>
/// <param name="Events">The configured events, comma-separated.</param>
public sealed record NotificationTargetOptions(
    string Target,
    bool Enabled,
    string? Url,
    string? Topic,
    string? Token,
    string? LibraryId,
    string? Events)
{
    /// <summary>The settings of <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not a known target.</exception>
    public static NotificationTargetOptions For(AppSettings settings, string target) => target switch
    {
        NotificationTarget.Ntfy => new(target, settings.NotifyNtfyEnabled, settings.NotifyNtfyUrl, settings.NotifyNtfyTopic, settings.NotifyNtfyToken, null, settings.NotifyNtfyEvents),
        NotificationTarget.Gotify => new(target, settings.NotifyGotifyEnabled, settings.NotifyGotifyUrl, null, settings.NotifyGotifyToken, null, settings.NotifyGotifyEvents),
        NotificationTarget.Discord => new(target, settings.NotifyDiscordEnabled, settings.NotifyDiscordWebhookUrl, null, null, null, settings.NotifyDiscordEvents),
        NotificationTarget.Webhook => new(target, settings.NotifyWebhookEnabled, settings.NotifyWebhookUrl, null, null, null, settings.NotifyWebhookEvents),
        NotificationTarget.Komga => new(target, settings.NotifyKomgaEnabled, settings.NotifyKomgaUrl, null, settings.NotifyKomgaApiKey, settings.NotifyKomgaLibraryId, settings.NotifyKomgaEvents),
        NotificationTarget.Kavita => new(target, settings.NotifyKavitaEnabled, settings.NotifyKavitaUrl, null, settings.NotifyKavitaApiKey, settings.NotifyKavitaLibraryId, settings.NotifyKavitaEvents),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown notification target."),
    };

    /// <summary>Whether the target is switched on and has chosen <paramref name="eventType"/>.</summary>
    public bool Receives(string eventType) => Enabled && NotificationEventType.Parse(Target, Events).Contains(eventType);
}

/// <summary>The rule every notification URL has to meet.</summary>
public static class NotificationUrl
{
    /// <summary>
    /// Parses an absolute http or https URL. Credentials in the URL are refused: nothing
    /// would send them, and they would end up wherever the URL is shown. Private addresses are
    /// fine, because Komga on a LAN is the normal setup, which is why targets can only be set
    /// by an administrator.
    /// </summary>
    /// <param name="value">The URL to check.</param>
    /// <param name="uri">The parsed URL; only meaningful when the method returns true.</param>
    public static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(parsed.Host)
            && string.IsNullOrEmpty(parsed.UserInfo)
            && (uri = parsed) is not null;
    }

    /// <summary>The URL for <paramref name="relativePath"/> under a server's base URL, which may include a sub-path.</summary>
    public static Uri Combine(Uri baseUrl, string relativePath)
    {
        var builder = new UriBuilder(baseUrl) { Query = string.Empty, Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        return builder.Uri;
    }
}
