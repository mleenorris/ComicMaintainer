using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Notifications;

/// <summary>The parts every target shares: its settings, its URL and the HTTP client.</summary>
public abstract class NotificationChannelBase : INotificationChannel
{
    private readonly IOptionsMonitor<AppSettings> _settings;

    protected NotificationChannelBase(string target, NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
    {
        Target = target;
        Http = http;
        _settings = settings;
    }

    public string Target { get; }

    protected NotificationHttpClient Http { get; }

    /// <summary>The target's current settings.</summary>
    protected NotificationTargetOptions Options => NotificationTargetOptions.For(_settings.CurrentValue, Target);

    /// <summary>What to call the target in messages.</summary>
    protected string Name => NotificationTarget.DisplayName(Target);

    public abstract Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);

    /// <summary>The target's URL, which must be set and valid.</summary>
    protected Uri RequireUrl(NotificationTargetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            throw new NotificationNotConfiguredException($"No URL is set for {Name}.");
        }

        if (!NotificationUrl.TryParse(options.Url, out var url))
        {
            throw new NotificationNotConfiguredException($"The URL set for {Name} is not an absolute http(s) URL without a user name or password.");
        }

        return url;
    }

    /// <summary>The target's token, which must be set.</summary>
    protected string RequireToken(NotificationTargetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new NotificationNotConfiguredException($"No token or API key is set for {Name}.");
        }

        return options.Token;
    }

    /// <summary>POSTs <paramref name="payload"/> as JSON and returns the response body.</summary>
    protected Task<string> PostJsonAsync(Uri url, object payload, string? secret, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        var request = NotificationHttpClient.CreateRequest(HttpMethod.Post, url);
        request.Content = JsonContent.Create(payload, payload.GetType());
        configure?.Invoke(request);
        return Http.SendAsync(request, Name, [secret], cancellationToken);
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxChars"/> characters, marking the cut.</summary>
    protected static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "…";
    }

    /// <summary>Cuts <paramref name="text"/> so its UTF-8 form fits in <paramref name="maxBytes"/>.</summary>
    protected static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        // Leaves room for the marker, which takes three bytes.
        var budget = maxBytes - 3;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }

        return text[..chars] + "…";
    }
}

/// <summary>
/// Publishes to an ntfy topic by POSTing JSON to the server's root URL.
/// See https://docs.ntfy.sh/publish/ ("Publish as JSON").
/// </summary>
public sealed class NtfyNotificationChannel : NotificationChannelBase
{
    // ntfy turns a longer message into an attachment.
    private const int MaxMessageBytes = 4096;

    public NtfyNotificationChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Ntfy, http, settings)
    {
    }

    public override Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var url = RequireUrl(options);
        if (string.IsNullOrWhiteSpace(options.Topic))
        {
            throw new NotificationNotConfiguredException("No topic is set for ntfy.");
        }

        var payload = new Dictionary<string, object>
        {
            ["topic"] = options.Topic,
            ["title"] = Truncate(message.Title, 250),
            ["message"] = TruncateUtf8(message.Body, MaxMessageBytes),
            ["priority"] = message.IsFailure ? 4 : 3,
            ["tags"] = new[] { message.IsFailure ? "warning" : "books" },
        };

        return PostJsonAsync(url, payload, options.Token, request =>
        {
            if (!string.IsNullOrWhiteSpace(options.Token))
            {
                NotificationHttpClient.AddSecretHeader(request, "Authorization", $"Bearer {options.Token}");
            }
        }, cancellationToken);
    }
}

/// <summary>
/// Pushes a message to Gotify with an application token.
/// See https://gotify.net/docs/pushmsg and the API spec (POST /message).
/// </summary>
public sealed class GotifyNotificationChannel : NotificationChannelBase
{
    public GotifyNotificationChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Gotify, http, settings)
    {
    }

    public override Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var url = NotificationUrl.Combine(RequireUrl(options), "message");
        var token = RequireToken(options);

        var payload = new Dictionary<string, object>
        {
            ["title"] = Truncate(message.Title, 250),
            ["message"] = Truncate(message.Body, 4000),
            ["priority"] = message.IsFailure ? 7 : 4,
        };

        return PostJsonAsync(url, payload, token, request => NotificationHttpClient.AddSecretHeader(request, "X-Gotify-Key", token), cancellationToken);
    }
}

/// <summary>
/// Posts to a Discord webhook. See "Execute Webhook" in Discord's webhook documentation:
/// a <c>content</c> of up to 2000 characters, and <c>allowed_mentions</c> so that series and
/// chapter names can never ping anyone.
/// </summary>
public sealed class DiscordNotificationChannel : NotificationChannelBase
{
    private const int MaxContentLength = 2000;

