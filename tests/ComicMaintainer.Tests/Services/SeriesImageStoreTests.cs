using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesImageStoreTests : IDisposable
{
    // Minimal valid PNG (8-byte signature + IHDR chunk for 1x1 image).
    // Magic-byte validation in SeriesImageStore only checks the first 8 bytes
    // so this is sufficient for the validation path even though it's not a
    // fully decodable image.
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D
    ];

    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46
    ];

    private readonly string _cacheDir;

    public SeriesImageStoreTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "cm-image-store-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task SaveUserImageAsync_PersistsValidPng()
    {
        var store = CreateStore();
        await using var stream = new MemoryStream(PngBytes);

        var result = await store.SaveUserImageAsync("series-a", stream, "image/png", previousFile: null);

        Assert.NotNull(result);
        Assert.Equal("image/png", result.ContentType);
        Assert.True(result.SizeBytes > 0);
        var path = store.ResolveAbsolutePath(result.FileName);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith(".png", result.FileName);
    }

    [Fact]
    public async Task SaveUserImageAsync_RejectsContentTypeMismatch()
    {
        var store = CreateStore();
        await using var stream = new MemoryStream(PngBytes);

        // Declared as JPEG but bytes are PNG: must fail magic-byte validation.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/jpeg", previousFile: null));
    }

    [Fact]
    public async Task SaveUserImageAsync_RejectsSvgContentType()
    {
        var store = CreateStore();
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        await using var stream = new MemoryStream(svg);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/svg+xml", previousFile: null));
    }

    [Fact]
    public async Task SaveUserImageAsync_RejectsHtmlPretendingToBeImage()
    {
        var store = CreateStore();
        var html = Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>");
        await using var stream = new MemoryStream(html);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/png", previousFile: null));
    }

    [Fact]
    public async Task SaveUserImageAsync_RejectsOversizePayload()
    {
        var store = CreateStore(maxBytes: 32);
        var oversize = new byte[64];
        Array.Copy(PngBytes, oversize, PngBytes.Length);
        await using var stream = new MemoryStream(oversize);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveUserImageAsync("series-a", stream, "image/png", previousFile: null));
    }

    [Fact]
    public async Task SaveUserImageAsync_DeletesPreviousFileOnReplace()
    {
        var store = CreateStore();
        await using var s1 = new MemoryStream(PngBytes);
        var first = await store.SaveUserImageAsync("series-a", s1, "image/png", previousFile: null);

        await using var s2 = new MemoryStream(JpegBytes);
        var second = await store.SaveUserImageAsync("series-a", s2, "image/jpeg", previousFile: first.FileName);

        Assert.NotEqual(first.FileName, second.FileName);
        Assert.Null(store.ResolveAbsolutePath(first.FileName));
        Assert.NotNull(store.ResolveAbsolutePath(second.FileName));
    }

    [Fact]
    public void ResolveAbsolutePath_RejectsPathTraversalAttempts()
    {
        var store = CreateStore();
        Assert.Null(store.ResolveAbsolutePath("../escape.png"));
        Assert.Null(store.ResolveAbsolutePath("/etc/passwd"));
        Assert.Null(store.ResolveAbsolutePath("foo/bar.png"));
        Assert.Null(store.ResolveAbsolutePath(""));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonHttpScheme()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "ftp://example.com/x.png", previousFile: null));
    }

    [Fact]
    public async Task DownloadAsync_RejectsInvalidUrl()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "not-a-url", previousFile: null));
    }

    [Fact]
    public async Task DownloadAsync_RejectsLoopbackHost()
    {
        var store = CreateStore();
        // 127.0.0.1 is unambiguously loopback so DNS resolution gives the
        // address as-is and SSRF guard blocks it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://127.0.0.1/x.png", previousFile: null));
    }

    [Fact]
    public async Task DownloadAsync_RejectsAwsMetadataAddress()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://169.254.169.254/latest/meta-data/", previousFile: null));
    }

    [Fact]
    public async Task DownloadAsync_RejectsRfc1918Address()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://10.0.0.1/x.png", previousFile: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DownloadAsync("series-a", "http://192.168.1.1/x.png", previousFile: null));
    }

    private SeriesImageStore CreateStore(int maxBytes = 1024 * 1024)
    {
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            SeriesImageCacheDirectory = _cacheDir,
            SeriesImageMaxBytes = maxBytes
        });
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ThrowingHandler()));
        return new SeriesImageStore(
            httpClientFactory.Object,
            settings.Object,
            Mock.Of<ILogger<SeriesImageStore>>());
    }

    /// <summary>
    /// Ensures DownloadAsync tests that should fail at the URL/SSRF stage
    /// never actually issue a network request — if validation regresses we'd
    /// otherwise either succeed unexpectedly or hit a real DNS lookup.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "SeriesImageStore should have rejected the URL before issuing a network request");
        }
    }
}
