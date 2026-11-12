using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StoredRar4 = ComicMaintainer.Tests.Services.ComicReaderServiceCoverageTests.StoredRar4;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Processing a file fills its empty ComicInfo fields from the series' provider match, never
/// changes a value already there, and leaves nothing that would make the file look edited or
/// out of date.
/// </summary>
public sealed class ComicProcessorServiceProviderFillTests : IDisposable
{
    private const string Normalized = "<ComicInfo><Title>The Black Swordsman</Title><Series>Berserk</Series><Number>1</Number></ComicInfo>";

    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly List<ComicProcessorService> _services = new();

    private SeriesMetadataCacheRecord _record = new()
    {
        NormalizedKey = "berserk",
        CanonicalTitle = "Berserk",
        ResolvedSeriesName = "Berserk",
        LookupStatus = "success",
        Synopsis = "<p>Guts, the **Black Swordsman**.</p>",
        Genres = new List<string> { "Action", "Dark Fantasy" },
        SiteUrl = "https://anilist.co/manga/30002",
        PublicationStatus = SeriesPublicationStatus.Completed,
        TotalChapters = 364,
        Origin = "ja",
        MetadataVersion = 7
    };

    public ComicProcessorServiceProviderFillTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cps-fill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = AppSettings.DefaultFilenameFormat,
            IssueNumberPadding = 4,
            WatcherEnableRename = false
        };
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _record);
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _record);
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>())).Returns("berserk");
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TestIO.TryDeleteDirectory(_root);
    }

    private ComicProcessorService CreateService()
    {
        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Berserk", WinningStep = SeriesNameResolutionStep.MatchedCache });
        var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            _history.Object,
            seriesMetadataCache: _cache.Object,
            seriesNameResolver: resolver.Object,
            jobRegistry: Jobs);
        _services.Add(service);
        return service;
    }

    private string WriteArchive(string comicInfo, string relativePath = "Berserk/Berserk - Chapter 0001.cbz")
    {
        var path = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write(comicInfo);
        }

        using var page = zip.CreateEntry("page001.jpg").Open();
        page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        return path;
    }

    private string WriteCbr(string comicInfo, string relativePath = "Berserk/Berserk - Chapter 0001.cbr")
    {
        var path = Path.GetFullPath(Path.Join(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StoredRar4.Write(
            path,
            ("page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }),
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(comicInfo)));
        return path;
    }

    private static XElement ReadComicInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("ComicInfo.xml")!.Open());
        return XElement.Parse(reader.ReadToEnd());
    }

    private void TrackFile(string path, ComicMetadataFieldFlags lockedFields, bool userEdited = true)
    {
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile
            {
                FilePath = path,
                Metadata = new ComicMetadata { IsUserEdited = userEdited, UserLockedFieldsMask = (long)lockedFields }
            });
    }

    private static void AssertFilled(XElement info)
    {
        Assert.Equal("Guts, the Black Swordsman.", info.Element("Summary")?.Value);
        Assert.Equal("Action, Dark Fantasy", info.Element("Genre")?.Value);
        Assert.Equal("https://anilist.co/manga/30002", info.Element("Web")?.Value);
        Assert.Equal("364", info.Element("Count")?.Value);
        Assert.Equal("YesAndRightToLeft", info.Element("Manga")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyNormalizedFile_FillsTheEmptyFields()
    {
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        AssertFilled(info);
        Assert.Equal("The Black Swordsman", info.Element("Title")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_FileNeedingNormalize_GetsBothInOneWrite()
    {
        var path = WriteArchive("<ComicInfo><Series>Wrong Name</Series><Number>1</Number></ComicInfo>");

        Assert.True(await CreateService().ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        AssertFilled(info);
        Assert.Equal("Berserk", info.Element("Series")?.Value);
        _fileStore.Verify(
            f => f.RecordArchiveMetadataAsync(path, It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_AlreadyNormalizedFile_FillsTheEmptyFields()
    {
        var path = WriteArchive(Normalized);

        var job = await NormalizeAsync(path);

        Assert.Equal(0, job.FailedFiles);
        AssertFilled(ReadComicInfo(path));
    }

    [Fact]
    public async Task NormalizeFilesAsync_FileNeedingNormalize_GetsBothInOneWrite()
    {
        var path = WriteArchive("<ComicInfo><Series>Wrong Name</Series><Number>1</Number></ComicInfo>");

        var job = await NormalizeAsync(path);

        Assert.Equal(0, job.FailedFiles);
        var info = ReadComicInfo(path);
        AssertFilled(info);
        Assert.Equal("Berserk", info.Element("Series")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyNormalizedCbr_BecomesAFilledCbzAndTheRowFollowsIt()
    {
        var cbr = WriteCbr(Normalized);
        var cbz = Path.ChangeExtension(cbr, ".cbz");

        Assert.True(await CreateService().ProcessFileAsync(cbr));

        Assert.False(File.Exists(cbr));
        AssertFilled(ReadComicInfo(cbz));
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileRenamedAsync(cbz, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbz, 7, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbr, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // Converting and filling changed the file, so the run has a history entry.
        _history.Verify(
            h => h.AddHistoryEntryAsync(
                It.Is<ProcessingHistoryEntry>(entry => entry.Action == "Process" && entry.Success && entry.FilePath == cbz),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_AlreadyNormalizedCbr_IsRenamedAsTheCbz()
    {
        _settings.WatcherEnableRename = true;
        var cbr = WriteCbr(Normalized, "Berserk/Berserk Ch 1.cbr");
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var renamed = Path.Join(_root, "Berserk", "Berserk - Chapter 0001.cbz");

        Assert.True(await CreateService().ProcessFileAsync(cbr));

        Assert.False(File.Exists(cbr));
        Assert.False(File.Exists(cbz));
        AssertFilled(ReadComicInfo(renamed));
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbz, renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(renamed, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_AlreadyNormalizedCbr_BecomesAFilledCbzAndTheRowFollowsIt()
    {
        var cbr = WriteCbr(Normalized);
        var cbz = Path.ChangeExtension(cbr, ".cbz");

        var job = await NormalizeAsync(cbr);

        Assert.Equal(0, job.FailedFiles);
        Assert.False(File.Exists(cbr));
        AssertFilled(ReadComicInfo(cbz));
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbz, 7, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbr, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _history.Verify(
            h => h.AddHistoryEntryAsync(
                It.Is<ProcessingHistoryEntry>(entry => entry.Action == "Normalize" && entry.Success && entry.FilePath == cbz),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithConversionOffAnAlreadyNormalizedCbrIsLeftAsItIs(bool throughProcessFile)
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var cbr = WriteCbr(Normalized);
        var before = await File.ReadAllBytesAsync(cbr);

        if (throughProcessFile)
        {
            Assert.True(await CreateService().ProcessFileAsync(cbr));
        }
        else
        {
            Assert.Equal(0, (await NormalizeAsync(cbr)).FailedFiles);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(Path.ChangeExtension(cbr, ".cbz")));
        _fileStore.Verify(f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbr, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingValuesAreNeverOverwritten()
    {
        var xml = """
            <ComicInfo>
              <Title>The Black Swordsman</Title>
              <Series>Berserk</Series>
              <Number>1</Number>
              <Count>41</Count>
              <Summary>This issue's own summary.</Summary>
              <Genre>Seinen</Genre>
              <Web>https://example.org/mine</Web>
              <Manga>No</Manga>
            </ComicInfo>
            """;
        var path = WriteArchive(xml);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ALockedSummaryIsLeftAloneButOtherFieldsFill()
    {
        // A user cleared the summary: the lock says it stays empty.
        var path = WriteArchive(Normalized);
        TrackFile(path, ComicMetadataFieldFlags.Summary);

        Assert.True(await CreateService().ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Null(info.Element("Summary"));
        Assert.Equal("Action, Dark Fantasy", info.Element("Genre")?.Value);
    }

    [Fact]
    public async Task ALockOnAnotherFieldDoesNotStopTheSummary()
    {
        var path = WriteArchive(Normalized);
        TrackFile(path, ComicMetadataFieldFlags.Title);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.NotNull(ReadComicInfo(path).Element("Summary"));
    }

    [Theory]
    [InlineData(SeriesPublicationStatus.Ongoing)]
    [InlineData(SeriesPublicationStatus.Hiatus)]
    [InlineData(SeriesPublicationStatus.Cancelled)]
    [InlineData(null)]
    public async Task CountIsOnlyWrittenForAFinishedSeries(string? status)
    {
        _record.PublicationStatus = status;
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Null(ReadComicInfo(path).Element("Count"));
    }

    [Fact]
    public async Task AFinishedSeriesWithoutAChapterCountGetsNoCount()
    {
        _record.TotalChapters = null;
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Null(ReadComicInfo(path).Element("Count"));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("zh")]
    [InlineData("en")]
    [InlineData(null)]
    public async Task OnlyAJapaneseOriginSetsRightToLeft(string? origin)
    {
        _record.Origin = origin;
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Null(ReadComicInfo(path).Element("Manga"));
    }

    [Fact]
    public async Task TheOriginLanguageIsNeverWrittenAsTheFilesLanguage()
    {
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Null(ReadComicInfo(path).Element("LanguageISO"));
    }

    [Fact]
    public async Task TheSettingTurnsFillingOff()
    {
        _settings.FillComicInfoFromProviders = false;
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("cleared")]
    [InlineData("error")]
    [InlineData("manual")]
    [InlineData(null)]
    public async Task ARecordWithoutAProviderMatchFillsNothing(string? status)
    {
        _record.LookupStatus = status;
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("error")]
    [InlineData(null)]
    public async Task ALookupThatFoundNothingGivesWayToTheMatchThatOwnsTheTitle(string? status)
    {
        var match = _record;
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "berserk", CanonicalTitle = "Berserk", LookupStatus = status });
        _cache.Setup(c => c.GetAuthoritativeByTitleAsync("Berserk", It.IsAny<CancellationToken>())).ReturnsAsync(match);
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        AssertFilled(ReadComicInfo(path));
    }

    [Fact]
    public async Task WhenTheOwnerOfTheTitleHasNoMatchEitherNothingIsFilled()
    {
        _record.LookupStatus = "not_found";
        _cache.Setup(c => c.GetAuthoritativeByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(_record);
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("cleared")]
    [InlineData("manual")]
    public async Task MetadataTheUserClearedOrManagesIsNotReplacedByASibling(string status)
    {
        var sibling = new SeriesMetadataCacheRecord { NormalizedKey = "other", LookupStatus = "success", Synopsis = "A sibling's text." };
        _record.LookupStatus = status;
        _cache.Setup(c => c.GetAuthoritativeByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(sibling);
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        _cache.Verify(c => c.GetAuthoritativeByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASuccessfulLookupDoesNotConsultTheTitleIndex()
    {
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        _cache.Verify(c => c.GetAuthoritativeByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AManualMatchFills()
    {
        _record.LookupStatus = "manual_match";
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        AssertFilled(ReadComicInfo(path));
    }

    [Fact]
    public async Task ASeriesWithNoRecordFillsNothing()
    {
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AMatchWithNoDetailsFillsNothing()
    {
        // An entry cached before this release, not refreshed yet.
        _record = new SeriesMetadataCacheRecord { NormalizedKey = "berserk", CanonicalTitle = "Berserk", LookupStatus = "success" };
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ACacheFailureDoesNotFailProcessing()
    {
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CancellationDuringTheLookupIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return _record;
            });
        var path = WriteArchive(Normalized);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().ProcessFileAsync(path, cts.Token));
    }

    [Fact]
    public async Task ProviderTextCannotBreakTheDocument()
    {
        _record.Synopsis = "Tom &amp; Jerry <script>alert(1)</script> \u0001 fight";
        _record.Genres = new List<string> { "Action & <Adventure>" };
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal("Tom & Jerry alert(1) fight", info.Element("Summary")?.Value);
        Assert.Equal("Action &", info.Element("Genre")?.Value);
    }

    [Fact]
    public async Task ProcessingAgainChangesNothingAndNeverMarksTheFileEdited()
    {
        var path = WriteArchive(Normalized);
        var service = CreateService();

        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));
        var afterFirst = await File.ReadAllBytesAsync(path);
        var modifiedAfterFirst = File.GetLastWriteTimeUtc(path);

        // Whatever triggers it (the watcher seeing our own write, a forced run), a second pass
        // finds nothing to fill and leaves the file alone, so it cannot loop.
        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(path));
        Assert.Equal(modifiedAfterFirst, File.GetLastWriteTimeUtc(path));
        _fileStore.Verify(
            f => f.RecordUserMetadataEditAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _fileStore.Verify(
            f => f.ApplyUserMetadataEditAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AFillStillRefreshesTheStoredStatsAndStampsTheCurrentVersion()
    {
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));

        // The stored size and date follow our own write, so a scan does not see a changed
        // file, and the stamp keeps the file off the retag pass's list.
        _fileStore.Verify(
            f => f.RefreshFileStatsAsync(It.Is<IEnumerable<string>>(paths => paths.Contains(path)), It.IsAny<CancellationToken>()),
            Times.Once);
        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(path, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANewerRecordDoesNotRefillAlreadyFilledFields()
    {
        var path = WriteArchive(Normalized);
        var service = CreateService();
        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));
        var filled = await File.ReadAllBytesAsync(path);

        _record.Synopsis = "A different summary after a refresh.";
        _record.Genres = new List<string> { "Horror" };
        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));

        Assert.Equal(filled, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AFileSkippedAsAlreadyProcessedIsNotFilled()
    {
        var path = WriteArchive(Normalized);
        _fileStore.Setup(f => f.IsFileProcessedAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WithNormalizeOffNothingIsFilled()
    {
        _settings.WatcherEnableNormalize = false;
        var path = WriteArchive(Normalized);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await CreateService().ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AFailedFillDoesNotFailAFileNormalizeHadNothingToDoFor()
    {
        // Recording the write is the last step of the writer; when it throws the writer reports
        // a failed write. The file is correctly normalized either way.
        _fileStore.Setup(f => f.RecordArchiveMetadataAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var path = WriteArchive(Normalized);

        Assert.True(await CreateService().ProcessFileAsync(path));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, true, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NormalizeFilesAsync_AFailedFillDoesNotFailTheFile()
    {
        _fileStore.Setup(f => f.RecordArchiveMetadataAsync(It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var path = WriteArchive(Normalized);

        var job = await NormalizeAsync(path);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(0, job.FailedFiles);
    }

    private BatchJobRegistry? _jobRegistry;

    private BatchJobRegistry Jobs => _jobRegistry ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private async Task<ProcessingJob> NormalizeAsync(string path)
    {
        var jobId = await CreateService().NormalizeFilesAsync(new[] { path });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Jobs.GetJob(jobId) is { IsTerminal: true } job)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job {jobId} did not finish");
    }
}
