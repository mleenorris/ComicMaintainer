using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers metadata, processing, rename, normalize and series-resolution paths of
/// <see cref="ComicProcessorService"/> against real CBZ fixtures.
/// </summary>
public class ComicProcessorServiceCoverageTests : IDisposable
{
    private const string RenamingTemplate = "Renaming file from {OldPath} to {NewPath}";
    private const int LargeEntrySize = 11 * 1024 * 1024;
    private static readonly DateTimeOffset FixtureTimestamp = new(2021, 3, 4, 5, 6, 8, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _library;
    private readonly AppSettings _settings;
    private readonly HookLogger _logger = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private readonly ConcurrentQueue<ProcessingHistoryEntry> _historyEntries = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _jobSignals = new();
    private readonly List<ComicProcessorService> _services = new();

    public ComicProcessorServiceCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cps-cov-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);

        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Combine(_root, "duplicates"),
            TempFileDirectory = Path.Combine(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
        };

        _history
            .Setup(h => h.AddHistoryEntryAsync(It.IsAny<ProcessingHistoryEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessingHistoryEntry, CancellationToken>((entry, _) => _historyEntries.Enqueue(entry))
            .Returns(Task.CompletedTask);

        _broadcaster
            .Setup(b => b.BroadcastJobUpdateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns<Guid, string, int, int, int, int>((jobId, status, _, _, _, _) =>
            {
                if (status is "completed" or "cancelled" or "failed")
                {
                    JobSignal(jobId).TrySetResult(status);
                }

                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- metadata

    [Fact]
    public async Task GetMetadataAsync_CorruptArchive_ReturnsNullAndLogsError()
    {
        var service = CreateService();
        var path = WriteCorrupt("Series/bad.cbz");

        var result = await service.GetMetadataAsync(path);

        Assert.Null(result);
        Assert.True(_logger.Has(LogLevel.Error, "Error reading metadata from {FilePath}"));
    }

    [Fact]
    public async Task GetMetadataAsync_MalformedComicInfo_ReturnsNullAndExcludesFromErrorReports()
    {
        var service = CreateService();
        var path = WriteCbz("Series/malformed.cbz", "<ComicInfo><Series>Broken</ComicInfo>");

        var result = await service.GetMetadataAsync(path);

        Assert.Null(result);
        Assert.True(_logger.Has(LogLevel.Error, "Error parsing ComicInfo.xml"));
        Assert.Contains(
            _logger.Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>(),
            scope => scope.Any(kv => kv.Key == ErrorReportLogProperties.ExcludeFromErrorReports));
    }

    [Fact]
    public async Task GetMetadataAsync_PathThroughLinkedDirectoryOutsideLibrary_ReturnsNull()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var outsideDir = Path.Combine(_root, "outside");
        WriteCbzAt(Path.Combine(outsideDir, "real.cbz"), Xml("Outside", "1", "Chapter 1"));
        Directory.CreateSymbolicLink(Path.Combine(_library, "linked"), outsideDir);

        var result = await service.GetMetadataAsync(Path.Combine(_library, "linked", "real.cbz"));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("My_Series/Oneshot.cbz", "My:Series", null)]
    [InlineData("Series/Chapter 0007.cbz", "Series", "7")]
    public async Task GetMetadataAsync_WithoutComicInfo_DerivesSeriesFromFolderAndIssueFromName(
        string relativePath, string expectedSeries, string? expectedIssue)
    {
        var service = CreateService();
        var path = WriteCbz(relativePath, comicInfoXml: null);

        var result = await service.GetMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Equal(expectedSeries, result!.Series);
        Assert.Equal(expectedIssue, result.Issue);
    }

    [Fact]
    public async Task GetMetadataAsync_FullComicInfo_ParsesEveryField()
    {
        var service = CreateService();
        var path = WriteCbz("Series/full.cbz", Xml("My Series", "12.5", "Chapter 12.5",
            ("Volume", "3"), ("Publisher", "Pub"), ("Year", "2021"), ("Summary", "Sum"),
            ("Writer", "Alice"), ("Writer", "Bob"), ("Tag", "t1"), ("Tag", "t2")));

        var result = await service.GetMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Equal("My Series", result!.Series);
        Assert.Equal("12.5", result.Issue);
        Assert.Equal("Chapter 12.5", result.Title);
        Assert.Equal("3", result.Volume);
        Assert.Equal("Pub", result.Publisher);
        Assert.Equal(2021, result.Year);
        Assert.Equal("Sum", result.Summary);
        Assert.Equal(new[] { "Alice", "Bob" }, result.Authors);
        Assert.Equal(new[] { "t1", "t2" }, result.Tags);
    }

    [Fact]
    public async Task GetMetadataAsync_SparseComicInfoWithInvalidYear_ReturnsNullFields()
    {
        var service = CreateService();
        var path = WriteCbz("Series/sparse.cbz", "<ComicInfo><Year>abc</Year></ComicInfo>");

        var result = await service.GetMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Null(result!.Series);
        Assert.Null(result.Title);
        Assert.Null(result.Issue);
        Assert.Null(result.Volume);
        Assert.Null(result.Publisher);
        Assert.Null(result.Year);
        Assert.Null(result.Summary);
        Assert.Empty(result.Authors);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_MissingOrNonArchiveFile_ReturnsNull()
    {
        var service = CreateService();
        var textFile = Path.Combine(_library, "notes.txt");
        await File.WriteAllTextAsync(textFile, "not a comic");

        Assert.Null(await service.GetSeriesMetadataAsync(Path.Combine(_library, "missing.cbz")));
        Assert.Null(await service.GetSeriesMetadataAsync(textFile));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_FullComicInfo_ParsesSeriesFields()
    {
        var service = CreateService();
        var path = WriteCbz("Series/full.cbz", Xml("Main", "4", "Chapter 4",
            ("AlternateSeries", "Alt"), ("SeriesGroup", "Group"), ("Volume", "2"),
            ("Publisher", "Pub"), ("Year", "1999")));

        var result = await service.GetSeriesMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Equal("Main", result!.Series);
        Assert.Equal("Alt", result.AlternateSeries);
        Assert.Equal("Group", result.SeriesGroup);
        Assert.Equal("Chapter 4", result.Title);
        Assert.Equal("4", result.Issue);
        Assert.Equal("2", result.Volume);
        Assert.Equal("Pub", result.Publisher);
        Assert.Equal(1999, result.Year);
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_EmptyComicInfo_ReturnsNullFields()
    {
        var service = CreateService();
        var path = WriteCbz("Series/empty.cbz", "<ComicInfo />");

        var result = await service.GetSeriesMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Null(result!.Series);
        Assert.Null(result.AlternateSeries);
        Assert.Null(result.SeriesGroup);
        Assert.Null(result.Title);
        Assert.Null(result.Issue);
        Assert.Null(result.Volume);
        Assert.Null(result.Publisher);
        Assert.Null(result.Year);
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_WithoutComicInfo_UsesFolderAndFilename()
    {
        var service = CreateService();
        var path = WriteCbz("Series Folder/Chapter 0007.cbz", comicInfoXml: null);

        var result = await service.GetSeriesMetadataAsync(path);

        Assert.NotNull(result);
        Assert.Equal("Series Folder", result!.Series);
        Assert.Equal("Series Folder", result.SeriesGroup);
        Assert.Equal("7", result.Issue);
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_CorruptArchive_ReturnsNullAndLogsError()
    {
        var service = CreateService();
        var path = WriteCorrupt("Series/bad.cbz");

        Assert.Null(await service.GetSeriesMetadataAsync(path));
        Assert.True(_logger.Has(LogLevel.Error, "Error reading series metadata from {FilePath}"));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_MalformedComicInfo_ReturnsNullAndLogsError()
    {
        var service = CreateService();
        var path = WriteCbz("Series/malformed.cbz", "<ComicInfo><Series>Broken");

        Assert.Null(await service.GetSeriesMetadataAsync(path));
        Assert.True(_logger.Has(LogLevel.Error, "Error parsing series metadata from ComicInfo.xml"));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_FileOutsideAllowedDirectories_ReturnsNullLikeGetMetadataAsync()
    {
        var service = CreateService();
        var outside = WriteCbzAt(Path.Combine(_root, "outside", "book.cbz"), Xml("Outside", "1", "Chapter 1"));

        Assert.Null(await service.GetMetadataAsync(outside));
        Assert.Null(await service.GetSeriesMetadataAsync(outside));
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_PathThroughLinkedDirectoryOutsideLibrary_ReturnsNull()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var outsideDir = Path.Combine(_root, "outside");
        WriteCbzAt(Path.Combine(outsideDir, "real.cbz"), Xml("Outside", "1", "Chapter 1"));
        Directory.CreateSymbolicLink(Path.Combine(_library, "linked"), outsideDir);

        var result = await service.GetSeriesMetadataAsync(Path.Combine(_library, "linked", "real.cbz"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSeriesMetadataAsync_PathThroughLinkedDirectoryInsideLibrary_ReadsTheArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        WriteCbz("Series/book.cbz", Xml("Main", "4", "Chapter 4", ("SeriesGroup", "Group")));
        var alias = Path.Combine(_library, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_library, "Series"));

        var result = await service.GetSeriesMetadataAsync(Path.Combine(alias, "book.cbz"));

        Assert.NotNull(result);
        Assert.Equal("Main", result!.Series);
        Assert.Equal("Group", result.SeriesGroup);
        Assert.Equal("4", result.Issue);
    }

    [Fact]
    public async Task UpdateMetadataAsync_MissingFile_ReturnsFalseWithWarning()
    {
        var service = CreateService();

        var result = await service.UpdateMetadataAsync(Path.Combine(_library, "missing.cbz"), new ComicMetadata());

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot update metadata: file does not exist: {FilePath}"));
    }

    [Fact]
    public async Task UpdateMetadataAsync_NonArchiveFile_ReturnsFalseWithWarning()
    {
        var service = CreateService();
        var textFile = Path.Combine(_library, "notes.txt");
        await File.WriteAllTextAsync(textFile, "plain text");

        var result = await service.UpdateMetadataAsync(textFile, new ComicMetadata { Series = "X" });

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot update metadata: file is not a supported comic archive: {FilePath}"));
        Assert.Equal("plain text", await File.ReadAllTextAsync(textFile));
    }

    [Fact]
    public async Task UpdateMetadataAsync_FileOutsideAllowedDirectories_IsRejectedAndLeftUntouched()
    {
        var service = CreateService();
        var outside = WriteCbzAt(Path.Combine(_root, "outside", "book.cbz"), Xml("Outside", "1", "Chapter 1"));
        var before = await File.ReadAllBytesAsync(outside);

        var result = await service.UpdateMetadataAsync(outside, new ComicMetadata { Series = "Hijacked" });

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot update metadata: file is outside the allowed directories: {FilePath}"));
        Assert.Equal(before, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task UpdateMetadataAsync_SymlinkInsideLibraryPointingOutside_IsRejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var outside = WriteCbzAt(Path.Combine(_root, "outside", "book.cbz"), Xml("Outside", "1", "Chapter 1"));
        var before = await File.ReadAllBytesAsync(outside);
        var link = Path.Combine(_library, "Series", "link.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, outside);

        var result = await service.UpdateMetadataAsync(link, new ComicMetadata { Series = "Hijacked" });

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task UpdateMetadataAsync_AllFields_RoundTripsIntoComicInfoAndKeepsPages()
    {
        var service = CreateService();
        var path = WriteCbz("Series/round.cbz", Xml("Old", "1", "Old Title"));
        var metadata = new ComicMetadata
        {
            Series = "New Series",
            Title = "New Title",
            Issue = "2",
            Volume = "4",
            Publisher = "Pub",
            Year = 2022,
            Summary = "Summary",
            Authors = { "Alice", "Bob" },
            Tags = { "t1", "t2" },
        };

        var result = await service.UpdateMetadataAsync(path, metadata);

        Assert.True(result);
        var info = ReadComicInfo(path)!;
        Assert.Equal("New Series", info.Element("Series")?.Value);
        Assert.Equal("New Title", info.Element("Title")?.Value);
        Assert.Equal("2", info.Element("Number")?.Value);
        Assert.Equal("4", info.Element("Volume")?.Value);
        Assert.Equal("Pub", info.Element("Publisher")?.Value);
        Assert.Equal("2022", info.Element("Year")?.Value);
        Assert.Equal("Summary", info.Element("Summary")?.Value);
        Assert.Equal(new[] { "Alice", "Bob" }, info.Elements("Writer").Select(e => e.Value));
        Assert.Equal(new[] { "t1", "t2" }, info.Elements("Tag").Select(e => e.Value));
        Assert.Contains("page001.jpg", EntryNames(path));
        _fileStore.Verify(
            f => f.RefreshFileStatsAsync(
                It.Is<IEnumerable<string>>(paths => paths.Single().EndsWith("round.cbz")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateMetadataAsync_EmptyMetadata_WritesComicInfoWithoutElements()
    {
        var service = CreateService();
        var path = WriteCbz("Series/blank.cbz", Xml("Old", "1", "Old Title"));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata());

        Assert.True(result);
        var info = ReadComicInfo(path)!;
        Assert.Empty(info.Elements());
    }

    [Fact]
    public async Task UpdateMetadataAsync_ArchiveWithEntryOver10MB_KeepsEveryEntryByteForByte()
    {
        var service = CreateService();
        var path = WriteCbzWithLargeEntry("Series/large.cbz");
        var before = SnapshotEntries(path);
        Assert.Contains(before, e => e.Name == "big.bin" && e.Length == LargeEntrySize);

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "Updated" });

        Assert.True(result);
        Assert.Equal("Updated", ReadComicInfo(path)!.Element("Series")?.Value);
        var after = SnapshotEntries(path);
        Assert.Equal("ComicInfo.xml", after[^1].Name);
        Assert.Equal(before.Where(e => e.Name != "ComicInfo.xml"), after.SkipLast(1));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task UpdateMetadataAsync_FileStatsRefreshFails_StillSucceeds()
    {
        _fileStore
            .Setup(f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store offline"));
        var service = CreateService();
        var path = WriteCbz("Series/refresh.cbz", Xml("Old", "1", "Chapter 1"));

        var result = await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "New" });

        Assert.True(result);
        Assert.Equal("New", ReadComicInfo(path)!.Element("Series")?.Value);
        Assert.True(_logger.Has(LogLevel.Debug, "Failed to refresh stored file stats for {FilePath}"));
    }

    [Fact]
    public async Task RemoveMetadataAsync_ArchiveWithEntryOver10MB_RemovesComicInfoAndKeepsEveryOtherEntryByteForByte()
    {
        var service = CreateService();
        var path = WriteCbzWithLargeEntry("Series/large.cbz");
        var before = SnapshotEntries(path);
        Assert.Contains(before, e => e.Name == "big.bin" && e.Length == LargeEntrySize);

        var result = await service.RemoveMetadataAsync(path);

        Assert.True(result);
        Assert.Equal(before.Where(e => e.Name != "ComicInfo.xml"), SnapshotEntries(path));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRewrite_EntryThatCannotBeRead_FailsAndLeavesArchiveUntouched(bool removeMetadata)
    {
        var service = CreateService();
        var path = WriteCbzWithUnreadableEntry("Series/unreadable.cbz");
        var before = await File.ReadAllBytesAsync(path);

        var result = removeMetadata
            ? await service.RemoveMetadataAsync(path)
            : await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "Updated" });

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(removeMetadata ? "Error removing metadata for {FilePath}" : "Error updating metadata for {FilePath}", error.Template);
        Assert.IsType<NotSupportedException>(error.Exception);
        Assert.Empty(Directory.GetFiles(_settings.TempFileDirectory));
        _fileStore.Verify(
            f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRewrite_EntryDataCorruptMidCopy_FailsAndLeavesArchiveUntouched(bool removeMetadata)
    {
        var service = CreateService();
        var path = WriteCbzWithCorruptDeflateEntry("Series/corrupt.cbz");
        var before = await File.ReadAllBytesAsync(path);

        var result = removeMetadata
            ? await service.RemoveMetadataAsync(path)
            : await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "Updated" });

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(removeMetadata ? "Error removing metadata for {FilePath}" : "Error updating metadata for {FilePath}", error.Template);
        Assert.NotNull(error.Exception);
        Assert.Empty(Directory.GetFiles(_settings.TempFileDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRewrite_EntryWithoutName_FailsAndLeavesArchiveUntouched(bool removeMetadata)
    {
        var service = CreateService();
        var path = WriteNamelessGzipCbz("Series/nameless.cbz");
        var before = await File.ReadAllBytesAsync(path);

        var result = removeMetadata
            ? await service.RemoveMetadataAsync(path)
            : await service.UpdateMetadataAsync(path, new ComicMetadata { Series = "Updated" });

        Assert.False(result);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(removeMetadata ? "Error removing metadata for {FilePath}" : "Error updating metadata for {FilePath}", error.Template);
        Assert.IsType<InvalidDataException>(error.Exception);
        Assert.Empty(Directory.GetFiles(_settings.TempFileDirectory));
    }

    [Fact]
    public async Task RemoveMetadataAsync_CorruptArchive_ReturnsFalseAndLogsError()
    {
        var service = CreateService();
        var path = WriteCorrupt("Series/bad.cbz");

        var result = await service.RemoveMetadataAsync(path);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Error, "Error removing metadata for {FilePath}"));
        Assert.Equal("this is not a comic archive", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RemoveMetadataAsync_FileOutsideAllowedDirectories_IsRejectedAndLeftUntouched()
    {
        var service = CreateService();
        var outside = WriteCbzAt(Path.Combine(_root, "outside", "book.cbz"), Xml("Outside", "1", "Chapter 1"));
        var before = await File.ReadAllBytesAsync(outside);

        var result = await service.RemoveMetadataAsync(outside);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot remove metadata: file is outside the allowed directories: {FilePath}"));
        Assert.Equal(before, await File.ReadAllBytesAsync(outside));
        _fileStore.Verify(
            f => f.RefreshFileStatsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveMetadataAsync_PathThroughLinkedDirectoryOutsideLibrary_IsRejectedAndLeftUntouched()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var outsideDir = Path.Combine(_root, "outside");
        var outside = WriteCbzAt(Path.Combine(outsideDir, "book.cbz"), Xml("Outside", "1", "Chapter 1"));
        var before = await File.ReadAllBytesAsync(outside);
        Directory.CreateSymbolicLink(Path.Combine(_library, "linked"), outsideDir);

        var result = await service.RemoveMetadataAsync(Path.Combine(_library, "linked", "book.cbz"));

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "Cannot remove metadata: file is outside the allowed directories: {FilePath}"));
        Assert.Equal(before, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task RemoveMetadataAsync_PathThroughLinkedDirectoryInsideLibrary_RewritesThePhysicalFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var physical = WriteCbz("Series/book.cbz", Xml("Old", "1", "Chapter 1"));
        var alias = Path.Combine(_library, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_library, "Series"));

        var result = await service.RemoveMetadataAsync(Path.Combine(alias, "book.cbz"));

        Assert.True(result);
        Assert.Null(ReadComicInfo(physical));
        Assert.Contains("page001.jpg", EntryNames(physical));
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
        _fileStore.Verify(
            f => f.RefreshFileStatsAsync(
                It.Is<IEnumerable<string>>(paths => paths.Single() == physical),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -------------------------------------------------------------- processing

    [Fact]
    public async Task ProcessFileAsync_FileOutsideAllowedDirectories_ReturnsFalseAndRecordsFailedMetric()
    {
        using var metrics = new ComicMaintainerMetrics();
        var measurements = new ConcurrentQueue<(long Value, string? Operation, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "comicmaintainer.files.processed")
            {
                return;
            }

            string? operation = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                {
                    operation = tag.Value as string;
                }
                else if (tag.Key == "outcome")
                {
                    outcome = tag.Value as string;
                }
            }

            measurements.Enqueue((value, operation, outcome));
        });
        listener.Start();

        var service = CreateService(metrics: metrics);
        var outside = WriteCbzAt(Path.Combine(_root, "outside", "book.cbz"), Xml("Outside", "1", "Chapter 1"));

        var result = await service.ProcessFileAsync(outside);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Warning, "ProcessFileAsync: File is outside the allowed directories: {FilePath}"));
        Assert.Contains(measurements, m => m.Value == 1 && m.Operation == "Process" && m.Outcome == "failed");
        _fileStore.Verify(f => f.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyProcessedInStore_SkipsWithoutTouchingFile()
    {
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        _fileStore.Setup(f => f.IsFileProcessedAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await service.ProcessFileAsync(path);

        Assert.True(result);
        Assert.True(File.Exists(path));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_UnreadableArchive_MarksNotNormalizedAndNotRenamed()
    {
        var service = CreateService();
        var path = WriteCorrupt("Series/bad.cbz");

        await service.ProcessFileAsync(path);

        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && !e.Success && e.ErrorMessage == "No metadata or series information");
        Assert.Contains(_historyEntries, e => e.Action == "Process" && !e.Success);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ProcessFileAsync_NormalizeDisabled_RenamesAndCarriesNormalizedFlagToNewPath()
    {
        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var expected = Path.Combine(_library, "Series", "Series - Chapter 0005.cbz");

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(path));
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.Success && e.ErrorMessage == "Normalize disabled");
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.UpdateFilePathAsync(path, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(expected, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(expected, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_historyEntries, e => e.Action == "Process" && e.Success);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task ProcessFileAsync_FullyProcessed_QueuesAutomaticEmail(int queued, bool expectInfoLog)
    {
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var email = new Mock<IComicEmailService>();
        email.Setup(e => e.QueueAutoSendAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queued);
        var service = CreateService(email: email.Object);
        var path = WriteCorrupt("Series/bad.cbz");

        var result = await service.ProcessFileAsync(path);

        Assert.True(result);
        email.Verify(e => e.QueueAutoSendAsync(path, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(expectInfoLog,
            _logger.Has(LogLevel.Information, "Queued {Count} automatic ereader delivery/deliveries for {FilePath}"));
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.Success && e.ErrorMessage == "Rename disabled");
        Assert.Contains(_historyEntries, e => e.Action == "Process" && e.Success);
    }

    [Fact]
    public async Task ProcessFileAsync_EmailQueueFails_LogsErrorAndStillSucceeds()
    {
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var email = new Mock<IComicEmailService>();
        email.Setup(e => e.QueueAutoSendAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp misconfigured"));
        var service = CreateService(email: email.Object);
        var path = WriteCbz("Series/book.cbz", Xml("Series", "1", "Chapter 1"));

        var result = await service.ProcessFileAsync(path);

        Assert.True(result);
        Assert.True(_logger.Has(LogLevel.Error, "Failed to queue automatic ereader delivery for {FilePath}"));
        email.Verify(e => e.QueueAutoSendAsync(path, "Series", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_EmailQueueCancelled_PropagatesAsProcessingFailure()
    {
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var email = new Mock<IComicEmailService>();
        email.Setup(e => e.QueueAutoSendAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = CreateService(email: email.Object);
        var path = WriteCbz("Series/book.cbz", Xml("Series", "1", "Chapter 1"));

        var result = await service.ProcessFileAsync(path);

        Assert.False(result);
        Assert.False(_logger.Has(LogLevel.Error, "Failed to queue automatic ereader delivery for {FilePath}"));
        Assert.True(_logger.Has(LogLevel.Error, "ProcessFileAsync: Error processing file: {FilePath}"));
    }

    [Fact]
    public async Task ProcessFileAsync_TargetAppearsDuringRename_IsTreatedAsDuplicate()
    {
        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var target = Path.Combine(_library, "Series", "Series - Chapter 0005.cbz");
        _logger.On(RenamingTemplate, _ => File.WriteAllText(target, "someone else's file"));

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(path));
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(target));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_historyEntries, e => e.Action == "Duplicate Detection" && e.ErrorMessage == "Target file already exists");
    }

    [Fact]
    public async Task ProcessFileAsync_SourceVanishesDuringRename_LogsRenameFailure()
    {
        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        _logger.On(RenamingTemplate, _ => File.Delete(path));

        await service.ProcessFileAsync(path);

        Assert.True(_logger.Has(LogLevel.Error, "Failed to rename file: {FilePath}"));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && !e.Success);
    }

    [Fact]
    public async Task ProcessFileAsync_SeriesFolderVanishesDuringRename_LogsRenameFailure()
    {
        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        _logger.On(RenamingTemplate, _ => Directory.Delete(Path.GetDirectoryName(path)!, recursive: true));

        await service.ProcessFileAsync(path);

        Assert.True(_logger.Has(LogLevel.Error, "Failed to rename file: {FilePath}"));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_LinkedDirectoryRepointedBeforeMove_RenamesTheValidatedFileAndKeepsTheRequestedPathForRecords()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var physical = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var outsideDir = Path.Combine(_root, "outside");
        var outside = WriteCbzAt(Path.Combine(outsideDir, "orig.cbz"), Xml("Outside", "1", "Chapter 1"));
        var alias = Path.Combine(_library, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_library, "Series"));
        var path = Path.Combine(alias, "orig.cbz");
        _logger.On(RenamingTemplate, _ =>
        {
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, outsideDir);
        });

        await service.ProcessFileAsync(path);

        Assert.False(File.Exists(physical));
        Assert.True(File.Exists(Path.Combine(_library, "Series", "Series - Chapter 0005.cbz")));
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(outsideDir, "Series - Chapter 0005.cbz")));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, Path.Combine(_library, "Series", "Series - Chapter 0005.cbz"), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_LinkedDirectoryRepointedBeforeMetadataRead_UsesTheCapturedArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        WriteCbz("SeriesA/orig.cbz", Xml("Series A", "5", "Chapter 5"));
        WriteCbz("SeriesB/orig.cbz", Xml("Series B", "9", "Chapter 9"));
        var alias = Path.Combine(_library, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_library, "SeriesA"));
        var path = Path.Combine(alias, "orig.cbz");
        _logger.On("ProcessFileAsync: Extracting metadata from archive: {FilePath}", _ =>
        {
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, Path.Combine(_library, "SeriesB"));
        });

        await service.ProcessFileAsync(path);

        var renamed = Path.Combine(_library, "SeriesA", "Series A - Chapter 0005.cbz");
        Assert.True(File.Exists(renamed));
        Assert.True(File.Exists(Path.Combine(_library, "SeriesB", "orig.cbz")));
        Assert.False(File.Exists(Path.Combine(_library, "SeriesB", "Series B - Chapter 0009.cbz")));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_LinkedDirectoryFallbackNamingUsesCallerPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        WriteCbz("Physical Series/Chapter 0005.cbz", comicInfoXml: null);
        var alias = Path.Combine(_library, "Caller Series");
        Directory.CreateSymbolicLink(alias, Path.Combine(_library, "Physical Series"));
        var path = Path.Combine(alias, "Chapter 0005.cbz");

        await service.ProcessFileAsync(path);

        var renamed = Path.Combine(_library, "Physical Series", "Caller Series - Chapter 0005.cbz");
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_FileSymlink_IsNotProcessedOrRenamed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var target = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var link = Path.Combine(_library, "alias.cbz");
        File.CreateSymbolicLink(link, target);

        var result = await service.ProcessFileAsync(link);

        Assert.False(result);
        Assert.True(File.Exists(target));
        Assert.Equal(target, new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)!.FullName);
        Assert.True(_logger.Has(LogLevel.Warning, "ProcessFileAsync: File symlinks are not processed: {FilePath}"));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_FolderReplacedByLinkAfterValidation_RenamesOnlyThroughTheCapturedDirectoryHandle()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var folder = Path.GetDirectoryName(path)!;
        var movedFolder = Path.Combine(_library, "Series-moved");
        var outsideDir = Path.Combine(_root, "outside");
        var outside = WriteCbzAt(Path.Combine(outsideDir, "orig.cbz"), Xml("Outside", "1", "Chapter 1"));
        _logger.On("ProcessFileAsync: Rename path validated: {FilePath}", _ =>
        {
            Directory.Move(folder, movedFolder);
            Directory.CreateSymbolicLink(folder, outsideDir);
        });

        await service.ProcessFileAsync(path);

        var renamed = Path.Combine(movedFolder, "Series - Chapter 0005.cbz");
        Assert.True(File.Exists(renamed));
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(outsideDir, "Series - Chapter 0005.cbz")));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_FolderReplacedByLinkOutsideLibraryBeforeRename_AbortsTheRename()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var folder = Path.GetDirectoryName(path)!;
        var movedFolder = Path.Combine(_library, "Series-moved");
        var outsideDir = Path.Combine(_root, "outside");
        var outside = WriteCbzAt(Path.Combine(outsideDir, "orig.cbz"), Xml("Outside", "1", "Chapter 1"));
        _logger.On("ProcessFileAsync: Attempting to rename file: {FilePath}", _ =>
        {
            Directory.Move(folder, movedFolder);
            Directory.CreateSymbolicLink(folder, outsideDir);
        });

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(outsideDir, "Series - Chapter 0005.cbz")));
        Assert.True(File.Exists(Path.Combine(movedFolder, "orig.cbz")));
        Assert.True(_logger.Has(LogLevel.Warning, "ProcessFileAsync: File path changed during processing, not renaming: {FilePath}"));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && !e.Success && e.ErrorMessage == "File path changed during processing");
    }

    [Fact]
    public async Task ProcessFileAsync_SymlinkedWatchedRoot_RenamesAndUpdatesRealFileStoreToConfiguredPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var watchedRoot = Path.Combine(_root, "watched-root");
        Directory.CreateSymbolicLink(watchedRoot, _library);
        _settings.WatchedDirectory = watchedRoot;
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        path = Path.Combine(watchedRoot, Path.GetRelativePath(_library, path));

        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var provider = services.BuildServiceProvider();
        var fileStore = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<FileStoreService>.Instance,
            provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>());
        await fileStore.AddFileAsync(path);
        var service = CreateService(fileStore: fileStore);

        await service.ProcessFileAsync(path);

        var expected = Path.Combine(watchedRoot, "Series", "Series - Chapter 0005.cbz");
        Assert.True(File.Exists(expected));
        var storedFile = Assert.Single(await fileStore.GetAllFilesAsync());
        Assert.Equal(expected, storedFile.FilePath);
        Assert.True(File.Exists(storedFile.FilePath));
    }

    [Fact]
    public async Task ProcessFileAsync_LiveDirectoryEndingInDeleted_RenamesSuccessfully()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var service = CreateService();
        var path = WriteCbz("Series (deleted)/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var expected = Path.Combine(_library, "Series (deleted)", "Series - Chapter 0005.cbz");

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(path));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_Renameat2Unsupported_FallsBackToLinkAndUnlink()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var renamed = Path.Combine(_library, "Series", "Series - Chapter 0005.cbz");
        var service = CreateUnsupportedRenameAt2Service();

        await service.ProcessFileAsync(path);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(renamed));
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(path, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_Renameat2UnsupportedAndTargetAppears_DoesNotOverwriteTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        _settings.WatcherEnableNormalize = false;
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        var renamed = Path.Combine(_library, "Series", "Series - Chapter 0005.cbz");
        var service = CreateUnsupportedRenameAt2Service();
        _logger.On("ProcessFileAsync: Rename path validated: {FilePath}", _ =>
            File.WriteAllText(renamed, "pre-existing target"));

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(path));
        Assert.Equal("pre-existing target", await File.ReadAllTextAsync(renamed));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_FileStoreThrows_ReturnsFalseAndExcludesFromErrorReports()
    {
        _fileStore.Setup(f => f.IsFileProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));

        var result = await service.ProcessFileAsync(path);

        Assert.False(result);
        Assert.True(_logger.Has(LogLevel.Error, "ProcessFileAsync: Error processing file: {FilePath}"));
        Assert.Contains(
            _logger.Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>(),
            scope => scope.Any(kv => kv.Key == ErrorReportLogProperties.ExcludeFromErrorReports));
        Assert.Contains(_historyEntries, e => e.Action == "Process" && !e.Success && e.ErrorMessage == "db down");
    }

    [Fact]
    public async Task ProcessFileAsync_SeriesCacheThrows_FallsBackToFileMetadataAndSkipsVersionStamp()
    {
        var cache = CreateCache();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));
        var service = CreateService(cache: cache.Object);
        var path = WriteCbz("Series/Series - Chapter 0005.cbz", Xml("Series", "5", "Chapter 5"));

        var result = await service.ProcessFileAsync(path);

        Assert.True(result);
        Assert.True(File.Exists(path));
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to consult series metadata cache for user-selected name {SeriesName}"));
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to consult series metadata cache for matched title {SeriesName}"));
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to consult series metadata cache for cleared-status check on {SeriesName}"));
        Assert.True(_logger.Has(LogLevel.Debug, "Failed to stamp series-metadata version on {FilePath}"));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(
            f => f.SetFileSeriesMetadataVersionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessFileAsync_AlreadyNormalized_StampsCacheVersionWhenRecordExists(bool recordExists)
    {
        var cache = CreateCache();
        cache.Setup(c => c.GetAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(recordExists ? new SeriesMetadataCacheRecord { MetadataVersion = 7 } : null);
        var service = CreateService(cache: cache.Object);
        var path = WriteCbz("Series/Series - Chapter 0005.cbz", Xml("Series", "5", "Chapter 5"));

        await service.ProcessFileAsync(path);

        _fileStore.Verify(
            f => f.SetFileSeriesMetadataVersionAsync(path, 7, It.IsAny<CancellationToken>()),
            recordExists ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task ProcessFileAsync_CacheKeyNormalizesToEmpty_NeverQueriesCache()
    {
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.NormalizeKey(It.IsAny<string?>())).Returns(string.Empty);
        var service = CreateService(cache: cache.Object);
        var path = WriteCbz("Series/Series - Chapter 0005.cbz", Xml("Series", "5", "Chapter 5"));

        var result = await service.ProcessFileAsync(path);

        Assert.True(result);
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(
            f => f.SetFileSeriesMetadataVersionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("Resolved Name", "Resolved Name")]
    [InlineData("   ", "Unknown Series")]
    public async Task ProcessFileAsync_SeriesNameResolver_DrivesNormalizedSeriesAndFilename(string resolvedSeries, string expectedSeries)
    {
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = resolvedSeries });
        var service = CreateService(resolver: resolver.Object);
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "1", "Chapter 1"));
        var expected = Path.Combine(_library, "Series", $"{expectedSeries} - Chapter 0001.cbz");

        await service.ProcessFileAsync(path);

        Assert.True(File.Exists(expected));
        Assert.Equal(expectedSeries, ReadComicInfo(expected)!.Element("Series")?.Value);
    }

    // ------------------------------------------------------------------ rename

    [Fact]
    public async Task RenameFilesAsync_AlreadyRenamedInStore_SkipsFile()
    {
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        _fileStore.Setup(f => f.IsFileRenamedAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RenameFilesAsync_MissingOrNonArchiveFiles_FailWithHistory()
    {
        var service = CreateService();
        var textFile = Path.Combine(_library, "notes.txt");
        await File.WriteAllTextAsync(textFile, "text");
        var missing = Path.Combine(_library, "missing.cbz");

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { missing, textFile }));

        Assert.Equal(2, job.FailedFiles);
        Assert.Equal("Rename failed", job.Errors[missing]);
        Assert.Equal(2, _historyEntries.Count(e => e.ErrorMessage == "File not found or not a comic archive"));
    }

    [Fact]
    public async Task RenameFilesAsync_NoUsableSeries_FailsAndMarksNotRenamed()
    {
        var service = CreateService();
        var corrupt = WriteCorrupt("Series/bad.cbz");
        var numberOnly = WriteCbz("Series/number-only.cbz", "<ComicInfo><Number>3</Number></ComicInfo>");

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { corrupt, numberOnly }));

        Assert.Equal(2, job.FailedFiles);
        Assert.True(File.Exists(corrupt));
        Assert.True(File.Exists(numberOnly));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(corrupt, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(numberOnly, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _historyEntries.Count(e => e.ErrorMessage == "No metadata or series information found"));
    }

    [Fact]
    public async Task RenameFilesAsync_FileStoreThrows_FailsWithError()
    {
        _fileStore.Setup(f => f.IsFileRenamedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.True(_logger.Has(LogLevel.Error, "RenameFileAsync: Error renaming file: {FilePath}"));
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.ErrorMessage == "db down");
    }

    [Fact]
    public async Task RenameFilesAsync_SourceVanishesDuringMove_FailsWithError()
    {
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));
        _logger.On(RenamingTemplate, _ => File.Delete(path));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.True(_logger.Has(LogLevel.Error, "RenameFileAsync: Error renaming file: {FilePath}"));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenameFilesAsync_FilenameGenerationFails_CountsAsFailedAndIsNotMarkedRenamed()
    {
        _settings.FilenameFormat = "{series}";
        _settings.IssueNumberPadding = int.MaxValue;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.True(_logger.Has(LogLevel.Error, "Error generating filename"));
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, true, It.IsAny<CancellationToken>()), Times.Never);
        var rename = Assert.Single(_historyEntries, e => e.Action == "Rename");
        Assert.False(rename.Success);
        Assert.Equal("Failed to generate new filename", rename.ErrorMessage);
    }

    [Fact]
    public async Task ProcessFileAsync_FilenameGenerationFails_RecordsRenameFailureAndIsNotFullyProcessed()
    {
        _settings.WatcherEnableNormalize = false;
        _settings.FilenameFormat = "{series}";
        _settings.IssueNumberPadding = int.MaxValue;
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Series", "5", "Chapter 5"));

        await service.ProcessFileAsync(path);

        Assert.True(_logger.Has(LogLevel.Error, "Error generating filename"));
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(path, true, It.IsAny<CancellationToken>()), Times.Never);
        var rename = Assert.Single(_historyEntries, e => e.Action == "Rename");
        Assert.False(rename.Success);
        Assert.Equal("Failed to generate new filename", rename.ErrorMessage);
        Assert.Contains(_historyEntries, e => e.Action == "Process" && !e.Success);
    }

