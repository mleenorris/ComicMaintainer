using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesImageStore"/> implementation. Downloads and
/// persists series-level cover images under a configurable cache directory.
///
/// Security:
/// - Only http and https remote URLs are accepted.
/// - Hostnames that resolve to private / loopback / link-local IPs are
///   rejected to mitigate SSRF, before the request and again when the
///   connection is made (DNS rebinding); redirects are followed here, up to
///   three, and each hop is checked the same way (see <see cref="PublicNetworkGuard"/>).
/// - Content-type must be one of <see cref="AllowedContentTypes"/>.
/// - First bytes are checked against the corresponding image magic numbers
///   to prevent HTML / SVG / executable payloads from being served back as
///   "images" (XSS via &lt;img&gt; would be unlikely but disk usage and
///   indirect display via direct fetch would be).
/// - Maximum download size is capped by <see cref="AppSettings.SeriesImageMaxDownloadBytes"/>.
///   A source image larger than <see cref="AppSettings.SeriesImageMaxBytes"/>
///   (but within that ceiling) is downscaled and re-encoded as JPEG so an
///   oversized source doesn't block the cover from being populated.
/// - Filenames are derived solely from the normalized key + a SHA-256 of the
///   payload + an extension chosen from the validated content-type. Provider
///   data never influences the on-disk filename.
/// </summary>
public class SeriesImageStore : ISeriesImageStore
{
    public const string HttpClientName = nameof(SeriesImageStore);

    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp"
        };

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    // Redirects followed per download; each hop is checked like the first URL (see SendFollowingCheckedRedirectsAsync).
    private const int MaxRedirects = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesImageStore> _logger;

    public SeriesImageStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesImageStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SeriesImageStoreResult> DownloadAsync(
        string normalizedKey,
        string remoteImageUrl,
        string? previousFile,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            throw new ArgumentException("Series key is required", nameof(normalizedKey));
        }
        if (string.IsNullOrWhiteSpace(remoteImageUrl))
        {
            throw new ArgumentException("Remote URL is required", nameof(remoteImageUrl));
        }

        var settings = _settings.CurrentValue;
        ValidateRemoteUrl(remoteImageUrl, out var uri);

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadTimeout);

        using var response = await SendFollowingCheckedRedirectsAsync(httpClient, uri, cancellationToken, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Image download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > settings.SeriesImageMaxDownloadBytes)
        {
            throw new InvalidOperationException(
                $"Image too large ({contentLength.Value} bytes > {settings.SeriesImageMaxDownloadBytes})");
        }

        var declaredContentType = response.Content.Headers.ContentType?.MediaType
            ?? string.Empty;
        var normalizedContentType = NormalizeContentType(declaredContentType);
        if (!AllowedContentTypes.ContainsKey(normalizedContentType))
        {
            throw new InvalidOperationException(
                $"Unsupported image content-type: '{LoggingHelper.SanitizeForLog(declaredContentType)}'");
        }

        // Allow fetching a source image larger than the persisted cap so we can
        // downscale it below. Only payloads exceeding the hard ceiling are
        // rejected outright.
        var downloadCeiling = Math.Max(settings.SeriesImageMaxDownloadBytes, settings.SeriesImageMaxBytes);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cts.Token);
        var (bytes, length) = await ReadCappedAsync(responseStream, downloadCeiling, cts.Token);

        var detected = DetectContentType(bytes, length);
        if (detected is null || !string.Equals(detected, normalizedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Image payload failed magic-byte validation against declared content-type");
        }

        // A large source image must not block the cover from being populated:
        // when the payload exceeds the persisted cap, downscale and re-encode
        // it as JPEG so it fits, rather than rejecting the download.
        if (length > settings.SeriesImageMaxBytes)
        {
            var scaled = SeriesImageDownscaler.DownscaleToJpeg(
                bytes.AsSpan(0, length),
                settings.SeriesImageMaxBytes,
                Math.Max(1, settings.SeriesImageMaxDimension));
            _logger.LogInformation(
                "Downscaled oversized series image for {SeriesKey} from {OriginalBytes} to {ScaledBytes} bytes",
                LoggingHelper.SanitizeForLog(normalizedKey),
                length,
                scaled.Length);
            bytes = scaled;
            length = scaled.Length;
            normalizedContentType = "image/jpeg";
        }

        return Persist(normalizedKey, bytes, length, normalizedContentType, previousFile);
    }

    public async Task<SeriesImageStoreResult> SaveUserImageAsync(
        string normalizedKey,
        Stream content,
        string declaredContentType,
        string? previousFile,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            throw new ArgumentException("Series key is required", nameof(normalizedKey));
        }
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var settings = _settings.CurrentValue;
        var normalizedContentType = NormalizeContentType(declaredContentType ?? string.Empty);
        if (!AllowedContentTypes.ContainsKey(normalizedContentType))
        {
            throw new InvalidOperationException(
                $"Unsupported image content-type: '{LoggingHelper.SanitizeForLog(declaredContentType ?? string.Empty)}'");
        }

        var (bytes, length) = await ReadCappedAsync(content, settings.SeriesImageMaxBytes, cancellationToken);

        var detected = DetectContentType(bytes, length);
        if (detected is null || !string.Equals(detected, normalizedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Uploaded image failed magic-byte validation against declared content-type");
        }

        return Persist(normalizedKey, bytes, length, normalizedContentType, previousFile);
    }

    public string? ResolveAbsolutePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        // Reject anything that looks like a path. Stored names are bare files.
        if (fileName.IndexOfAny(new[] { '/', '\\' }) >= 0
            || fileName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var dir = ResolveCacheDirectory();
        var fullDir = Path.GetFullPath(dir);
        var candidate = Path.GetFullPath(Path.Join(fullDir, fileName));
        // Defense in depth: ensure final path stays inside the cache directory.
        if (!candidate.StartsWith(EnsureTrailingSeparator(fullDir), StringComparison.Ordinal))
        {
            return null;
        }
        return File.Exists(candidate) ? candidate : null;
    }

    public void Delete(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var path = ResolveAbsolutePath(fileName);
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete cached series image {File}",
                LoggingHelper.SanitizeForLog(fileName));
        }
    }

    // ---- internals -------------------------------------------------------

    private SeriesImageStoreResult Persist(
        string normalizedKey,
        byte[] bytes,
        int length,
        string contentType,
        string? previousFile)
    {
        var dir = ResolveCacheDirectory();
        Directory.CreateDirectory(dir);

        // Filename: <normalizedKey>-<short-hash><ext>. Use SHA-256 over the
        // payload so identical images always produce the same filename, which
        // simplifies cleanup of replaced files.
        var hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length)))[..16]
            .ToLowerInvariant();
        var safeKey = SanitizeKey(normalizedKey);
        var ext = AllowedContentTypes[contentType];
        var fileName = $"{safeKey}-{hash}{ext}";
        var fullPath = Path.Join(dir, fileName);

        // Atomic write: write to a temp file in the same directory then move.
        var tempPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes.AsSpan(0, length).ToArray());
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
            throw;
        }

        // Best-effort cleanup of the previous image when it differs.
        if (!string.IsNullOrEmpty(previousFile)
            && !string.Equals(previousFile, fileName, StringComparison.Ordinal))
        {
            Delete(previousFile);
        }

        return new SeriesImageStoreResult(fileName, contentType, length);
    }

    private string ResolveCacheDirectory()
    {
        var settings = _settings.CurrentValue;
        var configured = settings.SeriesImageCacheDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        var configDir = settings.ConfigDirectory ?? "/Config";
        return Path.Join(configDir, "series-images");
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string SanitizeKey(string key)
    {
        // Defensive: NormalizeKey already returns lowercase alphanumerics with
        // dashes, but never trust the caller to pass something hostile.
        var chars = key
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray();
        var s = new string(chars).Trim('-');
        if (string.IsNullOrEmpty(s)) s = "series";
        // Cap to keep filenames sensible.
        if (s.Length > 100) s = s[..100];
        return s;
    }

    private static string NormalizeContentType(string contentType)
    {
        var trimmed = contentType.Trim();
        var semi = trimmed.IndexOf(';');
        return (semi < 0 ? trimmed : trimmed.Substring(0, semi)).Trim().ToLowerInvariant();
    }

    private static string? DetectContentType(byte[] data, int length)
    {
        if (length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }
        // RIFF....WEBP
        if (length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }
        return null;
    }

    private static async Task<(byte[] Buffer, int Length)> ReadCappedAsync(
        Stream source,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        // Count the bytes actually read and fail as soon as the running total
        // passes the cap, so oversized payloads are detected without trusting
        // Content-Length.
        // Start with a small bounded capacity so typical uploads avoid repeated
        // regrowth without pre-allocating the whole cap.
        using var ms = new MemoryStream(Math.Min(64 * 1024, Math.Max(0, maxBytes)));
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Image exceeds max size ({maxBytes} bytes)");
            }
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.GetBuffer();
        return (bytes, (int)ms.Length);
    }

    private static void ValidateRemoteUrl(string remoteImageUrl, out Uri uri)
    {
        if (!Uri.TryCreate(remoteImageUrl, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException("Image URL is not a valid absolute URL");
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Unsupported image URL scheme: '{LoggingHelper.SanitizeForLog(parsed.Scheme)}'");
        }
        uri = parsed;
    }

    /// <summary>
    /// Sends the GET for <paramref name="uri"/>, following up to <see cref="MaxRedirects"/> redirects itself: the
    /// client follows none (see <see cref="PublicNetworkGuard.CreateHandler"/>), so that every hop's URL and host are
    /// checked like the first one's. An https URL may not redirect to http.
    /// </summary>
    private static async Task<HttpResponseMessage> SendFollowingCheckedRedirectsAsync(
        HttpClient httpClient,
        Uri uri,
        CancellationToken callerToken,
        CancellationToken requestToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            await EnsurePublicHostAsync(uri, callerToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            }
            catch (HttpRequestException ex) when (FindNonPublicAddressRejection(ex) is { } rejection)
            {
                // The host resolved to a public address for the check above and to another one when connecting.
                throw new InvalidOperationException(
                    $"Image host '{LoggingHelper.SanitizeForLog(uri.Host)}' {PublicNetworkGuard.NonPublicAddressMessage}", rejection);
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            using (response)
            {
                if (redirects >= MaxRedirects)
                {
                    throw new InvalidOperationException($"Image download failed: more than {MaxRedirects} redirects");
                }

                var location = response.Headers.Location
                    ?? throw new InvalidOperationException(
                        $"Image download failed: HTTP {(int)response.StatusCode} redirect without a Location");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                ValidateRemoteUrl(next.ToString(), out var validated);
                if (uri.Scheme == Uri.UriSchemeHttps && validated.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("Image download failed: an https URL redirected to http");
                }

                uri = validated;
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static NonPublicAddressException? FindNonPublicAddressRejection(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NonPublicAddressException rejection)
            {
                return rejection;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the URL's host and reject any address that points at a private,
    /// loopback, link-local, multicast or otherwise non-public range (see
    /// <see cref="PublicNetworkGuard.IsBlockedAddress"/>), before any request is
    /// sent. This blocks SSRF against internal services (metadata endpoints,
    /// http://localhost, http://169.254.169.254, etc.) with a clear error. The
    /// client's handler checks the addresses again when it connects, which is what
    /// defeats DNS rebinding.
    /// </summary>
    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await PublicNetworkGuard.ResolvePublicAddressesAsync(uri.Host, resolver: null, cancellationToken);
        }
        catch (NonPublicAddressException ex)
        {
            throw new InvalidOperationException(
                $"Image host '{LoggingHelper.SanitizeForLog(uri.Host)}' {PublicNetworkGuard.NonPublicAddressMessage}", ex);
        }
        catch (HttpRequestException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException($"Cannot resolve image host: {ex.InnerException.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Image host did not resolve to any address", ex);
        }
    }
}
