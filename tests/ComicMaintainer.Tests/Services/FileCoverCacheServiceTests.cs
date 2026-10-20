using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileCoverCacheServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _cacheDir;
    private readonly Mock<IComicReaderService> _reader;
    private readonly Mock<IOptionsMonitor<AppSettings>> _options;
    private readonly FileCoverCacheService _service;
    private readonly AppSettings _settings;

    public FileCoverCacheServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "filecover_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _cacheDir = Path.Combine(_testDir, "cache");

        _settings = new AppSettings
        {
            ConfigDirectory = _testDir,
            FileCoverCacheDirectory = _cacheDir,
            FileCoverCacheEnabled = true,
            FileCoverCacheMaxMegabytes = 512,
        };

        _options = new Mock<IOptionsMonitor<AppSettings>>();
        _options.Setup(o => o.CurrentValue).Returns(_settings);

        _reader = new Mock<IComicReaderService>();
        _service = new FileCoverCacheService(_reader.Object, _options.Object, Mock.Of<ILogger<FileCoverCacheService>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* ignore */ }
    }

    private string CreateFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllBytes(path, content ?? new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private void SetupPage(byte[] data, string contentType = "image/jpeg")
    {
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .ReturnsAsync(((byte[], string)?)(data, contentType));
    }

    [Fact]
    public async Task GetOrCreateAsync_FirstCall_GeneratesAndPersists()
    {
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 9, 8, 7 });

        var entry = await _service.GetOrCreateAsync(file);

        Assert.NotNull(entry);
        Assert.Equal(new byte[] { 9, 8, 7 }, entry!.Value.Data);
        Assert.Equal("image/jpeg", entry.Value.ContentType);
        Assert.True(Directory.EnumerateFiles(_cacheDir, "*.img").Any());
        Assert.True(Directory.EnumerateFiles(_cacheDir, "*.meta").Any());
    }

    [Fact]
    public async Task GetOrCreateAsync_SecondCall_DoesNotReopenArchive()
    {
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 9, 8, 7 });

        _ = await _service.GetOrCreateAsync(file);
        _ = await _service.GetOrCreateAsync(file);

        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_FileChanged_Regenerates()
    {
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 1, 1, 1 });

        _ = await _service.GetOrCreateAsync(file);

        // Mutate file: change content + bump mtime.
        File.WriteAllBytes(file, new byte[] { 5, 5, 5, 5, 5 });
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        SetupPage(new byte[] { 2, 2, 2 });
        var entry = await _service.GetOrCreateAsync(file);

        Assert.Equal(new byte[] { 2, 2, 2 }, entry!.Value.Data);
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_MissingFile_ReturnsNull()
    {
        var entry = await _service.GetOrCreateAsync(Path.Combine(_testDir, "missing.cbz"));
        Assert.Null(entry);
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReaderReturnsNull_ReturnsNull()
    {
        var file = CreateFile("a.cbz");
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .ReturnsAsync(((byte[], string)?)null);

        var entry = await _service.GetOrCreateAsync(file);
        Assert.Null(entry);
        Assert.False(Directory.Exists(_cacheDir) && Directory.EnumerateFiles(_cacheDir, "*.img").Any());
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheDisabled_BypassesPersistence()
    {
        _settings.FileCoverCacheEnabled = false;
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 4, 4 });

        var entry = await _service.GetOrCreateAsync(file);

        Assert.NotNull(entry);
        Assert.False(Directory.Exists(_cacheDir));
    }

    [Fact]
    public async Task InvalidateAsync_RemovesBlobAndMeta()
    {
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 1, 2 });
        _ = await _service.GetOrCreateAsync(file);
        Assert.True(Directory.EnumerateFiles(_cacheDir).Any());

        await _service.InvalidateAsync(file);

        Assert.False(Directory.EnumerateFiles(_cacheDir).Any());
    }

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentRequests_OpenArchiveOnce()
    {
        var file = CreateFile("a.cbz");
        var gate = new TaskCompletionSource();
        var callCount = 0;
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .Returns(async () =>
            {
                Interlocked.Increment(ref callCount);
                await gate.Task;
                return ((byte[], string)?)(new byte[] { 7 }, "image/jpeg");
            });

        var t1 = _service.GetOrCreateAsync(file);
        var t2 = _service.GetOrCreateAsync(file);
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, callCount);
    }

    private static byte[] MakeImage(uint width, uint height, MagickFormat format = MagickFormat.Png)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Format = format;
        return image.ToByteArray();
    }

    [Fact]
    public async Task GetOrCreateAsync_LargeCover_ReturnsWebpThumbnailWithinMaxWidth()
    {
        var file = CreateFile("big.cbz");
        var source = MakeImage(1200, 1800);
        SetupPage(source, "image/png");

        var entry = await _service.GetOrCreateAsync(file);

        Assert.NotNull(entry);
        Assert.Equal("image/webp", entry!.Value.ContentType);
        Assert.Equal("webp-w480-q80", entry.Value.Variant);
        using var thumb = new MagickImage(entry.Value.Data);
        Assert.Equal(MagickFormat.WebP, thumb.Format);
        Assert.True(thumb.Width <= 480);
        Assert.Equal(480u, thumb.Width);
        Assert.Equal(720u, thumb.Height);

        // Served from disk the second time with the same variant/format.
        var again = await _service.GetOrCreateAsync(file);
        Assert.Equal("image/webp", again!.Value.ContentType);
        Assert.Equal(entry.Value.Data, again.Value.Data);
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_JpegFormat_ReturnsJpegThumbnail()
    {
        _settings.FileCoverThumbnailFormat = "jpeg";
        var file = CreateFile("big.cbz");
        SetupPage(MakeImage(1000, 1500), "image/png");

        var entry = await _service.GetOrCreateAsync(file);

        Assert.Equal("image/jpeg", entry!.Value.ContentType);
        using var thumb = new MagickImage(entry.Value.Data);
        Assert.Equal(MagickFormat.Jpeg, thumb.Format);
        Assert.Equal(480u, thumb.Width);
    }

    [Fact]
    public async Task GetOrCreateAsync_SmallCover_IsNotUpscaled()
    {
        var file = CreateFile("small.cbz");
        SetupPage(MakeImage(200, 300), "image/png");

        var entry = await _service.GetOrCreateAsync(file);

        using var thumb = new MagickImage(entry!.Value.Data);
        Assert.Equal(200u, thumb.Width);
        Assert.Equal(300u, thumb.Height);
    }

    [Fact]
    public async Task GetOrCreateAsync_VariantChange_Regenerates()
    {
        var file = CreateFile("big.cbz");
        SetupPage(MakeImage(1200, 1800), "image/png");

        var first = await _service.GetOrCreateAsync(file);
        _settings.FileCoverThumbnailMaxWidth = 240;
        var second = await _service.GetOrCreateAsync(file);

        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Exactly(2));
        Assert.NotEqual(first!.Value.Variant, second!.Value.Variant);
        using var thumb = new MagickImage(second.Value.Data);
        Assert.Equal(240u, thumb.Width);

        // Quality change is a variant change too.
        _settings.FileCoverThumbnailQuality = 50;
        _ = await _service.GetOrCreateAsync(file);
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Exactly(3));
    }

    [Fact]
    public async Task GetOrCreateAsync_LegacyMetaWithoutVariant_IsCacheMiss()
    {
        var file = CreateFile("a.cbz");
        SetupPage(MakeImage(1200, 1800), "image/png");
        _ = await _service.GetOrCreateAsync(file);

        // Simulate an entry written by a pre-thumbnail version.
        var metaPath = Directory.EnumerateFiles(_cacheDir, "*.meta").Single();
        var info = new FileInfo(file);
        File.WriteAllText(metaPath,
            $"{{\"SourceLength\":{info.Length},\"SourceMtimeUtcTicks\":{info.LastWriteTimeUtc.Ticks},\"ContentType\":\"image/png\"}}");

        var entry = await _service.GetOrCreateAsync(file);

        Assert.Equal("image/webp", entry!.Value.ContentType);
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), 1), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_MaxWidthZero_ReturnsOriginalBytes()
    {
        _settings.FileCoverThumbnailMaxWidth = 0;
        var file = CreateFile("big.cbz");
        var source = MakeImage(1200, 1800);
        SetupPage(source, "image/png");

        var entry = await _service.GetOrCreateAsync(file);

        Assert.Equal(source, entry!.Value.Data);
        Assert.Equal("image/png", entry.Value.ContentType);
        Assert.Equal("orig", entry.Value.Variant);
    }

    [Fact]
    public async Task GetOrCreateAsync_UndecodableImage_FallsBackToOriginalBytes()
    {
        var file = CreateFile("a.cbz");
        var garbage = new byte[] { 0xFF, 0xD8, 0x00, 0x01, 0x02 };
        SetupPage(garbage, "image/jpeg");

        var entry = await _service.GetOrCreateAsync(file);

        Assert.Equal(garbage, entry!.Value.Data);
        Assert.Equal("image/jpeg", entry.Value.ContentType);
    }
}
