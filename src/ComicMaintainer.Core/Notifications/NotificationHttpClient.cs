using System.Net;
using System.Text;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Notifications;

/// <summary>
/// The one way notifications reach the network, so every call has the same limits: a timeout,
/// no following of redirects, and a response body read only up to <see cref="MaxResponseBytes"/>.
/// URLs are never logged or put in an error, since some of them carry a token.
/// </summary>
public sealed class NotificationHttpClient
{
    /// <summary>The named client; its handler never follows redirects and its logging is removed.</summary>
    public const string HttpClientName = "Notifications";

    /// <summary>Limit for one call, connecting and reading the response included.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Most of a response read; longer ones are cut off.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>Most of a target's error text that is kept.</summary>
    private const int MaxErrorLength = 300;

    /// <summary>The longest wait a target's Retry-After is honoured for.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private static readonly HttpRequestOptionsKey<bool> NotificationOption = new("ComicMaintainer.Notification");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;

    public NotificationHttpClient(IHttpClientFactory httpClientFactory, TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether <paramref name="request"/> carries a notification, which is left out of the traces.</summary>
    public static bool IsNotificationRequest(HttpRequestMessage request) =>
        request.Options.TryGetValue(NotificationOption, out var isNotification) && isNotification;

    /// <summary>A request to send through <see cref="SendAsync"/>; the URL is checked first.</summary>
    /// <exception cref="NotificationNotConfiguredException"><paramref name="url"/> is not an http(s) URL.</exception>
    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            throw new NotificationNotConfiguredException("The target's URL must use http or https.");
        }

        var request = new HttpRequestMessage(method, url);
        request.Options.Set(NotificationOption, true);
        return request;
    }

    /// <summary>Adds a header holding a token, which may not contain line breaks.</summary>
    /// <exception cref="NotificationNotConfiguredException">The token contains an invalid character.</exception>
    public static void AddSecretHeader(HttpRequestMessage request, string name, string value)
    {
        // A header value has to be plain ASCII; anything else cannot be sent, so it is refused here.
        if (!value.All(char.IsAscii) || value.Any(char.IsControl) || !request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new NotificationNotConfiguredException("The target's token contains characters that cannot be sent.");
        }
    }

    /// <summary>Sends the request and returns the body of a 2xx response.</summary>
    /// <param name="request">The request; disposed by this method.</param>
    /// <param name="targetName">What to call the target in an error.</param>
    /// <param name="secrets">Values to mask in the target's error text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="NotificationDeliveryException">The target did not accept the request.</exception>
    public async Task<string> SendAsync(
        HttpRequestMessage request,
        string targetName,
        IReadOnlyCollection<string?> secrets,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var body = await ReadLimitedAsync(response, timeout.Token);

                if (response.IsSuccessStatusCode)
                {
                    return body;
                }

                throw Describe(response, body, targetName, secrets);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NotificationDeliveryException($"{targetName} did not answer within {RequestTimeout.TotalSeconds:0} seconds.", isTransient: true);
            }
            catch (HttpRequestException ex)
            {
                throw new NotificationDeliveryException($"Could not reach {targetName}: {DescribeNetworkFailure(ex)}.", isTransient: true);
            }
            catch (IOException)
            {
                // The connection dropped while the response was being read.
                throw new NotificationDeliveryException($"The connection to {targetName} was lost.", isTransient: true);
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponseBytes];
        var total = 0;
        int read;
        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private NotificationDeliveryException Describe(
        HttpResponseMessage response,
        string body,
        string targetName,
        IReadOnlyCollection<string?> secrets)
    {
        var status = (int)response.StatusCode;

        // Redirects are not followed: a token sent to one address must not be forwarded to
        // whatever address that server names.
        if (status is >= 300 and < 400)
        {
            return new NotificationDeliveryException(
                $"{targetName} answered with a redirect (HTTP {status}), which is not followed. Check the URL, including http or https.",
                isTransient: false);
        }

        var detail = CleanErrorText(body, secrets);
        var transient = status is (int)HttpStatusCode.RequestTimeout or 425 or (int)HttpStatusCode.TooManyRequests or >= 500;
        var reason = detail.Length == 0
            ? $"{targetName} answered with HTTP {status}."
            : $"{targetName} answered with HTTP {status}: {detail}";
        return new NotificationDeliveryException(reason, transient, ParseRetryAfter(response));
    }

    /// <summary>The target's own words, on one line, shortened, with any secret removed.</summary>
    private static string CleanErrorText(string body, IReadOnlyCollection<string?> secrets)
    {
        var text = body;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        text = LoggingHelper.SanitizeForLog(text).Trim();
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength] + "…";
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - _timeProvider.GetUtcNow());
        if (delay is not { } wait || wait <= TimeSpan.Zero)
        {
            return null;
        }

        return wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    /// <summary>The kind of network problem, from the error code rather than the exception's own text.</summary>
    private static string DescribeNetworkFailure(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "the host name could not be resolved",
        HttpRequestError.ConnectionError => "the connection failed",
        HttpRequestError.SecureConnectionError => "the TLS connection failed",
        HttpRequestError.ProxyTunnelError => "the proxy connection failed",
        _ => "the request failed",
    };
}
