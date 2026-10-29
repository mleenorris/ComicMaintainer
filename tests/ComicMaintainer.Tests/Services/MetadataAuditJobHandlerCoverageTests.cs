using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="MetadataAuditJobHandler"/>: every finding category,
/// locked-field drift detection, the legacy folder/cache resolution fallback, option parsing,
/// auto-correct reporting and cancellation. Uses SQLite in-memory because the handler clears
/// prior findings with <c>ExecuteDeleteAsync</c>.
/// </summary>
public class MetadataAuditJobHandlerCoverageTests : IAsyncLifetime
{
    private const string AutoCorrectOn = """{"autoCorrect":true}""";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly AppSettings _settings = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _dbFactory = null!;

    public MetadataAuditJobHandlerCoverageTests()
    {
        // By default the resolver has no opinion, so files are only flagged when a test says so.
        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution());
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private MetadataAuditJobHandler CreateHandler(bool withResolver = true, bool withCache = false) => new(
        _fileStore.Object,
        _processor.Object,
        _dbFactory,
        new TestOptionsMonitor<AppSettings>(_settings),
        Mock.Of<ILogger<MetadataAuditJobHandler>>(),
        withCache ? _cache.Object : null,
        withResolver ? _resolver.Object : null);

    private void SetupTrackedFiles(params string[] paths) =>
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(paths.Select(p => new ComicFile { FilePath = p }).ToList());

    private void SetupDiskMetadata(string path, ComicMetadata? metadata) =>
        _processor.Setup(p => p.GetMetadataAsync(path, It.IsAny<CancellationToken>())).ReturnsAsync(metadata);

    private void SetupExpectedSeries(string path, string resolved, string explanation = "") =>
        _resolver.Setup(r => r.ResolveAsync(path, It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = resolved, Explanation = explanation });

