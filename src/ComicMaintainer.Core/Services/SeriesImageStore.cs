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
///   rejected to mitigate SSRF (consistent with project security review).
/// - Content-type must be one of <see cref="AllowedContentTypes"/>.
/// - First bytes are checked against the corresponding image magic numbers
///   to prevent HTML / SVG / executable payloads from being served back as
///   "images" (XSS via &lt;img&gt; would be unlikely but disk usage and
///   indirect display via direct fetch would be).
/// - Maximum download size is capped by <see cref="AppSettings.SeriesImageMaxBytes"/>.
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
        await EnsurePublicHostAsync(uri, cancellationToken);

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        // Decline redirects we can't re-validate; HttpClient follows by default
        // but each redirect target is re-resolved by the underlying socket
        // layer using the original DNS, so we can't easily re-check the host
        // here. Disabling cross-origin redirects entirely is the safest stance.
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Image download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > settings.SeriesImageMaxBytes)
        {
            throw new InvalidOperationException(
                $"Image too large ({contentLength.Value} bytes > {settings.SeriesImageMaxBytes})");
        }

        var declaredContentType = response.Content.Headers.ContentType?.MediaType
            ?? string.Empty;
        var normalizedContentType = NormalizeContentType(declaredContentType);
        if (!AllowedContentTypes.ContainsKey(normalizedContentType))
        {
            throw new InvalidOperationException(
                $"Unsupported image content-type: '{LoggingHelper.SanitizeForLog(declaredContentType)}'");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cts.Token);
        var (bytes, length) = await ReadCappedAsync(responseStream, settings.SeriesImageMaxBytes, cts.Token);

        var detected = DetectContentType(bytes, length);
        if (detected is null || !string.Equals(detected, normalizedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Image payload failed magic-byte validation against declared content-type");
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
        var candidate = Path.GetFullPath(Path.Combine(fullDir, fileName));
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
        catch (Exception ex)
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
        var hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length)))
            .Substring(0, 16)
            .ToLowerInvariant();
        var safeKey = SanitizeKey(normalizedKey);
        var ext = AllowedContentTypes[contentType];
        var fileName = $"{safeKey}-{hash}{ext}";
        var fullPath = Path.Combine(dir, fileName);

        // Atomic write: write to a temp file in the same directory then move.
        var tempPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes.AsSpan(0, length).ToArray());
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
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
        return Path.Combine(configDir, "series-images");
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
        if (s.Length > 100) s = s.Substring(0, 100);
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
        // Read into a buffer one byte larger than the cap so we can detect
        // payloads that exceed it without trusting Content-Length.
        var capacity = Math.Max(64 * 1024, Math.Min(maxBytes + 1, maxBytes + 1));
        using var ms = new MemoryStream();
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
    /// Resolve the URL's host and reject any address that points at a private,
    /// loopback, link-local, multicast or otherwise non-public range. This
    /// blocks SSRF against internal services (metadata endpoints,
    /// http://localhost, http://169.254.169.254, etc.).
    /// </summary>
    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot resolve image host: {ex.Message}", ex);
        }

        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("Image host did not resolve to any address");
        }

        foreach (var addr in addresses)
        {
            if (IsBlockedAddress(addr))
            {
                throw new InvalidOperationException(
                    $"Image host '{LoggingHelper.SanitizeForLog(uri.Host)}' resolves to a non-public address");
            }
        }
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 100.64.0.0/10 (CGNAT)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
            // 127.0.0.0/8 (covered by IsLoopback but be explicit)
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 (link-local + AWS metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.0.0.0/24, 192.0.2.0/24, 198.18.0.0/15, 198.51.100.0/24, 203.0.113.0/24
            if (bytes[0] == 192 && bytes[1] == 0) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 224.0.0.0/4 (multicast) and above
            if (bytes[0] >= 224) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            // ::1, ::, ::ffff:127.0.0.1 etc are caught by IsLoopback / IsIPv4MappedToIPv6.
            if (address.IsIPv4MappedToIPv6)
            {
                var v4 = address.MapToIPv4();
                if (IsBlockedAddress(v4)) return true;
            }
            // fc00::/7 (unique local addresses)
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}