    public DiscordNotificationChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Discord, http, settings)
    {
    }

    public override Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var url = RequireUrl(options);

        var payload = new Dictionary<string, object>
        {
            ["username"] = "ComicMaintainer",
            ["content"] = Truncate($"**{message.Title}**\n{message.Body}", MaxContentLength),
            ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = Array.Empty<string>() },
        };

        // The URL itself is the secret here; a server echoing it back must not reveal it.
        return PostJsonAsync(url, payload, options.Url, null, cancellationToken);
    }
}

/// <summary>POSTs each notification as a JSON document to a configured URL.</summary>
public sealed class WebhookNotificationChannel : NotificationChannelBase
{
    public WebhookNotificationChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Webhook, http, settings)
    {
    }

    public override Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var url = RequireUrl(options);

        var payload = new WebhookPayload(
            "ComicMaintainer",
            message.EventType,
            message.Title,
            message.Body,
            message.IsFailure,
            message.Series,
            message.ItemCount,
            message.Items,
            message.OccurredAtUtc);

        return PostJsonAsync(url, payload, options.Url, null, cancellationToken);
    }

    /// <summary>The document a webhook receives.</summary>
    private sealed record WebhookPayload(
        string Source,
        string Event,
        string Title,
        string Message,
        bool IsFailure,
        string? Series,
        int Count,
        IReadOnlyList<string> Items,
        DateTime OccurredAt);
}

/// <summary>
/// Asks Komga to scan: <c>POST /api/v1/libraries/{id}/scan</c> with an administrator's API key
/// in the <c>X-API-Key</c> header. With no library configured, every library is scanned.
/// See Komga's OpenAPI document.
/// </summary>
public sealed class KomgaScanChannel : NotificationChannelBase
{
    public KomgaScanChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Komga, http, settings)
    {
    }

    public override async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var baseUrl = RequireUrl(options);
        var apiKey = RequireToken(options);

        var libraryIds = string.IsNullOrWhiteSpace(options.LibraryId)
            ? await ListLibrariesAsync(baseUrl, apiKey, cancellationToken)
            : [options.LibraryId];

        foreach (var libraryId in libraryIds)
        {
            var request = NotificationHttpClient.CreateRequest(
                HttpMethod.Post,
                NotificationUrl.Combine(baseUrl, $"api/v1/libraries/{Uri.EscapeDataString(libraryId)}/scan"));
            NotificationHttpClient.AddSecretHeader(request, "X-API-Key", apiKey);
            await Http.SendAsync(request, Name, [apiKey], cancellationToken);
        }
    }

    private async Task<IReadOnlyList<string>> ListLibrariesAsync(Uri baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var request = NotificationHttpClient.CreateRequest(HttpMethod.Get, NotificationUrl.Combine(baseUrl, "api/v1/libraries"));
        NotificationHttpClient.AddSecretHeader(request, "X-API-Key", apiKey);
        var body = await Http.SendAsync(request, Name, [apiKey], cancellationToken);

        List<string> ids;
        try
        {
            using var document = JsonDocument.Parse(body);
            ids = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Select(library => library.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>()
                    .Where(id => id.Length > 0)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: valid JSON of the wrong shape, such as a list of numbers
            // from something else listening at that URL.
            throw new NotificationDeliveryException("Komga's library list could not be read.", isTransient: false);
        }

        return ids.Count > 0
            ? ids
            : throw new NotificationDeliveryException("Komga has no libraries to scan.", isTransient: false);
    }
}

/// <summary>
/// Asks Kavita to scan: <c>POST /api/Library/scan-all</c>, or <c>/api/Library/scan?libraryId=</c>
/// for one library, with an Auth Key in the <c>x-api-key</c> header. See Kavita's OpenAPI
/// document and https://wiki.kavitareader.com/guides/api/.
/// </summary>
public sealed class KavitaScanChannel : NotificationChannelBase
{
    public KavitaScanChannel(NotificationHttpClient http, IOptionsMonitor<AppSettings> settings)
        : base(NotificationTarget.Kavita, http, settings)
    {
    }

    public override async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = Options;
        var baseUrl = RequireUrl(options);
        var apiKey = RequireToken(options);

        var url = string.IsNullOrWhiteSpace(options.LibraryId)
            ? NotificationUrl.Combine(baseUrl, "api/Library/scan-all")
            : new UriBuilder(NotificationUrl.Combine(baseUrl, "api/Library/scan")) { Query = $"libraryId={Uri.EscapeDataString(options.LibraryId)}" }.Uri;

        var request = NotificationHttpClient.CreateRequest(HttpMethod.Post, url);
        NotificationHttpClient.AddSecretHeader(request, "x-api-key", apiKey);
        await Http.SendAsync(request, Name, [apiKey], cancellationToken);
    }
}
