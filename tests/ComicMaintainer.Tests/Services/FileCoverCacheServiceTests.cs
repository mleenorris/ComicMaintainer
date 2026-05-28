using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
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
}