    private void SetupDbRow(string path, ComicMetadata? metadata) =>
        _fileStore.Setup(f => f.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = path, Metadata = metadata });

    private void SetupMismatch(string path, string actual = "Wrong Name", string expected = "Right Name", string issue = "1")
    {
        SetupDiskMetadata(path, new ComicMetadata { Series = actual, Issue = issue });
        SetupExpectedSeries(path, expected);
    }

    private async Task<List<MetadataAuditFindingEntity>> ReadFindingsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MetadataAuditFindings.AsNoTracking()
            .OrderBy(f => f.FilePath).ThenBy(f => f.FindingType)
            .ToListAsync();
    }

    private async Task SeedFindingAsync(string path)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.MetadataAuditFindings.Add(new MetadataAuditFindingEntity
        {
            FilePath = path,
            FindingType = nameof(MetadataAuditFindingType.SeriesMismatch),
            Details = "from a previous run",
        });
        await db.SaveChangesAsync();
    }

    private static ComicMetadata FullMetadata() => new()
    {
        Series = "Series",
        Title = "Title",
        Issue = "1",
        Volume = "2",
        Publisher = "Publisher",
        Year = 2020,
        Summary = "Summary",
        Authors = new List<string> { "Author A", "Author B" },
        Tags = new List<string> { "tag" },
    };

    [Fact]
    public async Task ExecuteAsync_NoTrackedFiles_ClearsPreviousFindingsAndReportsZeroCounts()
    {
        await SeedFindingAsync("/library/Old/c001.cbz");
        SetupTrackedFiles();

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 0: 0 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        Assert.Empty(await ReadFindingsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesFindingsFromThePreviousRun()
    {
        await SeedFindingAsync("/library/Old/c001.cbz");
        const string path = "/library/New/c001.cbz";
        SetupTrackedFiles(path);
        SetupMismatch(path);

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal(path, finding.FilePath);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataUnreadable_RecordsUnreadableFindingAndSkipsFurtherChecks()
    {
        const string throwing = "/library/A/broken.cbz";
        const string missing = "/library/A/empty.cbz";
        SetupTrackedFiles(throwing, missing);
        _processor.Setup(p => p.GetMetadataAsync(throwing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("corrupt archive"));
        SetupDiskMetadata(missing, null);

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 2: 0 series mismatch, 0 missing chapter, 2 unreadable.", summary);
        var findings = await ReadFindingsAsync();
        Assert.Equal(new[] { throwing, missing }, findings.Select(f => f.FilePath));
        Assert.All(findings, f =>
        {
            Assert.Equal(nameof(MetadataAuditFindingType.Unreadable), f.FindingType);
            Assert.Equal("Metadata could not be read.", f.Details);
            Assert.Null(f.ActualSeries);
            Assert.Null(f.ExpectedSeries);
        });
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BlankIssue_RecordsMissingChapterSkipsSeriesCheckButStillChecksDrift()
    {
        const string drifted = "/library/A/c001.cbz";
        const string plain = "/library/A/c002.cbz";
        SetupTrackedFiles(drifted, plain);
        SetupDiskMetadata(drifted, new ComicMetadata { Series = "Disk Series", Issue = "   " });
        SetupDiskMetadata(plain, new ComicMetadata { Series = "Disk Series", Issue = null });
        SetupDbRow(drifted, new ComicMetadata
        {
            Series = "Edited Series",
            IsUserEdited = true,
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series,
        });

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 2: 0 series mismatch, 2 missing chapter, 0 unreadable.", summary);
        Assert.Collection(await ReadFindingsAsync(),
            f =>
            {
                Assert.Equal((drifted, nameof(MetadataAuditFindingType.DiskDriftedFromDb)), (f.FilePath, f.FindingType));
                Assert.Equal("Disk Series", f.ActualSeries);
                Assert.Equal("Edited Series", f.ExpectedSeries);
                Assert.Equal("   ", f.ActualIssue);
                Assert.Equal("Disk metadata diverges from user-edited DB record; backfill will rewrite.", f.Details);
            },
            f =>
            {
                Assert.Equal((drifted, nameof(MetadataAuditFindingType.MissingChapter)), (f.FilePath, f.FindingType));
                Assert.Equal("Disk Series", f.ActualSeries);
                Assert.Null(f.ExpectedSeries);
                Assert.Null(f.ActualIssue);
                Assert.Equal("ComicInfo <Number> tag is empty.", f.Details);
            },
            f => Assert.Equal((plain, nameof(MetadataAuditFindingType.MissingChapter)), (f.FilePath, f.FindingType)));
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SeriesDisagreesWithResolver_RecordsMismatchWithExplanation()
    {
        const string path = "/library/Folder/c007.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, new ComicMetadata { Series = "Wrong Name", Issue = "7" });
        SetupExpectedSeries(path, "Right Name", "Matched cache record 'right-name'.");

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1: 1 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal(nameof(MetadataAuditFindingType.SeriesMismatch), finding.FindingType);
        Assert.Equal("Wrong Name", finding.ActualSeries);
        Assert.Equal("Right Name", finding.ExpectedSeries);
        Assert.Equal("7", finding.ActualIssue);
        Assert.Equal(
            "Series 'Wrong Name' does not match expected 'Right Name'. Matched cache record 'right-name'.",
            finding.Details);
        _resolver.Verify(r => r.ResolveAsync(
            path,
            It.Is<ComicMetadata?>(m => m != null && m.Series == "Wrong Name"),
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BlankExplanation_IsLeftOutOfTheMismatchDetails()
    {
        const string path = "/library/Folder/c001.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, new ComicMetadata { Series = "Wrong Name", Issue = "1" });
        SetupExpectedSeries(path, "Right Name", "   ");

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal("Series 'Wrong Name' does not match expected 'Right Name'.", finding.Details);
    }

    [Fact]
    public async Task ExecuteAsync_VeryLongExplanation_CapsDetailsAt1024Characters()
    {
        const string path = "/library/Folder/c001.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, new ComicMetadata { Series = "Wrong Name", Issue = "1" });
        SetupExpectedSeries(path, "Right Name", new string('x', 2000));

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal(1024, finding.Details!.Length);
        Assert.StartsWith("Series 'Wrong Name' does not match expected 'Right Name'. xxx", finding.Details);
    }

    [Fact]
    public async Task ExecuteAsync_BlankExpectedOrActualSeriesOrCaseOnlyDifference_IsNotAMismatch()
    {
        const string noExpectation = "/library/A/c001.cbz";
        const string blankActual = "/library/A/c002.cbz";
        const string nullActual = "/library/A/c003.cbz";
        const string caseOnly = "/library/A/c004.cbz";
        SetupTrackedFiles(noExpectation, blankActual, nullActual, caseOnly);
        SetupDiskMetadata(noExpectation, new ComicMetadata { Series = "Something", Issue = "1" });
        SetupExpectedSeries(noExpectation, "  ");
        SetupDiskMetadata(blankActual, new ComicMetadata { Series = "  ", Issue = "2" });
        SetupExpectedSeries(blankActual, "Something");
        SetupDiskMetadata(nullActual, new ComicMetadata { Series = null, Issue = "3" });
        SetupExpectedSeries(nullActual, "Something");
        SetupDiskMetadata(caseOnly, new ComicMetadata { Series = "SOMETHING", Issue = "4" });
        SetupExpectedSeries(caseOnly, "something");

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 4: 0 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        Assert.Empty(await ReadFindingsAsync());
    }

    [Theory]
    [InlineData(ComicMetadataFieldFlags.Series)]
    [InlineData(ComicMetadataFieldFlags.Title)]
    [InlineData(ComicMetadataFieldFlags.Issue)]
    [InlineData(ComicMetadataFieldFlags.Volume)]
    [InlineData(ComicMetadataFieldFlags.Publisher)]
    [InlineData(ComicMetadataFieldFlags.Year)]
    [InlineData(ComicMetadataFieldFlags.Summary)]
    [InlineData(ComicMetadataFieldFlags.Authors)]
    [InlineData(ComicMetadataFieldFlags.Tags)]
    public async Task ExecuteAsync_LockedFieldDiffersFromUserEditedRow_RecordsDiskDrift(ComicMetadataFieldFlags field)
    {
        const string path = "/library/Series/c001.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, FullMetadata());
        var edited = FullMetadata();
        edited.IsUserEdited = true;
        edited.UserLockedFieldsMask = (long)field;
        switch (field)
        {
            case ComicMetadataFieldFlags.Series: edited.Series = "Edited Series"; break;
            case ComicMetadataFieldFlags.Title: edited.Title = "Edited Title"; break;
            case ComicMetadataFieldFlags.Issue: edited.Issue = "99"; break;
            case ComicMetadataFieldFlags.Volume: edited.Volume = "9"; break;
            case ComicMetadataFieldFlags.Publisher: edited.Publisher = "Edited Publisher"; break;
            case ComicMetadataFieldFlags.Year: edited.Year = 1999; break;
            case ComicMetadataFieldFlags.Summary: edited.Summary = "Edited summary"; break;
            // Order and case are significant for list comparisons.
            case ComicMetadataFieldFlags.Authors: edited.Authors = new List<string> { "Author B", "Author A" }; break;
            case ComicMetadataFieldFlags.Tags: edited.Tags = new List<string> { "TAG" }; break;
        }
        SetupDbRow(path, edited);

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1: 0 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal(nameof(MetadataAuditFindingType.DiskDriftedFromDb), finding.FindingType);
        Assert.Equal("Series", finding.ActualSeries);
        Assert.Equal(edited.Series, finding.ExpectedSeries);
        Assert.Equal("1", finding.ActualIssue);
    }

    [Fact]
    public async Task ExecuteAsync_DifferenceOnlyInUnlockedField_IsNotDrift()
    {
        const string path = "/library/Series/c001.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, FullMetadata());
        var edited = FullMetadata();
        edited.Title = "Edited Title";
        edited.IsUserEdited = true;
        edited.UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series;
        SetupDbRow(path, edited);

        await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Empty(await ReadFindingsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_AllFieldsLockedAndNullEquivalentToEmpty_IsNotDrift()
    {
        const string path = "/library/Series/c001.cbz";
        SetupTrackedFiles(path);
        SetupDiskMetadata(path, new ComicMetadata
        {
            Series = "Series",
            Title = null,
            Issue = "1",
            Volume = "",
            Publisher = null,
            Year = null,
            Summary = "",
            Authors = null!,
            Tags = new List<string>(),
        });
        SetupDbRow(path, new ComicMetadata
        {
            Series = "Series",
            Title = "",
            Issue = "1",
            Volume = null,
            Publisher = "",
            Year = null,
            Summary = null,
            Authors = new List<string>(),
            Tags = null!,
            IsUserEdited = true,
            UserLockedFieldsMask = Enum.GetValues<ComicMetadataFieldFlags>().Aggregate(0L, (mask, flag) => mask | (long)flag),
        });

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1: 0 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        Assert.Empty(await ReadFindingsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_RowMissingNotUserEditedOrWithoutLocks_IsNotDrift()
    {
        const string noRow = "/library/A/c001.cbz";
        const string noMetadata = "/library/A/c002.cbz";
        const string notEdited = "/library/A/c003.cbz";
        const string noLocks = "/library/A/c004.cbz";
        SetupTrackedFiles(noRow, noMetadata, notEdited, noLocks);
        foreach (var path in new[] { noRow, noMetadata, notEdited, noLocks })
        {
            SetupDiskMetadata(path, new ComicMetadata { Series = "Disk Series", Issue = "1" });
        }
        SetupDbRow(noMetadata, null);
        SetupDbRow(notEdited, new ComicMetadata
        {
            Series = "Other",
            IsUserEdited = false,
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series,
        });
        SetupDbRow(noLocks, new ComicMetadata
        {
            Series = "Other",
            IsUserEdited = true,
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.None,
        });

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 4: 0 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        Assert.Empty(await ReadFindingsAsync());
        _fileStore.Verify(f => f.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutResolverOrCache_ExpectsTheFolderName()
    {
        const string underscoreFolder = "/library/Re_Zero/c001.cbz";
        const string wrongTag = "/library/Expected Folder/c001.cbz";
        const string noFolder = "c001.cbz";
        const string rootFile = "/c002.cbz";
        SetupTrackedFiles(underscoreFolder, wrongTag, noFolder, rootFile);
        // Underscores in folder names stand in for ':' (not allowed on disk).
        SetupDiskMetadata(underscoreFolder, new ComicMetadata { Series = "Re:Zero", Issue = "1" });
        SetupDiskMetadata(wrongTag, new ComicMetadata { Series = "Wrong", Issue = "1" });
        SetupDiskMetadata(noFolder, new ComicMetadata { Series = "Anything", Issue = "1" });
        SetupDiskMetadata(rootFile, new ComicMetadata { Series = "Anything", Issue = "1" });

        var summary = await CreateHandler(withResolver: false).ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 4: 1 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        var finding = Assert.Single(await ReadFindingsAsync());
        Assert.Equal(wrongTag, finding.FilePath);
        Assert.Equal("Expected Folder", finding.ExpectedSeries);
        Assert.Equal("Series 'Wrong' does not match expected 'Expected Folder'.", finding.Details);
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WithoutResolver_UsesTheCachedDisplayTitleForTheFolder()
    {
        _settings.DefaultPreferredLanguage = "en";
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns<string?>(title => (title ?? string.Empty).ToLowerInvariant().Replace(' ', '-'));
        _cache.Setup(c => c.NormalizeKey("Unkeyable")).Returns(string.Empty);
        void SetupRecord(string key, SeriesMetadataCacheRecord record) =>
            _cache.Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        SetupRecord("localized", new SeriesMetadataCacheRecord
        {
            CanonicalTitle = "Canonical",
            LocalizedTitles = new List<LocalizedTitle> { new("Japanese Title", "ja"), new("English Title", "en") },
        });
        SetupRecord("pinned", new SeriesMetadataCacheRecord { CanonicalTitle = "Canonical", SeriesName = "  Pinned Name " });
        SetupRecord("padded", new SeriesMetadataCacheRecord { CanonicalTitle = "  Padded Canonical  " });
        SetupRecord("blank-canonical", new SeriesMetadataCacheRecord { CanonicalTitle = "  " });
        _cache.Setup(c => c.GetAsync("broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache offline"));

        var expectations = new Dictionary<string, string>
        {
            ["/library/Localized/c001.cbz"] = "English Title",
            ["/library/Pinned/c001.cbz"] = "Pinned Name",
            ["/library/Padded/c001.cbz"] = "Padded Canonical",
            ["/library/Blank Canonical/c001.cbz"] = "Blank Canonical",
            ["/library/Uncached/c001.cbz"] = "Uncached",
            ["/library/Broken/c001.cbz"] = "Broken",
            ["/library/Unkeyable/c001.cbz"] = "Unkeyable",
        };
        SetupTrackedFiles(expectations.Keys.ToArray());
        foreach (var path in expectations.Keys)
        {
            SetupDiskMetadata(path, new ComicMetadata { Series = "Tagged Differently", Issue = "1" });
        }

        var summary = await CreateHandler(withResolver: false, withCache: true).ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 7: 7 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        var findings = await ReadFindingsAsync();
        Assert.All(findings, f => Assert.Equal(nameof(MetadataAuditFindingType.SeriesMismatch), f.FindingType));
        Assert.Equal(expectations, findings.ToDictionary(f => f.FilePath, f => f.ExpectedSeries!));
        _cache.Verify(c => c.GetAsync(string.Empty, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"autoCorrect":false}""")]
    public async Task ExecuteAsync_AutoCorrectNotRequested_OnlyReports(string? optionsJson)
    {
        const string path = "/library/Folder/c001.cbz";
        SetupTrackedFiles(path);
        SetupMismatch(path);

        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        Assert.Equal("Scanned 1: 1 series mismatch, 0 missing chapter, 0 unreadable.", summary);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AutoCorrectWithOnlyUnfixableFindings_ReportsNothingToFix()
    {
        const string unreadable = "/library/A/c001.cbz";
        const string drifted = "/library/A/c002.cbz";
        SetupTrackedFiles(unreadable, drifted);
        SetupDiskMetadata(unreadable, null);
        SetupDiskMetadata(drifted, new ComicMetadata { Series = "Disk", Issue = "1" });
        SetupDbRow(drifted, new ComicMetadata
        {
            Series = "Edited",
            IsUserEdited = true,
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series,
        });

        var summary = await CreateHandler().ExecuteAsync(AutoCorrectOn, CancellationToken.None);

        Assert.Equal("Scanned 2: 0 series mismatch, 0 missing chapter, 1 unreadable. Auto-correct: nothing to fix.", summary);
        Assert.Equal(2, (await ReadFindingsAsync()).Count);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task ExecuteAsync_AutoCorrect_ForceNormalizesFixableFilesAndReportsTheJobOutcome(JobStatus finalStatus)
    {
        const string mismatch = "/library/A/c001.cbz";
        const string chapterless = "/library/A/c002.cbz";
        const string unreadable = "/library/A/c003.cbz";
        const string healthy = "/library/A/c004.cbz";
        SetupTrackedFiles(mismatch, chapterless, unreadable, healthy);
        SetupMismatch(mismatch);
        SetupDiskMetadata(chapterless, new ComicMetadata { Series = "A", Issue = "" });
        SetupDbRow(chapterless, new ComicMetadata
        {
            Series = "Edited",
            IsUserEdited = true,
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series,
        });
        SetupDiskMetadata(unreadable, null);
        SetupDiskMetadata(healthy, new ComicMetadata { Series = "A", Issue = "4" });
        var normalizeJobId = Guid.NewGuid();
        List<string>? queued = null;
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((paths, _, _) => queued = paths.ToList())
            .ReturnsAsync(normalizeJobId);
        _processor.Setup(p => p.GetJob(normalizeJobId))
            .Returns(new ProcessingJob { JobId = normalizeJobId, Status = finalStatus, ProcessedFiles = 1, FailedFiles = 1 });

        // Option names are matched case-insensitively.
        var summary = await CreateHandler().ExecuteAsync("""{"AUTOCORRECT":true}""", CancellationToken.None);

        Assert.Equal(
            $"Scanned 4: 1 series mismatch, 1 missing chapter, 1 unreadable. Auto-correct: 1/2 normalized, 1 failed (status: {finalStatus}).",
            summary);
        Assert.Equal(new[] { mismatch, chapterless }, queued);
        Assert.Equal(4, (await ReadFindingsAsync()).Count);
        _processor.Verify(p => p.GetJob(normalizeJobId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AutoCorrectJobRecordEvicted_ReportsQueuedCount()
    {
        const string first = "/library/A/c001.cbz";
        const string second = "/library/A/c002.cbz";
        SetupTrackedFiles(first, second);
        SetupMismatch(first);
        SetupMismatch(second);
        var normalizeJobId = Guid.NewGuid();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(normalizeJobId);
        _processor.Setup(p => p.GetJob(normalizeJobId)).Returns(default(ProcessingJob?));

        var summary = await CreateHandler().ExecuteAsync(AutoCorrectOn, CancellationToken.None);

        Assert.Equal(
            "Scanned 2: 2 series mismatch, 0 missing chapter, 0 unreadable. Auto-correct queued 2 file(s); job record no longer available.",
            summary);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaitingForAutoCorrect_ThrowsButKeepsThePersistedFindings()
    {
        const string path = "/library/A/c001.cbz";
        SetupTrackedFiles(path);
        SetupMismatch(path);
        var normalizeJobId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(normalizeJobId);
        _processor.Setup(p => p.GetJob(normalizeJobId))
            .Callback(() => cts.Cancel())
            .Returns(new ProcessingJob { JobId = normalizeJobId, Status = JobStatus.Running });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(AutoCorrectOn, cts.Token));

        Assert.Equal(path, Assert.Single(await ReadFindingsAsync()).FilePath);
        _processor.Verify(p => p.GetJob(normalizeJobId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileQueueingAutoCorrect_ThrowsWithoutPolling()
    {
        const string path = "/library/A/c001.cbz";
        SetupTrackedFiles(path);
        SetupMismatch(path);
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(Guid.NewGuid());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(AutoCorrectOn, cts.Token));

        _processor.Verify(p => p.GetJob(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledBeforeScanning_ThrowsAndKeepsPreviousFindings()
    {
        await SeedFindingAsync("/library/Old/c001.cbz");
        SetupTrackedFiles("/library/A/c001.cbz");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(null, cts.Token));

        Assert.Equal("/library/Old/c001.cbz", Assert.Single(await ReadFindingsAsync()).FilePath);
        _processor.Verify(p => p.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMidScan_StopsBeforeTheNextFile()
    {
        const string first = "/library/A/c001.cbz";
        const string second = "/library/A/c002.cbz";
        SetupTrackedFiles(first, second);
        using var cts = new CancellationTokenSource();
        _processor.Setup(p => p.GetMetadataAsync(first, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new ComicMetadata { Series = "A", Issue = "1" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync(null, cts.Token));

        _processor.Verify(p => p.GetMetadataAsync(second, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Metadata_DescribesTheJob()
    {
        var handler = CreateHandler();

        Assert.Equal(MetadataAuditJobHandler.Key, handler.JobKey);
        Assert.Equal("metadata-audit", handler.JobKey);
        Assert.Equal("Metadata Audit", handler.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(handler.Description));
        Assert.False(handler.Defaults.Enabled);
        Assert.Equal(60 * 24 * 7, handler.Defaults.IntervalMinutes);
        Assert.Equal("""{"autoCorrect":false}""", handler.Defaults.OptionsJson);
    }
}
