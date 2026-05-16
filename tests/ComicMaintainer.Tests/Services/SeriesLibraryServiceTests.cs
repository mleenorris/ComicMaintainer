using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLibraryServiceTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _metadataCache = new();
    private readonly Mock<ILogger<SeriesLibraryService>> _logger = new();

    public SeriesLibraryServiceTests()
    {
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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Single(result.Series);
        Assert.Equal("Series A", result.Series[0].Title);
        Assert.Equal(2, result.Series[0].IssueCount);
        Assert.Contains("Series B", result.Series[0].Aliases);
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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);
        var result = await service.GetSeriesIssuesAsync("does-not-exist");

        Assert.Null(result);
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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);
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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);
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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

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

        var service = new SeriesLibraryService(_fileStore.Object, _processor.Object, _metadataCache.Object, _logger.Object);

        var result = await service.GetSeriesAsync();

        Assert.Equal(2, result.Series.Count);
        var a = result.Series.Single(s => s.Title.Equals("A", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, a.IssueCount);
        Assert.Empty(a.Aliases);
        var b = result.Series.Single(s => s.Title.Equals("B", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, b.IssueCount);
        Assert.Contains(b.Aliases, alias => alias.Equals("C", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }
}
