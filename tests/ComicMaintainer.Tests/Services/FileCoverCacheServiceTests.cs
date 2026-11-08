using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Controllers;
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
        _testDir = Path.Join(Path.GetTempPath(), "filecover_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _cacheDir = Path.Join(_testDir, "cache");

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
        try { Directory.Delete(_testDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
    }

    private string CreateFile(string name, byte[]? content = null)
    {
        var path = Path.Join(_testDir, name);
        File.WriteAllBytes(path, content ?? new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private void SetupPage(byte[] data, string contentType = "image/jpeg")
    {
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .ReturnsAsync((data, contentType));
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
        var entry = await _service.GetOrCreateAsync(Path.Join(_testDir, "missing.cbz"));
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
                return (new byte[] { 7 }, "image/jpeg");
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

    // ---- GetValidator (conditional requests) ------------------------------------------

    [Fact]
    public void GetValidator_ComesFromTheFilesMetadataWithoutReadingTheArchiveOrTheCache()
    {
        var file = CreateFile("a.cbz", new byte[] { 1, 2, 3, 4, 5 });
        var lastWrite = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, lastWrite);
        _settings.FileCoverThumbnailMaxWidth = 320;
        _settings.FileCoverThumbnailFormat = "jpeg";
        _settings.FileCoverThumbnailQuality = 70;

        var validator = _service.GetValidator(file);

        Assert.Equal(new FileCoverValidator(5, new DateTimeOffset(lastWrite), "jpeg-w320-q70"), validator);
        _reader.VerifyNoOtherCalls();
        Assert.False(Directory.Exists(_cacheDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetValidator_MatchesTheValidatorOfTheCoverItDescribes(bool cacheEnabled)
    {
        _settings.FileCoverCacheEnabled = cacheEnabled;
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 9, 8, 7 });

        var before = _service.GetValidator(file);
        var generated = await _service.GetOrCreateAsync(file);
        var cached = await _service.GetOrCreateAsync(file);

        Assert.NotNull(before);
        Assert.Equal(before, generated!.Value.Validator);
        Assert.Equal(before, cached!.Value.Validator);
    }

    [Fact]
    public void GetValidator_ChangesWhenTheFileOrTheThumbnailSettingsChange()
    {
        var file = CreateFile("a.cbz");
        var original = _service.GetValidator(file);

        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5, 6 });
        var rewritten = _service.GetValidator(file);
        _settings.FileCoverThumbnailMaxWidth = 123;
        var resized = _service.GetValidator(file);

        Assert.NotEqual(original, rewritten);
        Assert.NotEqual(rewritten, resized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetValidator_BlankPath_ReturnsNull(string path) =>
        Assert.Null(_service.GetValidator(path));

    [Fact]
    public void GetValidator_MissingFile_ReturnsNull() =>
        Assert.Null(_service.GetValidator(Path.Join(_testDir, "missing.cbz")));

    // ---- page-list version in the variant ----------------------------------------------

    [Theory]
    [InlineData("webp-w480-q80")]
    [InlineData("orig")]
    public void WithPageListVersion_Version1_KeepsTheVariantCoversWereCachedUnder(string thumbnailVariant) =>
        Assert.Equal(thumbnailVariant, FileCoverCacheService.WithPageListVersion(thumbnailVariant, 1));

    [Theory]
    [InlineData("webp-w480-q80", 2, "webp-w480-q80-l2")]
    [InlineData("orig", 3, "orig-l3")]
    public void WithPageListVersion_LaterVersions_MakeCachedCoversAndTheirETagsMiss(string thumbnailVariant, int version, string expected)
    {
        var variant = FileCoverCacheService.WithPageListVersion(thumbnailVariant, version);

        Assert.Equal(expected, variant);
        // The token survives into the cover ETag.
        Assert.Contains("-l" + version, ComicReaderController.BuildCoverETag(new FileCoverValidator(1, DateTimeOffset.UnixEpoch, variant)));
    }

    [Fact]
    public void GetValidator_CarriesTheReadersPageListVersion()
    {
        var file = CreateFile("a.cbz");

        Assert.Equal(
            FileCoverCacheService.WithPageListVersion("webp-w480-q80", ComicReaderService.PageListVersion),
            _service.GetValidator(file)!.Value.Variant);
    }

    // ---- last-access time (LRU pruning hint) ------------------------------------------

    // The clock values below are far in the past, so the access times the service
    // writes cannot be confused with the OS's own access-time updates (which stamp
    // the real current time, when the volume has them enabled at all).

    [Fact]
    public void RefreshAccessTime_RecentAccessTime_IsNotRewritten()
    {
        var blob = CreateFile("x.img");
        var lastAccess = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(blob, lastAccess);

        FileCoverCacheService.RefreshAccessTime(blob, lastAccess + FileCoverCacheService.AccessTimeRefreshInterval - TimeSpan.FromMinutes(1));

        Assert.Equal(lastAccess, File.GetLastAccessTimeUtc(blob));
    }

    [Fact]
    public void RefreshAccessTime_AccessTimeOlderThanTheInterval_IsSetToNow()
    {
        var blob = CreateFile("x.img");
        File.SetLastAccessTimeUtc(blob, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var now = new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        FileCoverCacheService.RefreshAccessTime(blob, now);

        Assert.Equal(now, File.GetLastAccessTimeUtc(blob));
    }

    [Fact]
    public void RefreshAccessTime_MissingBlob_IsIgnored() =>
        FileCoverCacheService.RefreshAccessTime(Path.Join(_testDir, "gone.img"), DateTime.UtcNow);

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_KeepsAStaleBlobRecentForPruning()
    {
        var file = CreateFile("a.cbz");
        SetupPage(new byte[] { 9, 8, 7 });
        await _service.GetOrCreateAsync(file);
        var blob = Directory.EnumerateFiles(_cacheDir, "*.img").Single();
        File.SetLastAccessTimeUtc(blob, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var before = DateTime.UtcNow;
        Assert.NotNull(await _service.GetOrCreateAsync(file));

        Assert.True(File.GetLastAccessTimeUtc(blob) >= before.AddSeconds(-2),
            $"expected a refreshed access time, got {File.GetLastAccessTimeUtc(blob):O}");
    }
}
