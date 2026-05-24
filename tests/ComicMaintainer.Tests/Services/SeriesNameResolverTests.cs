using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesNameResolverTests
{
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();

    private SeriesNameResolver BuildResolver()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
              .Returns<string?>(s => string.IsNullOrWhiteSpace(s) ? "unknown-series" : s.ToLowerInvariant().Replace(' ', '-'));
        return new SeriesNameResolver(
            _options.Object,
            new Mock<ILogger<SeriesNameResolver>>().Object,
            _cache.Object,
            _external.Object);
    }

    [Fact]
    public async Task ResolveAsync_UserCanonicalRecord_Wins()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            IsUserCanonical = true
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync("/library/One Piece/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserCanonical, result.WinningStep);
        Assert.Equal("One Piece (User Override)", result.ResolvedSeries);
        Assert.Contains("user-canonical", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_MatchedCacheRecord_AppliesPreferredLanguage()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = "ja",
            LocalizedTitles = { new LocalizedTitle("ワンピース", "ja"), new LocalizedTitle("One Piece", "en") }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync("/library/One Piece/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.MatchedCache, result.WinningStep);
        Assert.Equal("ワンピース", result.ResolvedSeries);
        Assert.Equal("ja", result.AppliedLanguage);
    }

    [Fact]
    public async Task ResolveAsync_NoCacheNoExternal_FallsBackToFolderName()
    {
        var resolver = BuildResolver();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ExternalSeriesMetadata?)null);

        var result = await resolver.ResolveAsync("/library/Some Series/c1.cbz", metadata: null, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.FolderName, result.WinningStep);
        Assert.Equal("Some Series", result.ResolvedSeries);
    }

    [Fact]
    public async Task ResolveAsync_NoCacheButExistingMetadata_PreservesExisting()
    {
        var resolver = BuildResolver();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ExternalSeriesMetadata?)null);

        var result = await resolver.ResolveAsync(
            "/library/Some Series/c1.cbz",
            new ComicMetadata { Series = "Pre-Existing Series Name" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.ExistingMetadata, result.WinningStep);
        Assert.Equal("Pre-Existing Series Name", result.ResolvedSeries);
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupHit_AppliesGlobalLanguage()
    {
        _settings.DefaultPreferredLanguage = "ja";
        var resolver = BuildResolver();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExternalSeriesMetadata
                 {
                     CanonicalTitle = "One Piece",
                     Source = "MangaDex",
                     LocalizedTitles = new List<LocalizedTitle>
                     {
                         new("One Piece", "en"),
                         new("ワンピース", "ja")
                     }
                 });

        var result = await resolver.ResolveAsync("/library/One Piece/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        Assert.Equal("ワンピース", result.ResolvedSeries);
        Assert.Equal("ja", result.AppliedLanguage);
    }

    [Fact]
    public async Task ResolveAsync_MutateCacheFalse_DoesNotAddFolderAlias()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            IsUserCanonical = true
        };
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);

        // Folder name "OP" differs from canonical so EnsureFolderNameIsAlias
        // would normally be invoked.
        await resolver.ResolveAsync("/library/OP/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        _cache.Verify(c => c.SetUserAliasesAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveForRecord_MatchesFilePathResolution_ForMatchedCacheRecord()
    {
        // Parity invariant: the string returned by ResolveForRecord(record)
        // — used by SeriesLibraryService.DisplayTitle and the language audit —
        // must equal the <Series> value that ResolveAsync (used by the
        // normalize pipeline to write ComicInfo.xml) produces for a file
        // reaching the same record. Drift between the two surfaces is a bug.
        _settings.DefaultPreferredLanguage = "en";
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = "ja",
            LocalizedTitles =
            {
                new LocalizedTitle("ワンピース", "ja"),
                new LocalizedTitle("One Piece", "en")
            }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var fromFile = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);
        var fromRecord = resolver.ResolveForRecord(record);

        Assert.Equal(fromFile.ResolvedSeries, fromRecord);
        Assert.Equal("ワンピース", fromRecord);
    }

    [Fact]
    public async Task ResolveForRecord_MatchesFilePathResolution_ForUserCanonicalRecord()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            IsUserCanonical = true,
            PreferredLanguage = "ja",
            LocalizedTitles =
            {
                new LocalizedTitle("ワンピース", "ja"),
                new LocalizedTitle("One Piece", "en")
            }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var fromFile = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);
        var fromRecord = resolver.ResolveForRecord(record);

        // User-canonical override wins in both surfaces, regardless of language preference.
        Assert.Equal(fromFile.ResolvedSeries, fromRecord);
        Assert.Equal("One Piece (User Override)", fromRecord);
    }

    [Fact]
    public void ResolveForRecord_AppliesGlobalDefaultLanguage_WhenNoPerSeriesPreference()
    {
        _settings.DefaultPreferredLanguage = "ja";
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "naruto",
            CanonicalTitle = "Naruto",
            PreferredLanguage = null,
            LocalizedTitles =
            {
                new LocalizedTitle("Naruto", "en"),
                new LocalizedTitle("ナルト", "ja")
            }
        };

        Assert.Equal("ナルト", resolver.ResolveForRecord(record));
    }

    [Fact]
    public void ResolveForRecord_NullRecord_Throws()
    {
        var resolver = BuildResolver();
        Assert.Throws<ArgumentNullException>(() => resolver.ResolveForRecord(null!));
    }

    [Fact]
    public async Task ResolveAsync_PinnedLocalizedTitle_ReportsPinAsWinningStep()
    {
        _settings.DefaultPreferredLanguage = null;
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = "ja",
            PinnedLocalizedTitle = "Wan Piisu",
            LocalizedTitles =
            {
                new LocalizedTitle("One Piece", "en"),
                new LocalizedTitle("ワンピース", "ja"),
                new LocalizedTitle("Wan Piisu", "ja-Latn")
            }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.PinnedLocalizedTitle, result.WinningStep);
        Assert.Equal("Wan Piisu", result.ResolvedSeries);
        // AppliedLanguage is meaningless when the pin decides the result;
        // the resolver leaves it null so consumers don't surface a
        // misleading "applied language" in audit/diagnostic output.
        Assert.Null(result.AppliedLanguage);
        Assert.Contains("pinned", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UserCanonical_StillWinsOverPin()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            IsUserCanonical = true,
            PinnedLocalizedTitle = "English Title",
            LocalizedTitles = { new LocalizedTitle("English Title", "en") }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserCanonical, result.WinningStep);
        Assert.Equal("One Piece (User Override)", result.ResolvedSeries);
    }

    [Fact]
    public void ResolveForRecord_PinnedLocalizedTitle_IsHonored()
    {
        // Parity: the record-only path (used by SeriesLibraryService and
        // the language audit) returns the pinned title just like the
        // file-path ResolveAsync does, so library display and on-disk
        // <Series> stay in lockstep when the user pins a title.
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            PreferredLanguage = "ja",
            PinnedLocalizedTitle = "Wan Piisu",
            LocalizedTitles =
            {
                new LocalizedTitle("ワンピース", "ja"),
                new LocalizedTitle("Wan Piisu", "ja-Latn")
            }
        };

        Assert.Equal("Wan Piisu", resolver.ResolveForRecord(record));
    }
}
