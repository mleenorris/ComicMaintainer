using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileCoverCacheServiceCoverageTests : IDisposable
{
    private const int PageSize = 64 * 1024;

    private readonly string _root;
    private readonly string _cacheDir;
    private readonly AppSettings _settings;
    private readonly Mock<IComicReaderService> _reader = new();

    public FileCoverCacheServiceCoverageTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cm-file-cover-cov-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Join(_root, "cache");
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            ConfigDirectory = Path.Join(_root, "config"),
            FileCoverCacheDirectory = _cacheDir,
            FileCoverCacheEnabled = true,
            FileCoverCacheMaxMegabytes = 512,
            // Store the raw page bytes so entry sizes are predictable.
            FileCoverThumbnailMaxWidth = 0,
        };
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .ReturnsAsync(() => (new byte[PageSize], "image/jpeg"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateAsync_BlankPath_ReturnsNull(string path)
    {
        var service = CreateService();

        Assert.Null(await service.GetOrCreateAsync(path));
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_PathThatCannotBeStatted_ReturnsNull()
    {
        var service = CreateService();

        Assert.Null(await service.GetOrCreateAsync(Path.Join(_root, "bad\0name.cbz")));
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidateAsync_BlankPath_LeavesCacheUntouched(string path)
    {
        var service = CreateService();
        var file = CreateSource("a.cbz");
        Assert.NotNull(await service.GetOrCreateAsync(file));

        await service.InvalidateAsync(path);

        Assert.Single(Directory.GetFiles(_cacheDir, "*.img"));
        Assert.Single(Directory.GetFiles(_cacheDir, "*.meta"));
    }

    [Fact]
    public async Task InvalidateAsync_PathThatCannotBeNormalized_IsSwallowed()
    {
        var service = CreateService();

        await service.InvalidateAsync(Path.Join(_root, "bad\0name.cbz"));

        Assert.False(Directory.Exists(_cacheDir));
    }

    [Fact]
    public async Task InvalidateAsync_NullConfigDirectory_FallsBackToDefaultRoot()
    {
        _settings.FileCoverCacheDirectory = null;
        _settings.ConfigDirectory = null!;
        var service = CreateService();

        // Nothing exists under the default root for this path, so this is a
        // no-op that must not throw.
        await service.InvalidateAsync(Path.Join(_root, $"missing-{Guid.NewGuid():N}.cbz"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetOrCreateAsync_BlankCacheDirectory_UsesConfigDirectory(string? configured)
    {
        _settings.FileCoverCacheDirectory = configured;
        var service = CreateService();
        var file = CreateSource("a.cbz");

        var entry = await service.GetOrCreateAsync(file);

        Assert.NotNull(entry);
        var expectedDir = Path.Join(_settings.ConfigDirectory, "file-covers");
        Assert.True(File.Exists(Path.Join(expectedDir, KeyFor(file) + ".img")));
        Assert.True(File.Exists(Path.Join(expectedDir, KeyFor(file) + ".meta")));
        Assert.False(Directory.Exists(_cacheDir));
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheDirectoryIsAFile_ServesGeneratedEntryWithoutCaching()
    {
        var blocker = Path.Join(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        _settings.FileCoverCacheDirectory = blocker;
        var service = CreateService();
        var file = CreateSource("a.cbz");

        var first = await service.GetOrCreateAsync(file);
        var second = await service.GetOrCreateAsync(file);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(PageSize, first!.Value.Data.Length);
        Assert.Equal("image/jpeg", first.Value.ContentType);
        // Nothing could be persisted, so every request re-reads the archive.
        _reader.Verify(r => r.GetPageAsync(file, 1), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_BlobPathOccupiedByDirectory_RemovesTempFileAndServesEntry()
    {
        var file = CreateSource("a.cbz");
        var key = KeyFor(file);
        var blobPath = Path.Join(_cacheDir, key + ".img");
        Directory.CreateDirectory(blobPath);
        var service = CreateService();

        var entry = await service.GetOrCreateAsync(file);

        Assert.NotNull(entry);
        Assert.Equal(PageSize, entry!.Value.Data.Length);
        Assert.False(File.Exists(blobPath + ".tmp"));
        Assert.False(File.Exists(Path.Join(_cacheDir, key + ".meta")));
        Assert.True(Directory.Exists(blobPath));
    }

    [Fact]
    public async Task GetOrCreateAsync_CorruptMetadata_RegeneratesAndRepairsEntry()
    {
        var service = CreateService();
        var file = CreateSource("a.cbz");
        Assert.NotNull(await service.GetOrCreateAsync(file));
        var metaPath = Path.Join(_cacheDir, KeyFor(file) + ".meta");
        File.WriteAllText(metaPath, "{ this is not json");

        var regenerated = await service.GetOrCreateAsync(file);
        var cached = await service.GetOrCreateAsync(file);

        Assert.NotNull(regenerated);
        Assert.NotNull(cached);
        Assert.StartsWith("{\"", File.ReadAllText(metaPath));
        _reader.Verify(r => r.GetPageAsync(file, 1), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_EveryThirtySecondWrite_PrunesOverLimitCache()
    {
        _settings.FileCoverCacheMaxMegabytes = 1;
        var service = CreateService();
        var files = Enumerable.Range(0, 32).Select(i => CreateSource($"issue-{i:D2}.cbz")).ToList();

        for (var i = 0; i < 31; i++)
        {
            Assert.NotNull(await service.GetOrCreateAsync(files[i]));
        }
        // 31 x 64 KiB is well over 1 MiB, but pruning only runs on the 32nd write.
        Assert.Equal(31, Directory.GetFiles(_cacheDir, "*.img").Length);

        var last = await service.GetOrCreateAsync(files[31]);

        Assert.NotNull(last);
        var blobs = Directory.GetFiles(_cacheDir, "*.img");
        Assert.InRange(blobs.Length, 1, 16);
        Assert.True(blobs.Sum(b => new FileInfo(b).Length) <= 1024L * 1024L);
        var metas = Directory.GetFiles(_cacheDir, "*.meta");
        Assert.Equal(
            blobs.Select(Path.GetFileNameWithoutExtension).Order(),
            metas.Select(Path.GetFileNameWithoutExtension).Order());
    }

    [Fact]
    public async Task GetOrCreateAsync_PruneWhenEveryEntryAloneExceedsLimit_EvictsWholeCache()
    {
        const int oversizedPage = 1024 * 1024 + 1;
        _settings.FileCoverCacheMaxMegabytes = 1;
        _reader
            .Setup(r => r.GetPageAsync(It.IsAny<string>(), 1))
            .ReturnsAsync(() => (new byte[oversizedPage], "image/jpeg"));
        var service = CreateService();

        for (var i = 0; i < 31; i++)
        {
            Assert.NotNull(await service.GetOrCreateAsync(CreateSource($"huge-{i:D2}.cbz")));
        }
        Assert.Equal(31, Directory.GetFiles(_cacheDir, "*.img").Length);

        var last = await service.GetOrCreateAsync(CreateSource("huge-31.cbz"));

        // The freshly generated cover is still served, but no single entry fits
        // the 1 MiB budget so the prune pass empties the cache completely.
        Assert.Equal(oversizedPage, last!.Value.Data.Length);
        Assert.Empty(Directory.GetFiles(_cacheDir, "*.img"));
        Assert.Empty(Directory.GetFiles(_cacheDir, "*.meta"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(512)]
    public async Task GetOrCreateAsync_PruneWithDisabledOrUnreachedLimit_KeepsAllEntries(int maxMegabytes)
    {
        _settings.FileCoverCacheMaxMegabytes = maxMegabytes;
        var service = CreateService();

        for (var i = 0; i < 32; i++)
        {
            Assert.NotNull(await service.GetOrCreateAsync(CreateSource($"issue-{i:D2}.cbz")));
        }

        Assert.Equal(32, Directory.GetFiles(_cacheDir, "*.img").Length);
        Assert.Equal(32, Directory.GetFiles(_cacheDir, "*.meta").Length);
    }

    private FileCoverCacheService CreateService()
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(_settings);
        return new FileCoverCacheService(_reader.Object, options.Object, Mock.Of<ILogger<FileCoverCacheService>>());
    }

    private string CreateSource(string name)
    {
        var path = Path.Join(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static string KeyFor(string filePath)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath))))
            .ToLowerInvariant();
}
