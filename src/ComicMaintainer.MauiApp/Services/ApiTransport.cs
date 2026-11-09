using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>
/// Sends one request to the server and turns every failure into an <see cref="ApiException"/> with a message for the
/// user, so that nothing above this class has to know HTTP status codes or the platform's network exceptions.
/// </summary>
/// <remarks>
/// The HTTP client must not follow redirects (see <c>MauiProgram</c>). A followed redirect would carry an
/// <c>X-API-Key</c> header to wherever it points, turn a sign-in POST into a GET, and hide a proxy's single sign-on
/// page behind a successful status.
/// </remarks>
public sealed class ApiTransport
{
    /// <summary>The name of the <see cref="HttpClient"/> requests are sent with.</summary>
    public const string HttpClientName = "ComicMaintainerApi";

    /// <summary>
    /// How long to wait for a request that someone is watching a spinner for and that can be done without an answer,
    /// because an unreachable server, such as one that is only on the home network, may never give one.
    /// </summary>
    internal static readonly TimeSpan QuickRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Shown when the HTTPS connection fails on the certificate, which a user can do something about.</summary>
    internal const string UntrustedCertificateMessage =
        "This device does not trust the server's HTTPS certificate. The app cannot use a self-signed certificate: " +
        "use one from a public authority such as Let's Encrypt.";

    private const string ApiKeyHeader = "X-API-Key";

    /// <summary>A longer error body is not a message meant for people.</summary>
    private const int MaxDisplayedErrorLength = 300;

    private readonly IHttpClientFactory _httpClientFactory;

    public ApiTransport(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Sends the request and returns the body of a successful response.
    /// </summary>
    /// <param name="serverUrl">The server, in the form <see cref="ServerAddress.TryParse"/> produces.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="relativeUri">The path and query, without a leading slash, so it stays under the server's address.</param>
    /// <param name="content">The body, if any.</param>
    /// <param name="credential">What to sign the request with, if anything.</param>
    /// <param name="timeout">How long to wait, when shorter than the client's own timeout.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<string> SendAsync(
        string serverUrl,
        HttpMethod method,
        string relativeUri,
        HttpContent? content = null,
        ApiCredential? credential = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), relativeUri)) { Content = content };
        AttachCredential(request, credential);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutSource.CancelAfter(limit);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            return response.IsSuccessStatusCode ? body : throw DescribeFailure(response, body);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(ApiErrorKind.Network, "The server took too long to answer. Try again.", innerException: exception);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            throw new ApiException(
                ApiErrorKind.Network,
                IsCertificateFailure(exception)
                    ? UntrustedCertificateMessage
                    : "Could not reach the server. Check its address and this device's network connection.",
                innerException: exception);
        }
    }

    /// <summary>Reads a successful response's body, failing with a message for the user when it is not what was expected.</summary>
    public static T ParseJson<T>(string body, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            if (JsonSerializer.Deserialize(body, typeInfo) is { } value)
            {
                return value;
            }
        }
        catch (JsonException exception)
        {
            throw UnreadableResponse(exception);
        }

        throw UnreadableResponse(null);
    }

    // A value that cannot be a header (a damaged stored credential, say) counts as refused, so that it is discarded.
    // Left to propagate, it would crash the app at every start, before the user could sign in again.
    private static void AttachCredential(HttpRequestMessage request, ApiCredential? credential)
    {
        try
        {
            if (credential?.Kind == CredentialKind.Session)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
            }
            else if (credential?.Kind == CredentialKind.ApiKey)
            {
                request.Headers.Add(ApiKeyHeader, credential.Value);
            }
        }
        catch (FormatException exception)
        {
            throw new ApiException(ApiErrorKind.Unauthorized, "The saved sign-in is damaged. Sign in again.", innerException: exception);
        }
    }

    private static ApiException UnreadableResponse(Exception? cause) => new(
        ApiErrorKind.InvalidResponse,
        "The server's answer was not what this app expects. Check that the address points to a ComicMaintainer server and that the server and app are up to date.",
        innerException: cause);

    // The handler on Android reports connection problems with Java exceptions, which this code cannot name. TLS failures
    // are in javax rather than java, and one of those is what a self-signed certificate raises.
    private static bool IsConnectionFailure(Exception exception) =>
        exception is HttpRequestException or IOException or SocketException or WebException
        || HasTypeNamePrefix(exception, "Java.")
        || HasTypeNamePrefix(exception, "Javax.");

    // The handler may wrap the cause, so the whole chain is searched.
    private static bool IsCertificateFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException
                || HasTypeNamePrefix(current, "Javax.Net.Ssl.")
                || HasTypeNamePrefix(current, "Java.Security.Cert."))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTypeNamePrefix(Exception exception, string prefix) =>
        exception.GetType().FullName?.StartsWith(prefix, StringComparison.Ordinal) == true;

    private static ApiException DescribeFailure(HttpResponseMessage response, string body)
    {
        var status = response.StatusCode;
        var message = ReadErrorMessage(body);

        if ((int)status is >= 300 and < 400)
        {
            var target = response.Headers.Location is { IsAbsoluteUri: true } location ? location.Host : "another address";
            return new ApiException(
                ApiErrorKind.Redirected,
                $"The server redirected the app to {target}. Enter the final address (https:// if the server requires it). " +
                "If a sign-on page such as Authelia protects the server, let requests that carry an API key through to the app.",
                status);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => new ApiException(ApiErrorKind.Unauthorized, message ?? "The server did not accept these credentials.", status),
            HttpStatusCode.Forbidden => new ApiException(ApiErrorKind.Forbidden, message ?? "Your account does not have permission to do this.", status),
            HttpStatusCode.TooManyRequests => new ApiException(ApiErrorKind.RateLimited, message ?? "Too many requests. Wait a few minutes and try again.", status),
            HttpStatusCode.NotFound => new ApiException(ApiErrorKind.NotFound, message ?? "The server could not find that.", status),
            >= HttpStatusCode.InternalServerError => new ApiException(ApiErrorKind.Server, message ?? $"The server had a problem (HTTP {(int)status}). Try again later.", status),
            _ => new ApiException(ApiErrorKind.Rejected, message ?? $"The server refused the request (HTTP {(int)status}).", status),
        };
    }

    /// <summary>
    /// The human-readable part of an error body. The API answers with <c>{ "error": ... }</c>, sometimes with a more
    /// descriptive <c>message</c> next to it, with ProblemDetails, or with a plain string.
    /// </summary>
    private static string? ReadErrorMessage(string body)
    {
        var text = body.Trim();
        if (text.Length == 0 || text[0] == '<')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.String => Displayable(root.GetString()),
                JsonValueKind.Object => new[] { "message", "error", "detail", "title" }
                    .Select(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Displayable(value.GetString()) : null)
                    .FirstOrDefault(value => value is not null),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return Displayable(text);
        }
    }

    private static string? Displayable(string? message) =>
        string.IsNullOrWhiteSpace(message) || message.Length > MaxDisplayedErrorLength ? null : message;
}
