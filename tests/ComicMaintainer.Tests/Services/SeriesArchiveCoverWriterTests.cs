using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesArchiveCoverWriterTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _settings = new();
    private AppSettings _appSettings = new() { WriteCoverToFirstArchive = true };

    public SeriesArchiveCoverWriterTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"archive_cover_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _settings.Setup(s => s.CurrentValue).Returns(() => _appSettings);
    }

    private SeriesArchiveCoverWriter CreateWriter()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _library.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new SeriesArchiveCoverWriter(
            scopeFactory,
            _settings.Object,
            new Mock<ILogger<SeriesArchiveCoverWriter>>().Object);
    }

    private string CreateCbz(string name, params string[] pageNames)
    {
        var path = Path.Combine(_testDirectory, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var page in pageNames)
        {
            var entry = zip.CreateEntry(page);
            using var stream = entry.Open();
            stream.Write(new byte[] { 1, 2, 3, 4 });
        }
        return path;
    }

    private string CreateCoverImage(byte[] bytes)
    {
        var path = Path.Combine(_testDirectory, $"cover_src_{Guid.NewGuid()}.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static List<string> ListEntries(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static byte[] ReadEntry(string cbzPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        var entry = zip.GetEntry(entryName)!;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task WriteAsync_EmbedsCoverIntoFirstArchive()
    {
        var cbz = CreateCbz("issue1.cbz", "01.jpg", "02.jpg");
        var coverBytes = new byte[] { 9, 8, 7, 6, 5 };
        var coverSrc = CreateCoverImage(coverBytes);
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        await CreateWriter().WriteAsync("series-a", coverSrc, "image/jpeg");

        var entries = ListEntries(cbz);
        Assert.Contains("cover.jpg", entries);
        Assert.Contains("01.jpg", entries);
        Assert.Contains("02.jpg", entries);
        Assert.Equal(coverBytes, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_FeatureDisabled_DoesNothing()
    {
        _appSettings = new AppSettings { WriteCoverToFirstArchive = false };
        var cbz = CreateCbz("issue1.cbz", "01.jpg");
        var coverSrc = CreateCoverImage(new byte[] { 1 });
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        await CreateWriter().WriteAsync("series-a", coverSrc, "image/jpeg");

        Assert.DoesNotContain("cover.jpg", ListEntries(cbz));
        _library.Verify(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteAsync_ReplacesDifferentExtensionCover()
    {
        var cbz = CreateCbz("issue1.cbz", "cover.png", "01.jpg");
        var coverBytes = new byte[] { 4, 5, 6 };
        var coverSrc = CreateCoverImage(coverBytes);
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        await CreateWriter().WriteAsync("series-a", coverSrc, "image/jpeg");

        var entries = ListEntries(cbz);
        Assert.Contains("cover.jpg", entries);
        Assert.DoesNotContain("cover.png", entries);
        Assert.Equal(coverBytes, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_IdenticalCover_DoesNotRewrite()
    {
        var coverBytes = new byte[] { 7, 7, 7 };
        var cbz = CreateCbz("issue1.cbz", "01.jpg");
        var coverSrc = CreateCoverImage(coverBytes);
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        var writer = CreateWriter();
        await writer.WriteAsync("series-a", coverSrc, "image/jpeg");
        var firstWrite = File.GetLastWriteTimeUtc(cbz);

        await Task.Delay(20);
        await writer.WriteAsync("series-a", coverSrc, "image/jpeg");

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(cbz));
    }

    [Fact]
    public async Task WriteAsync_NonWritableCbr_DoesNothing()
    {
        // A .cbr file (even if it's actually a zip) is not a writable archive.
        var cbr = CreateCbz("issue1.cbr", "01.jpg");
        var coverSrc = CreateCoverImage(new byte[] { 1, 2 });
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbr);

        await CreateWriter().WriteAsync("series-a", coverSrc, "image/jpeg");

        Assert.DoesNotContain("cover.jpg", ListEntries(cbr));
    }

    [Fact]
    public async Task WriteAsync_NoFirstArchive_DoesNothing()
    {
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Should not throw.
        await CreateWriter().WriteAsync("series-a", CreateCoverImage(new byte[] { 1 }), "image/jpeg");
    }

    [Fact]
    public async Task RemoveAsync_RemovesManagedCoverOnly()
    {
        var cbz = CreateCbz("issue1.cbz", "cover.jpg", "01.jpg", "02.jpg");
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        await CreateWriter().RemoveAsync("series-a");

        var entries = ListEntries(cbz);
        Assert.DoesNotContain("cover.jpg", entries);
        Assert.Contains("01.jpg", entries);
        Assert.Contains("02.jpg", entries);
    }

    [Fact]
    public async Task RemoveAsync_NoManagedCover_DoesNotRewrite()
    {
        var cbz = CreateCbz("issue1.cbz", "01.jpg");
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        var before = File.GetLastWriteTimeUtc(cbz);
        await Task.Delay(20);
        await CreateWriter().RemoveAsync("series-a");

        Assert.Equal(before, File.GetLastWriteTimeUtc(cbz));
    }

    [Fact]
    public async Task RemoveAsync_DoesNotTouchNestedCover()
    {
        var cbz = CreateCbz("issue1.cbz", "sub/cover.jpg", "01.jpg");
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        await CreateWriter().RemoveAsync("series-a");

        Assert.Contains("sub/cover.jpg", ListEntries(cbz));
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignore */ }
    }
}
