using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ComicMaintainer.Tests.Services;

public class SeriesArchiveCoverWriterTests : IDisposable
{
    private const string CoverEntryName = "0000-cmcover.jpg";

    private readonly string _root;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly AppSettings _settings = new();

    public SeriesArchiveCoverWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cmtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private SeriesArchiveCoverWriter CreateWriter()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_library.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(_settings);

        return new SeriesArchiveCoverWriter(
            scopeFactory,
            monitor.Object,
            new Mock<ILogger<SeriesArchiveCoverWriter>>().Object);
    }

    private string CreateCbz(string name, params string[] pageNames)
    {
        var path = Path.Combine(_root, name);
        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var page in pageNames)
        {
            var entry = zip.CreateEntry(page, CompressionLevel.Optimal);
            using var s = entry.Open();
            var bytes = System.Text.Encoding.UTF8.GetBytes("page:" + page);
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    private string CreateCoverImage(string contents = "cover-bytes")
    {
        var path = Path.Combine(_root, "cover.jpg");
        File.WriteAllText(path, contents);
        return path;
    }

    private static List<string> ListEntries(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public async Task WriteAsync_EmbedsCoverThatSortsFirstAmongPages()
    {
        var cbz = CreateCbz("issue.cbz", "001.jpg", "002.jpg", "010.jpg");
        var cover = CreateCoverImage();
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        var writer = CreateWriter();
        var written = await writer.WriteAsync("series", cover, "image/jpeg", force: true);

        Assert.True(written);
        var entries = ListEntries(cbz);
        Assert.Contains(CoverEntryName, entries);
        // The embedded cover must sort first under the reader's natural comparer.
        var imageEntries = entries
            .OrderBy(e => e, new NaturalStringComparer())
            .ToList();
        Assert.Equal(CoverEntryName, imageEntries.First());
        // Real pages are preserved.
        Assert.Contains("001.jpg", entries);
        Assert.Contains("002.jpg", entries);
        Assert.Contains("010.jpg", entries);
    }

    [Fact]
    public async Task WriteAsync_IsIdempotent_SecondWriteMakesNoChanges()
    {
        var cbz = CreateCbz("issue.cbz", "001.jpg");
        var cover = CreateCoverImage();
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);

        var writer = CreateWriter();
        Assert.True(await writer.WriteAsync("series", cover, "image/jpeg", force: true));
        Assert.False(await writer.WriteAsync("series", cover, "image/jpeg", force: true));

        // Exactly one managed cover entry remains.
        var covers = ListEntries(cbz).Count(e => e == CoverEntryName);
        Assert.Equal(1, covers);
    }

    [Fact]
    public async Task WriteAsync_ReplacesExistingCover_WhenBytesDiffer()
    {
        var cbz = CreateCbz("issue.cbz", "001.jpg");
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);
        var writer = CreateWriter();

        await writer.WriteAsync("series", CreateCoverImage("first"), "image/jpeg", force: true);
        var changed = await writer.WriteAsync("series", CreateCoverImage("second"), "image/jpeg", force: true);

        Assert.True(changed);
        Assert.Equal(1, ListEntries(cbz).Count(e => e == CoverEntryName));
        using var zip = ZipFile.OpenRead(cbz);
        var entry = zip.GetEntry(CoverEntryName)!;
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("second", reader.ReadToEnd());
    }

    [Fact]
    public async Task WriteAsync_RespectsSettingFlag_WhenNotForced()
    {
        var cbz = CreateCbz("issue.cbz", "001.jpg");
        var cover = CreateCoverImage();
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);
        var writer = CreateWriter();

        _settings.WriteCoverToFirstArchive = false;
        Assert.False(await writer.WriteAsync("series", cover, "image/jpeg", force: false));
        Assert.DoesNotContain(CoverEntryName, ListEntries(cbz));

        _settings.WriteCoverToFirstArchive = true;
        Assert.True(await writer.WriteAsync("series", cover, "image/jpeg", force: false));
        Assert.Contains(CoverEntryName, ListEntries(cbz));
    }

    [Fact]
    public async Task RemoveAsync_StripsEmbeddedCover_LeavingRealPages()
    {
        var cbz = CreateCbz("issue.cbz", "001.jpg", "002.jpg");
        var cover = CreateCoverImage();
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbz);
        var writer = CreateWriter();

        await writer.WriteAsync("series", cover, "image/jpeg", force: true);
        Assert.Contains(CoverEntryName, ListEntries(cbz));

        await writer.RemoveAsync("series");
        var entries = ListEntries(cbz);
        Assert.DoesNotContain(CoverEntryName, entries);
        Assert.Contains("001.jpg", entries);
        Assert.Contains("002.jpg", entries);
    }

    [Fact]
    public async Task WriteAsync_SkipsNonWritableFirstIssue()
    {
        // A .cbr is not a writable archive; the writer must not touch it.
        var cbr = Path.Combine(_root, "issue.cbr");
        File.WriteAllText(cbr, "not-a-real-rar");
        var cover = CreateCoverImage();
        _library.Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cbr);
        var writer = CreateWriter();

        var written = await writer.WriteAsync("series", cover, "image/jpeg", force: true);
        Assert.False(written);
    }
}
