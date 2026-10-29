using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesImageStoreCoverageTests : IDisposable
{
    private static readonly byte[] JpegMagic =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46
    ];

    // "RIFF" + 4-byte size + "WEBP"
    private static readonly byte[] WebpMagic =
    [
        0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50
    ];

    private readonly string _root;
    private readonly string _cacheDir;

    public SeriesImageStoreCoverageTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cm-image-store-cov-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Join(_root, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
    }

    // ---- argument validation ---------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadAsync_BlankKey_ThrowsArgumentException(string key)
    {
        var (store, handler) = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DownloadAsync(key, "http://8.8.8.8/x.png", previousFile: null));

        Assert.Equal("normalizedKey", ex.ParamName);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DownloadAsync_BlankUrl_ThrowsArgumentException(string url)
    {
        var (store, handler) = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DownloadAsync("series-a", url, previousFile: null));

        Assert.Equal("remoteImageUrl", ex.ParamName);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    public async Task SaveUserImageAsync_BlankKey_ThrowsArgumentException(string key)
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveUserImageAsync(key, stream, "image/jpeg", previousFile: null));

        Assert.Equal("normalizedKey", ex.ParamName);
    }

    [Fact]
    public async Task SaveUserImageAsync_NullContent_ThrowsArgumentNullException()
    {
        var (store, _) = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.SaveUserImageAsync("series-a", null!, "image/jpeg", previousFile: null));

        Assert.Equal("content", ex.ParamName);
    }

    [Fact]
    public async Task SaveUserImageAsync_NullContentType_IsRejectedAsUnsupported()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, null!, previousFile: null));

        Assert.Contains("Unsupported image content-type: ''", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    // ---- SSRF guard ------------------------------------------------------

    [Theory]
    [InlineData("http://0.1.2.3/x.png")]
    [InlineData("http://100.64.0.1/x.png")]
    [InlineData("http://100.127.255.254/x.png")]
    [InlineData("http://172.16.0.1/x.png")]
    [InlineData("http://172.31.255.1/x.png")]
    [InlineData("http://192.0.2.10/x.png")]
    [InlineData("http://224.0.0.1/x.png")]
    [InlineData("http://240.0.0.1/x.png")]
    public async Task DownloadAsync_NonPublicIpv4Address_IsRejectedBeforeAnyRequest(string url)
    {
        var (store, handler) = CreateStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", url, previousFile: null));

        Assert.Contains("non-public address", ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://[::1]/x.png")]
    [InlineData("http://[fe80::1]/x.png")]
    [InlineData("http://[fec0::1]/x.png")]
    [InlineData("http://[ff02::1]/x.png")]
    [InlineData("http://[fc00::1]/x.png")]
    [InlineData("http://[fd12:3456::1]/x.png")]
    [InlineData("http://[::ffff:10.0.0.1]/x.png")]
    [InlineData("http://[::ffff:192.168.0.1]/x.png")]
    public async Task DownloadAsync_NonPublicIpv6Address_IsRejectedBeforeAnyRequest(string url)
    {
        var (store, handler) = CreateStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", url, previousFile: null));

        // Without OS IPv6 support the resolver narrows lookups to IPv4, so an
        // IPv6 literal resolves to no address and is rejected for that reason.
        Assert.Contains(
            Socket.OSSupportsIPv6 ? "non-public address" : "Image host did not resolve to any address",
            ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://8.8.8.8/x.png")]
    [InlineData("http://100.1.1.1/x.png")]
    [InlineData("http://100.128.0.1/x.png")]
    [InlineData("http://172.15.0.1/x.png")]
    [InlineData("http://172.32.0.1/x.png")]
    [InlineData("http://192.169.0.1/x.png")]
    [InlineData("http://192.1.2.3/x.png")]
    [InlineData("https://223.255.255.1/x.png")]
    public async Task DownloadAsync_PublicIpv4Address_PassesGuardAndIssuesRequest(string url)
    {
        var (store, handler) = CreateStore(respond: _ => NotFound());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", url, previousFile: null));

        Assert.Equal("Image download failed: HTTP 404 Not Found", ex.Message);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("image/*", handler.LastAcceptHeader);
    }

    [Theory]
    [InlineData("https://[2001:4860:4860::8888]/x.png")]
    [InlineData("http://[::ffff:8.8.8.8]/x.png")]
    public async Task DownloadAsync_PublicIpv6Address_IsOnlyRejectedWithoutIpv6Support(string url)
    {
        var (store, handler) = CreateStore(respond: _ => NotFound());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", url, previousFile: null));

        if (Socket.OSSupportsIPv6)
        {
            Assert.Equal("Image download failed: HTTP 404 Not Found", ex.Message);
            Assert.Equal(1, handler.RequestCount);
        }
        else
        {
            Assert.Equal("Image host did not resolve to any address", ex.Message);
            Assert.Equal(0, handler.RequestCount);
        }
    }

    [Fact]
    public async Task DownloadAsync_UnspecifiedAddress_IsReportedAsUnresolvable()
    {
        var (store, handler) = CreateStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://0.0.0.0/x.png", previousFile: null));

        Assert.StartsWith("Cannot resolve image host:", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_CallerCancelledDuringHostCheck_LetsTheCancellationThrough()
    {
        var (store, handler) = CreateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Not "Cannot resolve image host": a timed-out request must reach the timeout middleware (504).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null, cts.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    // ---- HTTP response handling ------------------------------------------

    [Fact]
    public async Task DownloadAsync_DeclaredContentLengthOverLimit_Throws()
    {
        var payload = Pad(CreatePng(), 4096);
        var (store, _) = CreateStore(
            s => s.SeriesImageMaxDownloadBytes = 1024,
            _ => ImageResponse(payload, "image/png"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null));

        Assert.Equal($"Image too large ({payload.Length} bytes > 1024)", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public async Task DownloadAsync_UnsupportedOrMissingContentType_Throws(string? contentType)
    {
        var (store, _) = CreateStore(respond: _ => ImageResponse(CreatePng(), contentType));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null));

        Assert.Equal($"Unsupported image content-type: '{contentType ?? string.Empty}'", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_PayloadNotMatchingDeclaredType_Throws()
    {
        var (store, _) = CreateStore(respond: _ => ImageResponse(JpegMagic, "image/png"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null));

        Assert.Contains("magic-byte validation", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [Fact]
    public async Task DownloadAsync_UndeclaredLengthBodyOverCeiling_Throws()
    {
        // The ceiling is the larger of the download cap and the persisted cap.
        var payload = Pad(CreatePng(), 300_000);
        var (store, _) = CreateStore(
            s =>
            {
                s.SeriesImageMaxDownloadBytes = 1000;
                s.SeriesImageMaxBytes = 200_000;
            },
            _ =>
            {
                var response = ImageResponse(payload, "image/png");
                response.Content.Headers.ContentLength = null;
                return response;
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null));

        Assert.Equal("Image exceeds max size (200000 bytes)", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [Fact]
    public async Task DownloadAsync_ValidImage_PersistsAndReplacesPrevious()
    {
        var png = CreatePng();
        var (store, handler) = CreateStore(respond: _ => ImageResponse(png, "image/png"));
        await using var old = new MemoryStream(JpegMagic);
        var previous = await store.SaveUserImageAsync("series-a", old, "image/jpeg", previousFile: null);

        var result = await store.DownloadAsync(
            "Series-A", "https://8.8.4.4/cover.png", previous.FileName);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(png.Length, result.SizeBytes);
        Assert.Equal($"series-a-{ShortHash(png)}.png", result.FileName);
        Assert.Equal(png, await File.ReadAllBytesAsync(store.ResolveAbsolutePath(result.FileName)!));
        Assert.Null(store.ResolveAbsolutePath(previous.FileName));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ImageOverPersistedCap_IsDownscaledToJpeg()
    {
        var png = CreateNoisyPng(200, 150);
        const int maxBytes = 20_000;
        Assert.True(png.Length > maxBytes, $"fixture should exceed the cap but was {png.Length} bytes");
        var (store, _) = CreateStore(
            s =>
            {
                s.SeriesImageMaxBytes = maxBytes;
                s.SeriesImageMaxDownloadBytes = 4 * 1024 * 1024;
                s.SeriesImageMaxDimension = 64;
            },
            _ => ImageResponse(png, "image/png"));

        var result = await store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null);

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.EndsWith(".jpg", result.FileName);
        Assert.InRange(result.SizeBytes, 1, maxBytes);
        var path = store.ResolveAbsolutePath(result.FileName);
        Assert.NotNull(path);
        using var stored = new MagickImage(path!);
        Assert.Equal(MagickFormat.Jpeg, stored.Format);
        Assert.Equal(64u, stored.Width);
        Assert.Equal(48u, stored.Height);
    }

    [Fact]
    public async Task DownloadAsync_UndecodableOversizedPayload_Throws()
    {
        // Passes the magic-byte check (PNG signature) but is not a decodable
        // image, so the downscale step that the oversize triggers must fail.
        var payload = Pad(CreatePng()[..8], 4096);
        var (store, _) = CreateStore(
            s =>
            {
                s.SeriesImageMaxBytes = 1024;
                s.SeriesImageMaxDownloadBytes = 64 * 1024;
            },
            _ => ImageResponse(payload, "image/png"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://8.8.8.8/x.png", previousFile: null));

        Assert.Contains("for downscaling", ex.Message);
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    // ---- SaveUserImageAsync / Persist -------------------------------------

    [Fact]
    public async Task SaveUserImageAsync_ContentTypeWithParameters_IsNormalized()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync(
            "series-a", stream, "  IMAGE/JPEG ; charset=binary", previousFile: null);

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.EndsWith(".jpg", result.FileName);
    }

    [Fact]
    public async Task SaveUserImageAsync_WebpPayload_PersistsWithWebpExtension()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(WebpMagic);

        var result = await store.SaveUserImageAsync("series-a", stream, "image/webp", previousFile: null);

        Assert.Equal("image/webp", result.ContentType);
        Assert.EndsWith(".webp", result.FileName);
        Assert.NotNull(store.ResolveAbsolutePath(result.FileName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task SaveUserImageAsync_WebpMagicMismatch_IsRejected(int corruptIndex)
    {
        var (store, _) = CreateStore();
        var bytes = (byte[])WebpMagic.Clone();
        bytes[corruptIndex] = 0x00;
        await using var stream = new MemoryStream(bytes);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/webp", previousFile: null));

        Assert.Contains("magic-byte validation", ex.Message);
    }

    [Fact]
    public async Task SaveUserImageAsync_TruncatedWebpHeader_IsRejected()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(WebpMagic[..11]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/webp", previousFile: null));
    }

    [Theory]
    [InlineData("!!!", "series")]
    [InlineData("--Foo Bar!--", "foo-bar")]
    [InlineData("Saga_2", "saga-2")]
    public async Task SaveUserImageAsync_SanitizesKeyIntoFileName(string key, string expectedPrefix)
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync(key, stream, "image/jpeg", previousFile: null);

        Assert.Equal($"{expectedPrefix}-{ShortHash(JpegMagic)}.jpg", result.FileName);
    }

    [Fact]
    public async Task SaveUserImageAsync_LongKey_IsTruncatedTo100Characters()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync(new string('k', 150), stream, "image/jpeg", previousFile: null);

        Assert.Equal($"{new string('k', 100)}-{ShortHash(JpegMagic)}.jpg", result.FileName);
    }

    [Fact]
    public async Task SaveUserImageAsync_IdenticalPayload_KeepsPreviousFile()
    {
        var (store, _) = CreateStore();
        await using var first = new MemoryStream(JpegMagic);
        var original = await store.SaveUserImageAsync("series-a", first, "image/jpeg", previousFile: null);

        await using var second = new MemoryStream(JpegMagic);
        var replacement = await store.SaveUserImageAsync(
            "series-a", second, "image/jpeg", previousFile: original.FileName);

        Assert.Equal(original.FileName, replacement.FileName);
        Assert.NotNull(store.ResolveAbsolutePath(replacement.FileName));
    }

    [Fact]
    public async Task SaveUserImageAsync_UnknownPreviousFile_IsIgnored()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync(
            "series-a", stream, "image/jpeg", previousFile: "does-not-exist.jpg");

        Assert.NotNull(store.ResolveAbsolutePath(result.FileName));
    }

    [Fact]
    public async Task SaveUserImageAsync_TargetPathIsDirectory_CleansUpTempFileAndThrows()
    {
        var (store, _) = CreateStore();
        var fileName = $"series-a-{ShortHash(JpegMagic)}.jpg";
        Directory.CreateDirectory(Path.Join(_cacheDir, fileName));
        await using var stream = new MemoryStream(JpegMagic);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/jpeg", previousFile: null));

        Assert.False(File.Exists(Path.Join(_cacheDir, fileName + ".tmp")));
        Assert.True(Directory.Exists(Path.Join(_cacheDir, fileName)));
    }

    // ---- cache directory resolution ----------------------------------------

    [Fact]
    public async Task CacheDirectoryWithTrailingSeparator_ResolvesStoredFiles()
    {
        var (store, _) = CreateStore(s => s.SeriesImageCacheDirectory = _cacheDir + Path.DirectorySeparatorChar);
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync("series-a", stream, "image/jpeg", previousFile: null);

        Assert.Equal(Path.Join(_cacheDir, result.FileName), store.ResolveAbsolutePath(result.FileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankCacheDirectory_FallsBackToConfigDirectory(string? configured)
    {
        var configDir = Path.Join(_root, "config");
        var (store, _) = CreateStore(s =>
        {
            s.SeriesImageCacheDirectory = configured;
            s.ConfigDirectory = configDir;
        });
        await using var stream = new MemoryStream(JpegMagic);

        var result = await store.SaveUserImageAsync("series-a", stream, "image/jpeg", previousFile: null);

        var expected = Path.Join(configDir, "series-images", result.FileName);
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, store.ResolveAbsolutePath(result.FileName));
    }

    [Fact]
    public void NullConfigDirectory_FallsBackToDefaultConfigRoot()
    {
        var (store, _) = CreateStore(s =>
        {
            s.SeriesImageCacheDirectory = null;
            s.ConfigDirectory = null!;
        });

        Assert.Null(store.ResolveAbsolutePath($"missing-{Guid.NewGuid():N}.png"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("a\\b.png")]
    [InlineData("..hidden.png")]
    [InlineData("   ")]
    public void ResolveAbsolutePath_NamesOutsideCacheDirectory_ReturnNull(string fileName)
    {
        var (store, _) = CreateStore();

        Assert.Null(store.ResolveAbsolutePath(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.png")]
    [InlineData("../outside.png")]
    public void Delete_BlankUnknownOrUnsafeName_IsNoOp(string? fileName)
    {
        var (store, _) = CreateStore();
        var outside = Path.Join(_root, "outside.png");
        File.WriteAllBytes(outside, JpegMagic);

        store.Delete(fileName);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Delete_ExistingFile_RemovesIt()
    {
        var (store, _) = CreateStore();
        await using var stream = new MemoryStream(JpegMagic);
        var result = await store.SaveUserImageAsync("series-a", stream, "image/jpeg", previousFile: null);

        store.Delete(result.FileName);

        Assert.Null(store.ResolveAbsolutePath(result.FileName));
    }

    // ---- helpers -------------------------------------------------------------

    private (SeriesImageStore Store, StubHandler Handler) CreateStore(
        Action<AppSettings>? configure = null,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var settings = new AppSettings
        {
            SeriesImageCacheDirectory = _cacheDir,
            ConfigDirectory = Path.Join(_root, "unused-config")
        };
        configure?.Invoke(settings);
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(settings);

        var handler = new StubHandler(respond ?? (_ =>
            throw new InvalidOperationException("No HTTP response configured for this test")));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(SeriesImageStore.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var store = new SeriesImageStore(factory.Object, monitor.Object, Mock.Of<ILogger<SeriesImageStore>>());
        return (store, handler);
    }

    private static HttpResponseMessage NotFound()
        => new(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" };

    private static HttpResponseMessage ImageResponse(byte[] payload, string? contentType)
    {
        var content = new ByteArrayContent(payload);
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] CreatePng()
    {
        using var image = new MagickImage(MagickColors.OrangeRed, 4, 4);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static byte[] CreateNoisyPng(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Gray, width, height);
        image.AddNoise(NoiseType.Gaussian);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static byte[] Pad(byte[] prefix, int totalLength)
    {
        var bytes = new byte[totalLength];
        prefix.CopyTo(bytes, 0);
        return bytes;
    }

    private static string ShortHash(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public string LastAcceptHeader { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastAcceptHeader = request.Headers.Accept.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
