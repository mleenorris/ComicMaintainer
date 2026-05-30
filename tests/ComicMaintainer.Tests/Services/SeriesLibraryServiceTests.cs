using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLibraryServiceTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<ILogger<SeriesLibraryService>> _logger = new();
    private readonly IOptionsMonitor<AppSettings> _settings;

    public SeriesLibraryServiceTests()
    {
        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(new AppSettings());
        _settings = monitor.Object;

        // Default: NormalizeKey mirrors the production sanitizer used by the real service.
        _metadataCache.Setup(m => m.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(NormalizeKey);
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());
    }

    [Fact]
    public async Task GetSeriesAsync_GroupsAlternateTitlesUnderCanonicalTitle()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/Dark Knight/Dark Knight 002.cbz", FileName = "Dark Knight 002.cbz", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync("processed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman", Issue = "1", Title = "Year One" });
        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[1].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "The Dark Knight", AlternateSeries = "Batman", Issue = "2", Title = "Part Two" });

        // Cached external metadata declares Batman with "Dark Knight" as an alias.
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string> { "Dark Knight", "The Dark Knight" },
                    UserAliases = new List<string>(),
                    Source = "ComicVine"
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync(filter: "processed");

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains(result.Series[0].Aliases, a => a.Equals("Dark Knight", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ComicVine", result.Series[0].MetadataSource);
    }

    [Fact]
    public async Task GetSeriesAsync_GroupsFilesByFolderWhenMetadataVariesByIssue()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = "/library/Batman", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman: Year One", Issue = "1", Title = "Year One Part 1" });
        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[1].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman: Year One", AlternateSeries = "Batman (2011)", Issue = "2", Title = "Year One Part 2" });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Batman: Year One", result.Series[0].Aliases);
        Assert.Contains("Batman (2011)", result.Series[0].Aliases);
    }

    [Fact]
    public async Task GetSeriesAsync_UsesCachedMetadataAndSkipsArchiveRead()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman", Issue = "1", Title = "Year One" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(1, result.Series[0].IssueCount);
        Assert.Equal("Year One", result.Series[0].Issues[0].Title);
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetSeriesAsync_FallsBackToArchiveWhenCachedMetadataIsEmpty()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata() // empty
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(processor => processor.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman", Issue = "1", Title = "From Archive" });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("From Archive", result.Series[0].Issues[0].Title);
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSeriesAsync_MergesFoldersUsingUserAliases()
    {
        // Two folders ("Series A" and "Series B") with unrelated names should be
        // collapsed into a single series card when the user has added "Series B"
        // as an alias of "Series A".
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Series A/Series A 001.cbz",
                FileName = "Series A 001.cbz",
                Directory = "/library/Series A",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series A", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Series B/Series B 002.cbz",
                FileName = "Series B 002.cbz",
                Directory = "/library/Series B",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series B", Issue = "2" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "series-a",
                    CanonicalTitle = "Series A",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "Series B" }
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Series A", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Series B", result.Series[0].Aliases);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_AppliesGlobalDefaultPreferredLanguage()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Naruto/Naruto 001.cbz", FileName = "Naruto 001.cbz", Directory = "/library/Naruto", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Naruto", Issue = "1" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "naruto",
                    CanonicalTitle = "Naruto",
                    PreferredLanguage = null,
                    LocalizedTitles = new List<LocalizedTitle>
                    {
                        new("Naruto", "en"),
                        new("ナルト", "ja")
                    }
                }
            });

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(new AppSettings { DefaultPreferredLanguage = "ja" });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, monitor.Object, _logger.Object);

        var result = await service.GetSeriesSummariesAsync();

        Assert.Single(result.Series);
        Assert.Equal("ナルト", result.Series[0].Title);
        Assert.Equal("Naruto", result.Series[0].CanonicalTitle);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_DoesNotIncludeIssuesAndDoesNotOpenArchives()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = "/library/Batman", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Batman", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        // Summary mode never opens archives on disk for the listing.
        _processor.Verify(p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // SeriesSummaryDto does not expose an Issues collection at all (compile-time check).
        Assert.IsType<SeriesSummaryDto>(result.Series[0]);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_ReturnsPagedIssuesForResolvedSeriesId()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Batman", Issue = "1" } },
            new() { FilePath = "/library/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = "/library/Batman", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Batman", Issue = "2" } },
            new() { FilePath = "/library/Batman/Batman 003.cbz", FileName = "Batman 003.cbz", Directory = "/library/Batman", FileSize = 300, LastModified = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Batman", Issue = "3" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var summaries = await service.GetSeriesSummariesAsync();
        var seriesId = summaries.Series.Single().Id;

        var page1 = await service.GetSeriesIssuesAsync(seriesId, page: 1, perPage: 2);
        Assert.NotNull(page1);
        Assert.Equal(3, page1!.IssueCount);
        Assert.Equal(2, page1.Issues.Count);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal("Batman", page1.Title);
        Assert.Equal("1", page1.Issues[0].Issue);

        var page2 = await service.GetSeriesIssuesAsync(seriesId, page: 2, perPage: 2);
        Assert.NotNull(page2);
        Assert.Single(page2!.Issues);
        Assert.Equal("3", page2.Issues[0].Issue);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_ReturnsNullForUnknownId()
    {
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);
        var result = await service.GetSeriesIssuesAsync("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_LastPageContainsRemainder()
    {
        var files = Enumerable.Range(1, 5).Select(i => new ComicFile
        {
            FilePath = $"/library/Batman/Batman {i:000}.cbz",
            FileName = $"Batman {i:000}.cbz",
            Directory = "/library/Batman",
            FileSize = 100,
            LastModified = new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc),
            Metadata = new ComicMetadata { Series = "Batman", Issue = i.ToString() }
        }).Cast<ComicFile>().ToList();
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);
        var summaries = await service.GetSeriesSummariesAsync();
        var seriesId = summaries.Series.Single().Id;

        var lastPage = await service.GetSeriesIssuesAsync(seriesId, page: 2, perPage: 3);

        Assert.NotNull(lastPage);
        Assert.Equal(5, lastPage!.IssueCount);
        Assert.Equal(2, lastPage.TotalPages);
        Assert.Equal(2, lastPage.Issues.Count);
        Assert.Equal("4", lastPage.Issues[0].Issue);
        Assert.Equal("5", lastPage.Issues[1].Issue);
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_CapsArchiveUpgradesAtConfiguredLimit()
    {
        // 50 files with no cached Title — the upgrade loop would normally
        // open every archive when perPage == -1. With the cap set to 5
        // (overriding the AppSettings default of 100) we should see at
        // most 5 processor calls regardless of how many issues lack a title.
        var files = Enumerable.Range(1, 50).Select(i => new ComicFile
        {
            FilePath = $"/library/Batman/Batman {i:000}.cbz",
            FileName = $"Batman {i:000}.cbz",
            Directory = "/library/Batman",
            FileSize = 100,
            LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            // Series set so grouping works, but Issue/Title intentionally null
            // so the upgrade loop wants to call GetSeriesMetadataAsync.
            Metadata = new ComicMetadata { Series = "Batman" }
        }).Cast<ComicFile>().ToList();
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);
        _processor.Setup(p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Batman", Title = "Upgraded", Issue = "1" });

        var monitor = new Mock<IOptionsMonitor<AppSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(new AppSettings { SeriesIssuesMaxArchiveUpgrades = 5 });
        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, monitor.Object, _logger.Object);

        var summaries = await service.GetSeriesSummariesAsync();
        var seriesId = summaries.Series.Single().Id;

        var result = await service.GetSeriesIssuesAsync(seriesId, perPage: -1);

        Assert.NotNull(result);
        Assert.Equal(50, result!.IssueCount);
        // The processor was invoked at most cap-many times by the upgrade
        // loop (Times.AtMost accommodates internal calls outside the loop).
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtMost(5));
    }

    [Fact]
    public async Task GetSeriesIssuesAsync_SkipsArchiveUpgradeWhenCachedTitleAndIssuePresent()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Batman", Issue = "1", Title = "Year One" } },
            new() { FilePath = "/library/Batman/Batman 002.cbz", FileName = "Batman 002.cbz", Directory = "/library/Batman", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Batman", Issue = "2", Title = "Year Two" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);
        var summaries = await service.GetSeriesSummariesAsync();
        var seriesId = summaries.Series.Single().Id;

        var result = await service.GetSeriesIssuesAsync(seriesId);

        Assert.NotNull(result);
        // Every DTO already has Title and Issue, so the upgrade loop should
        // not call into the processor at all.
        _processor.Verify(
            p => p.GetSeriesMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTitlesForSeriesIdAsync_ReturnsCanonicalAndAliases()
    {
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Batman/Batman 001.cbz", FileName = "Batman 001.cbz", Directory = "/library/Batman", FileSize = 100, LastModified = DateTime.UtcNow, Metadata = new ComicMetadata { Series = "Batman", Issue = "1" } },
            new() { FilePath = "/library/Dark Knight/DK 001.cbz", FileName = "DK 001.cbz", Directory = "/library/Dark Knight", FileSize = 100, LastModified = DateTime.UtcNow, Metadata = new ComicMetadata { Series = "Dark Knight", Issue = "1" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(files);
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string> { "Dark Knight" },
                    UserAliases = new List<string>(),
                    Source = "ComicVine"
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);
        var summaries = await service.GetSeriesSummariesAsync();
        var seriesId = summaries.Series.Single().Id;

        var titles = await service.GetTitlesForSeriesIdAsync(seriesId);

        Assert.Contains("Batman", titles);
        Assert.Contains(titles, t => string.Equals(t, "Dark Knight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetTitlesForSeriesIdAsync_ReturnsEmptyForUnknownId()
    {
        _fileStore.Setup(s => s.GetFilteredFilesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);
        var result = await service.GetTitlesForSeriesIdAsync("nope");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSeriesAsync_DoesNotMergeRecordsWhenProviderAliasMatchesAnotherCanonical()
    {
        // Regression: record A's provider Aliases listed record B's canonical title.
        // Previously the alias-index "last writer wins" + transitive record-record
        // union collapsed unrelated series into one giant card.
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Boss Monster/BM 001.cbz", FileName = "BM 001.cbz", Directory = "/library/Boss Monster", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Boss Monster", Issue = "1" } },
            new() { FilePath = "/library/Omniscient Reader/OR 001.cbz", FileName = "OR 001.cbz", Directory = "/library/Omniscient Reader", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Omniscient Reader", Issue = "1" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "boss-monster",
                    CanonicalTitle = "Boss Monster",
                    Aliases = new List<string> { "Omniscient Reader" }, // bogus provider alias
                    UserAliases = new List<string>()
                },
                new()
                {
                    NormalizedKey = "omniscient-reader",
                    CanonicalTitle = "Omniscient Reader",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>()
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Equal(2, result.Series.Count);
        var omniscient = result.Series.Single(s => s.Title.Equals("Omniscient Reader", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, omniscient.IssueCount);
        var boss = result.Series.Single(s => s.Title.Equals("Boss Monster", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, boss.IssueCount);
    }

    [Fact]
    public async Task GetSeriesAsync_DoesNotMergeRecordsWhenUserAliasMatchesAnotherCanonical()
    {
        // The Manage-Series-Names UI's "Add Aliases" button copies every
        // external-search-result alias into user_aliases. If those happen to
        // include another real series' canonical title, the two series must
        // still remain separate (canonical title beats user alias).
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/Boss Monster/BM 001.cbz", FileName = "BM 001.cbz", Directory = "/library/Boss Monster", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Boss Monster", Issue = "1" } },
            new() { FilePath = "/library/Omniscient Reader/OR 001.cbz", FileName = "OR 001.cbz", Directory = "/library/Omniscient Reader", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Omniscient Reader", Issue = "1" } },
            new() { FilePath = "/library/Dead Knight Gunther/DK 001.cbz", FileName = "DK 001.cbz", Directory = "/library/Dead Knight Gunther", FileSize = 300, LastModified = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "Dead Knight Gunther", Issue = "1" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "boss-monster",
                    CanonicalTitle = "Boss Monster",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "Omniscient Reader", "Dead Knight Gunther" } // accidentally adopted
                },
                new()
                {
                    NormalizedKey = "omniscient-reader",
                    CanonicalTitle = "Omniscient Reader",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>()
                },
                new()
                {
                    NormalizedKey = "dead-knight-gunther",
                    CanonicalTitle = "Dead Knight Gunther",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>()
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Equal(3, result.Series.Count);
        Assert.Contains(result.Series, s => s.Title.Equals("Boss Monster", StringComparison.OrdinalIgnoreCase) && s.IssueCount == 1);
        Assert.Contains(result.Series, s => s.Title.Equals("Omniscient Reader", StringComparison.OrdinalIgnoreCase) && s.IssueCount == 1);
        Assert.Contains(result.Series, s => s.Title.Equals("Dead Knight Gunther", StringComparison.OrdinalIgnoreCase) && s.IssueCount == 1);
    }

    [Fact]
    public async Task GetSeriesAsync_PostFolderCombine_DoesNotLeakAliasesToOtherSeries()
    {
        // Scenario: three series A/B/C existed. The user combined folder C into
        // folder B (so C's files now physically live under /library/B). Record B
        // has "C" as a user alias. Record A is unrelated. Expected: two series
        // (A with 1 file, B with 2 files); A retains no extra aliases.
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/A/A 001.cbz", FileName = "A 001.cbz", Directory = "/library/A", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "A", Issue = "1" } },
            new() { FilePath = "/library/B/B 001.cbz", FileName = "B 001.cbz", Directory = "/library/B", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "B", Issue = "1" } },
            new() { FilePath = "/library/B/C 001.cbz", FileName = "C 001.cbz", Directory = "/library/B", FileSize = 300, LastModified = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), Metadata = new ComicMetadata { Series = "B", Issue = "2" } }
        };
        _fileStore.Setup(s => s.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "a",
                    CanonicalTitle = "A",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>()
                },
                new()
                {
                    NormalizedKey = "b",
                    CanonicalTitle = "B",
                    Aliases = new List<string>(),
                    UserAliases = new List<string> { "C" }
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Equal(2, result.Series.Count);
        var a = result.Series.Single(s => s.Title.Equals("A", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, a.IssueCount);
        Assert.Empty(a.Aliases);
        var b = result.Series.Single(s => s.Title.Equals("B", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, b.IssueCount);
        Assert.Contains(b.Aliases, alias => alias.Equals("C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetSeriesAsync_FilterByProviderMatched_ReturnsOnlySeriesWithMetadataSource()
    {
        // Two series: "Batman" has a cached external-provider record (matched),
        // "Lonely Title" has no cache record at all (unmatched). The file store
        // is invoked with a null file-level filter because "matched" is a
        // series-level filter applied after grouping.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Lonely Title/Lonely Title 001.cbz",
                FileName = "Lonely Title 001.cbz",
                Directory = "/library/Lonely Title",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Lonely Title", Issue = "1" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "ComicVine",
                    LookupStatus = "success"
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var matched = await service.GetSeriesAsync(filter: "matched");
        Assert.Single(matched.Series);
        Assert.Equal("Batman", matched.Series[0].Title);
        Assert.Equal("ComicVine", matched.Series[0].MetadataSource);

        var unmatched = await service.GetSeriesAsync(filter: "unmatched");
        Assert.Single(unmatched.Series);
        Assert.Equal("Lonely Title", unmatched.Series[0].Title);
        Assert.Null(unmatched.Series[0].MetadataSource);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_FilterByProviderUnmatched_ReturnsOnlySeriesWithoutMetadataSource()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Batman/Batman 001.cbz",
                FileName = "Batman 001.cbz",
                Directory = "/library/Batman",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Batman", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Unknown/Unknown 001.cbz",
                FileName = "Unknown 001.cbz",
                Directory = "/library/Unknown",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Unknown", Issue = "1" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "batman",
                    CanonicalTitle = "Batman",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "ComicVine",
                    LookupStatus = "success"
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync(filter: "unmatched");

        Assert.Single(result.Series);
        Assert.Equal("Unknown", result.Series[0].Title);
        Assert.Equal(1, result.TotalSeries);
    }

    [Fact]
    public async Task GetSeriesAsync_FilterByMissingIssues_ReturnsOnlySeriesWithGaps()
    {
        // "Gappy Series" has issues 1 and 3 (missing 2); "Complete Series" has
        // issues 1 and 2 (no gap). "missing" is a series-level filter applied
        // after grouping, so the file store is invoked with a null file filter.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Gappy Series/Gappy Series 001.cbz",
                FileName = "Gappy Series 001.cbz",
                Directory = "/library/Gappy Series",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Gappy Series", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Gappy Series/Gappy Series 003.cbz",
                FileName = "Gappy Series 003.cbz",
                Directory = "/library/Gappy Series",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Gappy Series", Issue = "3" }
            },
            new()
            {
                FilePath = "/library/Complete Series/Complete Series 001.cbz",
                FileName = "Complete Series 001.cbz",
                Directory = "/library/Complete Series",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Complete Series", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Complete Series/Complete Series 002.cbz",
                FileName = "Complete Series 002.cbz",
                Directory = "/library/Complete Series",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Complete Series", Issue = "2" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var missing = await service.GetSeriesAsync(filter: "missing");

        Assert.Single(missing.Series);
        Assert.Equal("Gappy Series", missing.Series[0].Title);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_FilterByMissingIssues_IgnoresDecimalSpecials()
    {
        // Issues 1, 1.5 and 2: the decimal "special" must not be treated as a
        // gap, so this series is considered complete and excluded by the filter.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Specials/Specials 001.cbz",
                FileName = "Specials 001.cbz",
                Directory = "/library/Specials",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Specials", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Specials/Specials 001.5.cbz",
                FileName = "Specials 001.5.cbz",
                Directory = "/library/Specials",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Specials", Issue = "1.5" }
            },
            new()
            {
                FilePath = "/library/Specials/Specials 002.cbz",
                FileName = "Specials 002.cbz",
                Directory = "/library/Specials",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Specials", Issue = "2" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync(filter: "missing");

        Assert.Empty(result.Series);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_FilterByMissingIssues_DecimalFillsWholeNumberGap()
    {
        // Issues 1, 2.5 and 3: the decimal "2.5" counts as a found issue for
        // whole number 2, so there is no gap and the series is excluded.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Decimals/Decimals 001.cbz",
                FileName = "Decimals 001.cbz",
                Directory = "/library/Decimals",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Decimals", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Decimals/Decimals 002.5.cbz",
                FileName = "Decimals 002.5.cbz",
                Directory = "/library/Decimals",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Decimals", Issue = "2.5" }
            },
            new()
            {
                FilePath = "/library/Decimals/Decimals 003.cbz",
                FileName = "Decimals 003.cbz",
                Directory = "/library/Decimals",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Decimals", Issue = "3" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync(filter: "missing");

        Assert.Empty(result.Series);
    }

    [Fact]
    public async Task GetSeriesAsync_DoesNotMergeUnrelatedSeriesSharingOnlyDigitFromCjkAlias()
    {
        // Regression: two unrelated series whose only "shared" normalized
        // alias is a number stripped from a CJK title (e.g. "怪獣8号" and
        // "8階級魔法使い" both reduce to "8" after the [^a-z0-9]+ sanitizer)
        // must NOT be merged into a single library card.
        var files = new List<ComicFile>
        {
            new() { FilePath = "/library/kaiju/Kaiju No 8 001.cbz", FileName = "Kaiju No 8 001.cbz", Directory = "/library/kaiju", FileSize = 100, LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { FilePath = "/library/mage8/8th Class Mage 002.cbz", FileName = "8th Class Mage 002.cbz", Directory = "/library/mage8", FileSize = 200, LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _processor.Setup(p => p.GetSeriesMetadataAsync(files[0].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Kaiju No. 8", Issue = "1" });
        _processor.Setup(p => p.GetSeriesMetadataAsync(files[1].FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadata { Series = "Return of the 8th Class Mage", Issue = "2" });

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "kaiju-no-8",
                    CanonicalTitle = "Kaiju No. 8",
                    Aliases = new List<string> { "怪獣8号" }, // collapses to "8"
                    UserAliases = new List<string>()
                },
                new()
                {
                    NormalizedKey = "return-of-the-8th-class-mage",
                    CanonicalTitle = "Return of the 8th Class Mage",
                    Aliases = new List<string> { "帰還した8階級魔法使い" }, // also collapses to "8"
                    UserAliases = new List<string>()
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        // Two separate cards, not one merged.
        Assert.Equal(2, result.Series.Count);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_PrefersSuccessRecordOverNotFoundSiblingInSameGroup()
    {
        // Reproduces the bug where a successful folder-refresh produced both
        // a "success" record (for the canonical title) and a stale "not_found"
        // sibling (for the folder/alternate title that didn't match any
        // provider). Both share the same union-find component because the
        // file links to both — the folder title and the embedded series
        // name. If we just returned the first record encountered, the stale
        // "not_found" sibling could win and the series-list badge would
        // render the yellow "?" even though a successful match exists.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Berserk Deluxe Edition/Berserk 001.cbz",
                FileName = "Berserk 001.cbz",
                Directory = "/library/Berserk Deluxe Edition",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Berserk", Issue = "1" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Two records share the same logical series. The "not_found" record
        // is listed first to exercise the previous "first-match wins" bug.
        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "berserk-deluxe-edition",
                    CanonicalTitle = "Berserk Deluxe Edition",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = null,
                    LookupStatus = "not_found",
                    LastLookupUtc = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    NormalizedKey = "berserk",
                    CanonicalTitle = "Berserk",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "MangaDex",
                    LookupStatus = "success",
                    LastLookupUtc = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync();

        Assert.Single(result.Series);
        Assert.Equal("success", result.Series[0].LookupStatus);
        Assert.Equal("MangaDex", result.Series[0].MetadataSource);
    }

    [Fact]
    public async Task GetSeriesSummariesAsync_PrefersManualMatchOverAutoSuccessInSameGroup()
    {
        // A user's manual match must outrank a sibling provider-success
        // record so the explicit user choice survives an automatic refresh
        // that happened to land on a different cache key.
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Series Alt/Series 001.cbz",
                FileName = "Series 001.cbz",
                Directory = "/library/Series Alt",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series", Issue = "1" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "series-alt",
                    CanonicalTitle = "Series Alt",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "MangaDex",
                    LookupStatus = "success",
                    LastLookupUtc = new DateTime(2024, 5, 2, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    NormalizedKey = "series",
                    CanonicalTitle = "Series",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "AniList",
                    LookupStatus = "manual_match",
                    LastLookupUtc = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesSummariesAsync();

        Assert.Single(result.Series);
        Assert.Equal("manual_match", result.Series[0].LookupStatus);
        Assert.Equal("AniList", result.Series[0].MetadataSource);
    }

    [Fact]
    public async Task GetSeriesAsync_CollapsesDuplicateCanonicalTitlesAcrossRecords()
    {
        // Two cache records share the canonical title "Berserk" but live
        // under different NormalizedKeys (e.g. user matched two on-disk
        // folders to the same external series). Without the duplicate
        // collapse pass each record produced its own card and the library
        // rendered two series both labelled "Berserk".
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Berserk/Berserk 001.cbz",
                FileName = "Berserk 001.cbz",
                Directory = "/library/Berserk",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Berserk", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Berserk Deluxe/Berserk Deluxe 002.cbz",
                FileName = "Berserk Deluxe 002.cbz",
                Directory = "/library/Berserk Deluxe",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Berserk Deluxe", Issue = "2" }
            },
            new()
            {
                FilePath = "/library/Berserk Deluxe/Berserk Deluxe 003.cbz",
                FileName = "Berserk Deluxe 003.cbz",
                Directory = "/library/Berserk Deluxe",
                FileSize = 300,
                LastModified = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Berserk Deluxe", Issue = "3" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        _metadataCache.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new()
                {
                    NormalizedKey = "berserk",
                    CanonicalTitle = "Berserk",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "MangaDex",
                    LookupStatus = "success",
                    LastLookupUtc = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    NormalizedKey = "berserk-deluxe",
                    CanonicalTitle = "Berserk",
                    Aliases = new List<string>(),
                    UserAliases = new List<string>(),
                    Source = "MangaDex",
                    LookupStatus = "success",
                    LastLookupUtc = new DateTime(2024, 5, 2, 0, 0, 0, DateTimeKind.Utc)
                }
            });

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Berserk", result.Series[0].Title);
        Assert.Equal(3, result.Series[0].IssueCount);
        // The survivor should be the one with the most issues (Berserk Deluxe with 2).
        // Either way, the dropped group's title is preserved as an alias so the
        // merge is transparent to the user.
        Assert.Contains(result.Series[0].Aliases, a => a.Equals("Berserk Deluxe", StringComparison.OrdinalIgnoreCase)
            || a.Equals("Berserk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetFoldersForSeriesIdAsync_ReturnsDistinctFoldersWithCounts()
    {
        var files = new List<ComicFile>
        {
            new()
            {
                FilePath = "/library/Series/Series 001.cbz",
                FileName = "Series 001.cbz",
                Directory = "/library/Series",
                FileSize = 100,
                LastModified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series", Issue = "1" }
            },
            new()
            {
                FilePath = "/library/Series/Series 002.cbz",
                FileName = "Series 002.cbz",
                Directory = "/library/Series",
                FileSize = 200,
                LastModified = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series", Issue = "2" }
            },
            new()
            {
                FilePath = "/library/Series Alt/Series Alt 003.cbz",
                FileName = "Series Alt 003.cbz",
                Directory = "/library/Series Alt",
                FileSize = 300,
                LastModified = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                Metadata = new ComicMetadata { Series = "Series", Issue = "3" }
            }
        };

        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var seriesList = await service.GetSeriesAsync();
        Assert.Single(seriesList.Series);
        var seriesId = seriesList.Series[0].Id;

        var folders = await service.GetFoldersForSeriesIdAsync(seriesId);

        Assert.NotNull(folders);
        Assert.Equal(2, folders!.Folders.Count);
        // Most populated folder comes first.
        Assert.Equal("/library/Series", folders.Folders[0].Directory);
        Assert.Equal(2, folders.Folders[0].FileCount);
        Assert.Equal(300, folders.Folders[0].TotalSize);
        Assert.Equal("/library/Series Alt", folders.Folders[1].Directory);
        Assert.Equal(1, folders.Folders[1].FileCount);
    }

    [Fact]
    public async Task GetFoldersForSeriesIdAsync_ReturnsNullForUnknownId()
    {
        _fileStore.Setup(store => store.GetFilteredFilesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ComicFile>());

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _settings, _logger.Object);

        var result = await service.GetFoldersForSeriesIdAsync("does-not-exist");

        Assert.Null(result);
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }
}