    [Theory]
    [InlineData("{series}", "Series", "5", "Series - Chapter 0005.cbz")]
    [InlineData("{series}", "Series", "5.5", "Series - Chapter 0005.5.cbz")]
    [InlineData("{series}", "Series", "5.50", "Series - Chapter 0005.5.cbz")]
    [InlineData("{series}", "Series", "5.0", "Series - Chapter 0005.cbz")]
    [InlineData("{series}", "Series", "Extra", "Series - Chapter Extra.cbz")]
    [InlineData("{title}", "Series", "5", "Chapter 0005.cbz")]
    [InlineData("{series}", "Series 2", "5", "Series 2 - Chapter 0005.cbz")]
    public async Task RenameFilesAsync_TemplateWithoutIssue_AppendsChapterToPreserveIt(
        string template, string series, string issue, string expectedName)
    {
        _settings.FilenameFormat = template;
        var service = CreateService();
        var path = WriteCbz($"{series}/orig.cbz", Xml(series, issue, title: null));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, series, expectedName)), $"expected {expectedName}");
        Assert.True(_logger.Has(LogLevel.Warning,
            "Chapter number '{Chapter}' would be lost from filename during rename of {OriginalPath}; appending it to preserve the chapter."));
    }

    [Fact]
    public async Task RenameFilesAsync_RecoveredChapterCannotBeWritten_WarnsAndStillRenames()
    {
        var tempAsFile = Path.Combine(_root, "temp-is-a-file");
        await File.WriteAllTextAsync(tempAsFile, "blocking");
        _settings.TempFileDirectory = tempAsFile;
        var service = CreateService();
        var path = WriteCbz("Series/Series - Ch 5.cbz", Xml("Series", number: null, title: null));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to write recovered chapter number '{Chapter}' to metadata for {FilePath}."));
        Assert.True(File.Exists(Path.Combine(_library, "Series", "Series - Chapter 0005.cbz")));
    }

    // --------------------------------------------------------------- normalize

    [Fact]
    public async Task NormalizeFilesAsync_AlreadyNormalizedInStore_SkipsFile()
    {
        var service = CreateService();
        var path = WriteCbz("Series/orig.cbz", Xml("Wrong", "5", "Wrong"));
        _fileStore.Setup(f => f.IsFileNormalizedAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var before = await File.ReadAllBytesAsync(path);

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task NormalizeFilesAsync_MissingOrNonArchiveFiles_MarkedNotNormalized()
    {
        var service = CreateService();
        var textFile = Path.Combine(_library, "notes.txt");
        await File.WriteAllTextAsync(textFile, "text");
        var missing = Path.Combine(_library, "missing.cbz");

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { missing, textFile }));

        Assert.Equal(2, job.FailedFiles);
        Assert.Equal("Normalize failed", job.Errors[textFile]);
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(missing, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(textFile, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_UnreadableArchive_ReportsNoMetadata()
    {
        var service = CreateService();
        var path = WriteCorrupt("Series/bad.cbz");

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.ErrorMessage == "No metadata found");
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_MetadataAlreadyNormalized_LeavesArchiveUntouched()
    {
        var service = CreateService();
        var path = WriteCbz("Series X/book.cbz", Xml("Series X", "5", "Chapter 5"));
        var before = await File.ReadAllBytesAsync(path);

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.Success && e.ErrorMessage == "File already normalized");
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_MetadataWriteFails_ReportsFailure()
    {
        var tempAsFile = Path.Combine(_root, "temp-is-a-file");
        await File.WriteAllTextAsync(tempAsFile, "blocking");
        _settings.TempFileDirectory = tempAsFile;
        var service = CreateService();
        var path = WriteCbz("Series X/book.cbz", Xml("Series X", "5", "Wrong"));

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.True(_logger.Has(LogLevel.Warning, "NormalizeFileAsync: Failed to normalize file: {FilePath}"));
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.ErrorMessage == "Failed to update metadata");
        Assert.Equal("Wrong", ReadComicInfo(path)!.Element("Title")?.Value);
    }

    [Fact]
    public async Task NormalizeFilesAsync_FileStoreThrows_FailsWithError()
    {
        _fileStore.Setup(f => f.IsFileNormalizedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var service = CreateService();
        var path = WriteCbz("Series X/book.cbz", Xml("Series X", "5", "Wrong"));

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.True(_logger.Has(LogLevel.Error, "NormalizeFileAsync: Error normalizing file: {FilePath}"));
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.ErrorMessage == "db down");
    }

    [Fact]
    public async Task NormalizeFilesAsync_TitleOnlyMetadata_FillsSeriesIssueAndTitleFromPath()
    {
        var service = CreateService();
        var path = WriteCbz("Series X/Series X Ch 7.cbz", Xml(series: null, number: null, title: "Something"));

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        var info = ReadComicInfo(path)!;
        Assert.Equal("Series X", info.Element("Series")?.Value);
        Assert.Equal("7", info.Element("Number")?.Value);
        Assert.Equal("Chapter 7", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task NormalizeFilesAsync_TitleAndIssueWithoutSeries_FillsSeriesFromFolder()
    {
        var service = CreateService();
        var path = WriteCbz("Series X/book.cbz", Xml(series: null, number: "5", title: "Some Title"));

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        var info = ReadComicInfo(path)!;
        Assert.Equal("Series X", info.Element("Series")?.Value);
        Assert.Equal("5", info.Element("Number")?.Value);
        Assert.Equal("Chapter 5", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task NormalizeFilesAsync_WhitespaceFolderNameAndNoSeries_FallsBackToUnknownSeries()
    {
        // Windows trims trailing spaces from path segments, so a whitespace-only folder needs Linux.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var service = CreateService();
        var path = WriteCbz(" /book.cbz", Xml(series: null, number: "5", title: "Some Title"));

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        var info = ReadComicInfo(path)!;
        Assert.Equal("Unknown Series", info.Element("Series")?.Value);
        Assert.Equal("Chapter 5", info.Element("Title")?.Value);
    }

    // -------------------------------------------------------- series resolution

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameFilesAsync_LocalizedSeriesTitle_IsResolvedThroughFolderRecord(bool userSelectedRecord)
    {
        var cache = CreateCache();
        var localized = new List<LocalizedTitle>
        {
            null!,
            new("  ", "en"),
            new("Other", "ja"),
            new("Titre Local", "fr"),
        };
        var record = userSelectedRecord
            ? new SeriesMetadataCacheRecord { CanonicalTitle = string.Empty, SeriesName = "Pinned Name", LocalizedTitles = localized }
            : new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical Title", LookupStatus = "success", LocalizedTitles = localized };
        var folderLookups = 0;
        cache.Setup(c => c.GetAsync("folder name", It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<SeriesMetadataCacheRecord?>(
                Interlocked.Increment(ref folderLookups) >= 3 ? record : null));
        var service = CreateService(cache: cache.Object);
        var path = WriteCbz("Folder Name/source.cbz", Xml("Titre Local", "1", "Chapter 1"));
        var expectedSeries = userSelectedRecord ? "Pinned Name" : "Canonical Title";

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, "Folder Name", $"{expectedSeries} - Chapter 0001.cbz")));
        if (userSelectedRecord)
        {
            cache.Verify(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            cache.Verify(c => c.SetUserAliasesAsync(
                "Canonical Title",
                It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "Folder Name" })),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task RenameFilesAsync_ExternalLookupWithPreferredLanguage_UsesLocalizedTitleEvenIfPersistFails()
    {
        _settings.DefaultPreferredLanguage = "ja";
        var cache = CreateCache();
        cache.Setup(c => c.PersistExternalLookupAsync(It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));
        var external = new Mock<IExternalSeriesMetadataService>();
        external.Setup(e => e.LookupSeriesAsync("Series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "  External Title  ",
                LocalizedTitles = new List<LocalizedTitle> { null!, new("  ", "ja"), new("Gaibu Title", "ja") },
            });
        var service = CreateService(cache: cache.Object, external: external.Object);
        var path = WriteCbz("Series/source.cbz", Xml("Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to persist external lookup for series {SeriesName}"));
        Assert.True(File.Exists(Path.Combine(_library, "Series", "Gaibu Title - Chapter 0001.cbz")));
        cache.Verify(c => c.SetUserAliasesAsync(
            "External Title",
            It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "Series" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameFilesAsync_PersistedRecordWithoutTitle_FallsBackToUnknownSeries()
    {
        var cache = CreateCache();
        cache.Setup(c => c.PersistExternalLookupAsync(It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = string.Empty });
        var external = new Mock<IExternalSeriesMetadataService>();
        external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "External Title" });
        var service = CreateService(cache: cache.Object, external: external.Object);
        var path = WriteCbz("Series/source.cbz", Xml("Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, "Series", "Unknown Series - Chapter 0001.cbz")));
    }

    [Theory]
    [InlineData("canonical-matches-folder", null)]
    [InlineData("provider-alias-matches", null)]
    [InlineData("user-alias-matches", null)]
    [InlineData("null-alias-lists", "Folder Name")]
    [InlineData("unrelated-aliases", "Folder Name")]
    [InlineData("existing-user-alias", "Old Alias|Folder Name")]
    [InlineData("no-record", "Folder Name")]
    [InlineData("save-fails", "Folder Name")]
    public async Task RenameFilesAsync_MatchedSeries_LearnsFolderNameAsAliasOnlyWhenUnknown(string scenario, string? expectedAliases)
    {
        var cache = CreateCache();
        cache.Setup(c => c.GetAsync("folder name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", LookupStatus = "success" });
        SeriesMetadataCacheRecord? canonicalRecord = scenario switch
        {
            "canonical-matches-folder" => new SeriesMetadataCacheRecord { CanonicalTitle = "folder name" },
            "provider-alias-matches" => new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", Aliases = { "FOLDER NAME" } },
            "user-alias-matches" => new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", UserAliases = { "folder name" } },
            "null-alias-lists" => new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", Aliases = null!, UserAliases = null! },
            "unrelated-aliases" => new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", Aliases = { "Other" } },
            "existing-user-alias" => new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", UserAliases = { "Old Alias" } },
            _ => null,
        };
        cache.Setup(c => c.GetAsync("canonical", It.IsAny<CancellationToken>())).ReturnsAsync(canonicalRecord);
        if (scenario == "save-fails")
        {
            cache.Setup(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("write failed"));
        }

        var service = CreateService(cache: cache.Object);
        var path = WriteCbz("Folder Name/source.cbz", Xml("Folder Name", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, "Folder Name", "Canonical - Chapter 0001.cbz")));
        if (expectedAliases is null)
        {
            cache.Verify(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var aliases = expectedAliases.Split('|');
            cache.Verify(c => c.SetUserAliasesAsync(
                "Canonical",
                It.Is<IEnumerable<string>>(a => a.SequenceEqual(aliases)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        Assert.Equal(scenario == "save-fails",
            _logger.Has(LogLevel.Warning, "Failed to add folder name {Folder} as alias for series {Series}"));
    }

    [Fact]
    public async Task RenameFilesAsync_CacheKeysNormalizeToEmpty_UsesExternalTitleWithoutTouchingCache()
    {
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.NormalizeKey(It.IsAny<string?>())).Returns(string.Empty);
        var external = new Mock<IExternalSeriesMetadataService>();
        external.Setup(e => e.LookupSeriesAsync("Series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "External Title" });
        var service = CreateService(cache: cache.Object, external: external.Object);
        var path = WriteCbz("Series/source.cbz", Xml("Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, "Series", "External Title - Chapter 0001.cbz")));
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        cache.Verify(c => c.SetUserAliasesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenameFilesAsync_ExternalLookupThrows_KeepsMetadataSeries()
    {
        var cache = CreateCache();
        var external = new Mock<IExternalSeriesMetadataService>();
        external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));
        var service = CreateService(cache: cache.Object, external: external.Object);
        var path = WriteCbz("Folder Name/source.cbz", Xml("Meta Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(_logger.Has(LogLevel.Warning, "Failed to resolve external metadata for series {SeriesName}"));
        Assert.True(File.Exists(Path.Combine(_library, "Folder Name", "Meta Series - Chapter 0001.cbz")));
    }

    [Theory]
    [InlineData("cleared", 0)]
    [InlineData("pending", 1)]
    public async Task RenameFilesAsync_ClearedCacheRecord_SuppressesExternalLookup(string lookupStatus, int expectedLookups)
    {
        var cache = CreateCache();
        cache.Setup(c => c.GetAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { CanonicalTitle = "Cached Title", LookupStatus = lookupStatus });
        var external = new Mock<IExternalSeriesMetadataService>();
        var service = CreateService(cache: cache.Object, external: external.Object);
        var path = WriteCbz("Series/source.cbz", Xml("Series", "1", "Chapter 1"));

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(Path.Combine(_library, "Series", "Series - Chapter 0001.cbz")));
        external.Verify(e => e.LookupSeriesAsync("Series", It.IsAny<CancellationToken>()), Times.Exactly(expectedLookups));
    }

    // ----------------------------------------------------------------- helpers

    private ComicProcessorService CreateService(
        ISeriesMetadataCacheService? cache = null,
        ISeriesNameResolver? resolver = null,
        IExternalSeriesMetadataService? external = null,
        IComicEmailService? email = null,
        ComicMaintainerMetrics? metrics = null,
        IFileStoreService? fileStore = null)
    {
        var service = new ComicProcessorService(
            new TestOptionsMonitor<AppSettings>(_settings),
            _logger,
            fileStore ?? _fileStore.Object,
            _history.Object,
            eventBroadcaster: _broadcaster.Object,
            externalSeriesMetadata: external,
            seriesMetadataCache: cache,
            seriesNameResolver: resolver,
            comicEmail: email,
            metrics: metrics);
        _services.Add(service);
        return service;
    }

    private ComicProcessorService CreateUnsupportedRenameAt2Service()
    {
        var service = new UnsupportedRenameAt2ComicProcessorService(
            _settings,
            _logger,
            _fileStore.Object,
            _history.Object,
            _broadcaster.Object);
        _services.Add(service);
        return service;
    }

    private static Mock<ISeriesMetadataCacheService> CreateCache()
    {
        var cache = new Mock<ISeriesMetadataCacheService>();
        cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns<string?>(title => (title ?? string.Empty).Trim().ToLowerInvariant());
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        return cache;
    }

    private TaskCompletionSource<string> JobSignal(Guid jobId) =>
        _jobSignals.GetOrAdd(jobId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<ProcessingJob> RunJobAsync(ComicProcessorService service, Func<ComicProcessorService, Task<Guid>> start)
    {
        var jobId = await start(service);
        await JobSignal(jobId).Task.WaitAsync(TimeSpan.FromSeconds(30));
        return service.GetJob(jobId)!;
    }

    private string WriteCbz(string relativePath, string? comicInfoXml) =>
        WriteCbzAt(Path.Combine(_library, relativePath), comicInfoXml);

    private static string WriteCbzAt(string path, string? comicInfoXml)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (comicInfoXml != null)
            {
                var info = zip.CreateEntry("ComicInfo.xml");
                using var writer = new StreamWriter(info.Open());
                writer.Write(comicInfoXml);
            }

            var page = zip.CreateEntry("page001.jpg");
            using var stream = page.Open();
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        return path;
    }

    /// <summary>
    /// Pages around an 11 MB entry (zeros, so it compresses to almost nothing), with ComicInfo.xml
    /// in the middle and distinct old timestamps, so entry order and timestamps are observable.
    /// </summary>
    private string WriteCbzWithLargeEntry(string relativePath)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FixtureTimestamp);
            AddEntry(zip, "ComicInfo.xml", Encoding.UTF8.GetBytes(Xml("Old", "1", "Chapter 1")), FixtureTimestamp.AddDays(1));
            AddEntry(zip, "big.bin", new byte[LargeEntrySize], FixtureTimestamp.AddDays(2));
            AddEntry(zip, "extras/page002.jpg", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, FixtureTimestamp.AddDays(3));
        }

        return path;
    }

    /// <summary>
    /// A valid archive whose "unreadable.bin" entry declares the reserved compression method 11
    /// in its local and central directory headers, so no reader can decompress it.
    /// </summary>
    private string WriteCbzWithUnreadableEntry(string relativePath)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "ComicInfo.xml", Encoding.UTF8.GetBytes(Xml("Old", "1", "Chapter 1")), FixtureTimestamp);
            AddEntry(zip, "page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FixtureTimestamp);
            AddEntry(zip, "unreadable.bin", new byte[] { 1, 2, 3, 4 }, FixtureTimestamp, CompressionLevel.NoCompression);
        }

        var bytes = File.ReadAllBytes(path);
        var name = Encoding.UTF8.GetBytes("unreadable.bin");
        var patched = 0;
        for (var i = 0; i + 46 + name.Length <= bytes.Length; i++)
        {
            var (methodOffset, nameOffset) = BitConverter.ToUInt32(bytes, i) switch
            {
                0x04034b50 => (8, 30), // local file header
                0x02014b50 => (10, 46), // central directory header
                _ => (-1, -1),
            };
            if (methodOffset >= 0 && bytes.AsSpan(i + nameOffset, name.Length).SequenceEqual(name))
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i + methodOffset, 2), (ushort)11);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// A valid archive whose deflated "corrupt.bin" entry starts with a reserved deflate block type,
    /// so decompression fails while the entry is being copied (after its header was written).
    /// </summary>
    private string WriteCbzWithCorruptDeflateEntry(string relativePath)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "ComicInfo.xml", Encoding.UTF8.GetBytes(Xml("Old", "1", "Chapter 1")), FixtureTimestamp);
            AddEntry(zip, "page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FixtureTimestamp);
            AddEntry(zip, "corrupt.bin", new byte[64 * 1024], FixtureTimestamp);
        }

        var bytes = File.ReadAllBytes(path);
        var name = Encoding.UTF8.GetBytes("corrupt.bin");
        var patched = 0;
        for (var i = 0; i + 30 + name.Length <= bytes.Length; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) == 0x04034b50 && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
            {
                Assert.Equal(8, BitConverter.ToUInt16(bytes, i + 8)); // deflate
                var dataStart = i + 30 + BitConverter.ToUInt16(bytes, i + 26) + BitConverter.ToUInt16(bytes, i + 28);
                bytes[dataStart] = 0xFF; // BFINAL=1, BTYPE=11 (reserved)
                patched++;
            }
        }

        Assert.Equal(1, patched);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// A ".cbz" that really is a gzip stream without a stored file name: SharpCompress opens it as
    /// an archive with a single entry whose Key is null.
    /// </summary>
    private string WriteNamelessGzipCbz(string relativePath)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        return path;
    }

    private static void AddEntry(
        ZipArchive zip, string name, byte[] content, DateTimeOffset lastWriteTime, CompressionLevel level = CompressionLevel.Fastest)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = lastWriteTime;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private string WriteCorrupt(string relativePath)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a comic archive");
        return path;
    }

    private static string Xml(string? series, string? number, string? title, params (string Name, string Value)[] extra)
    {
        var root = new XElement("ComicInfo");
        if (series != null)
        {
            root.Add(new XElement("Series", series));
        }

        if (number != null)
        {
            root.Add(new XElement("Number", number));
        }

        if (title != null)
        {
            root.Add(new XElement("Title", title));
        }

        foreach (var (name, value) in extra)
        {
            root.Add(new XElement(name, value));
        }

        return root.ToString();
    }

    private static XElement? ReadComicInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("ComicInfo.xml");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XElement.Load(stream);
    }

    private static List<string> EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static List<EntrySnapshot> SnapshotEntries(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries
            .Select(e =>
            {
                using var stream = e.Open();
                return new EntrySnapshot(e.FullName, e.LastWriteTime.DateTime, e.Length, Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToList();
    }

    private sealed record EntrySnapshot(string Name, DateTime LastWriteTime, long Length, string Sha256);

    private sealed record LogEntry(LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

    private sealed class UnsupportedRenameAt2ComicProcessorService : ComicProcessorService
    {
        public UnsupportedRenameAt2ComicProcessorService(
            AppSettings settings,
            ILogger<ComicProcessorService> logger,
            IFileStoreService fileStore,
            IProcessingHistoryService history,
            IEventBroadcaster eventBroadcaster)
            : base(new TestOptionsMonitor<AppSettings>(settings), logger, fileStore, history, eventBroadcaster)
        {
        }

        protected override int RenameLinuxNoReplace(
            int sourceDirectory,
            string sourceName,
            int targetDirectory,
            string targetName,
            uint flags)
        {
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(38);
            return -1;
        }
    }

    private sealed class HookLogger : ILogger<ComicProcessorService>
    {
        private readonly ConcurrentDictionary<string, Action<LogEntry>> _hooks = new();

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ConcurrentQueue<object> Scopes { get; } = new();

        public void On(string template, Action<LogEntry> hook) => _hooks[template] = hook;

        public bool Has(LogLevel level, string template) =>
            Entries.Any(e => e.Level == level && e.Template == template);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Enqueue(state);
            return NoopScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var template = string.Empty;
            var properties = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == "{OriginalFormat}")
                    {
                        template = pair.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        properties[pair.Key] = pair.Value;
                    }
                }
            }

            var entry = new LogEntry(logLevel, template, properties, exception);
            Entries.Enqueue(entry);
            if (_hooks.TryGetValue(template, out var hook))
            {
                hook(entry);
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
