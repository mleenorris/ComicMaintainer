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
    public async Task ResolveAsync_UserSelectedNameRecord_Wins()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            SeriesName = "One Piece (User Override)"
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync("/library/One Piece/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("One Piece (User Override)", result.ResolvedSeries);
        Assert.Contains("user-selected", result.Explanation, StringComparison.OrdinalIgnoreCase);
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
            SeriesName = "One Piece (User Override)"
        };
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(record);

        // Folder name "OP" differs from canonical so EnsureFolderNameIsAlias
        // would normally be invoked.
        await resolver.ResolveAsync("/library/OP/c1.cbz", new ComicMetadata { Series = "One Piece" }, mutateCache: false);

        _cache.Verify(c => c.SetUserAliasesAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(),
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
    public async Task ResolveForRecord_MatchesFilePathResolution_ForUserSelectedNameRecord()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            SeriesName = "One Piece (User Override)",
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

        // User-selected name wins in both surfaces, regardless of language preference.
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
    public async Task ResolveAsync_SeriesName_ReportsUserSelectedNameAsWinningStep()
    {
        _settings.DefaultPreferredLanguage = null;
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            LookupStatus = "success",
            PreferredLanguage = "ja",
            SeriesName = "Wan Piisu",
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

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("Wan Piisu", result.ResolvedSeries);
        // AppliedLanguage is meaningless when the user-selected name decides the result;
        // the resolver leaves it null so consumers don't surface a
        // misleading "applied language" in audit/diagnostic output.
        Assert.Null(result.AppliedLanguage);
        Assert.Contains("user-selected", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_SeriesName_WinsOverLanguagePreference()
    {
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece (User Override)",
            SeriesName = "One Piece (User Override)",
            LocalizedTitles = { new LocalizedTitle("English Title", "en") }
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.UserSelectedName, result.WinningStep);
        Assert.Equal("One Piece (User Override)", result.ResolvedSeries);
    }

    [Fact]
    public void ResolveForRecord_SeriesName_IsHonored()
    {
        // Parity: the record-only path (used by SeriesLibraryService and
        // the language audit) returns the pinned title just like the
        // file-path ResolveAsync does, so library display and on-disk
        // <Series> stay in lockstep when the user selects a title.
        var resolver = BuildResolver();
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "One Piece",
            PreferredLanguage = "ja",
            SeriesName = "Wan Piisu",
            LocalizedTitles =
            {
                new LocalizedTitle("ワンピース", "ja"),
                new LocalizedTitle("Wan Piisu", "ja-Latn")
            }
        };

        Assert.Equal("Wan Piisu", resolver.ResolveForRecord(record));
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupHit_PersistsToCache_WhenMutateCacheTrue()
    {
        // External metadata returned by the auto-lookup step must be written
        // back to the cache so subsequent normalize/resolve calls hit the
        // matched-cache pass instead of re-querying the provider on every
        // file. This is the core "external metadata persistence" guarantee.
        var resolver = BuildResolver();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesMetadataCacheRecord?)null);
        var lookup = new ExternalSeriesMetadata
        {
            CanonicalTitle = "One Piece",
            Source = "MangaDex",
            LocalizedTitles = new List<LocalizedTitle> { new("One Piece", "en") }
        };
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(lookup);
        _cache.Setup(c => c.PersistExternalLookupAsync(It.IsAny<string>(), It.IsAny<ExternalSeriesMetadata>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SeriesMetadataCacheRecord
              {
                  NormalizedKey = "one-piece",
                  CanonicalTitle = "One Piece",
                  LookupStatus = "success",
                  LocalizedTitles = lookup.LocalizedTitles
              });

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: true);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        _cache.Verify(c => c.PersistExternalLookupAsync(
            It.IsAny<string>(),
            It.Is<ExternalSeriesMetadata>(m => m.CanonicalTitle == "One Piece"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ExternalLookupHit_DoesNotPersist_WhenMutateCacheFalse()
    {
        // Read-only resolution paths (previews, diagnostics) must not write
        // back to the cache even when an external lookup succeeds.
        var resolver = BuildResolver();
        _cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "One Piece", Source = "MangaDex" });

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: false);

        Assert.Equal(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        _cache.Verify(c => c.PersistExternalLookupAsync(
            It.IsAny<string>(),
            It.IsAny<ExternalSeriesMetadata>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_ClearedRecord_SkipsExternalLookup()
    {
        // After a user explicitly clears external metadata for a series, the
        // resolver must NOT silently re-fetch it from the provider on the
        // next normalize. Only an explicit refresh / manual match may
        // repopulate external metadata.
        var resolver = BuildResolver();
        var cleared = new SeriesMetadataCacheRecord
        {
            NormalizedKey = "one-piece",
            CanonicalTitle = "one-piece",
            LookupStatus = "cleared"
        };
        _cache.Setup(c => c.GetAsync("one-piece", It.IsAny<CancellationToken>())).ReturnsAsync(cleared);

        var result = await resolver.ResolveAsync(
            "/library/One Piece/c1.cbz",
            new ComicMetadata { Series = "One Piece" },
            mutateCache: true);

        // Should fall through past Step 4 to Step 5 (existing <Series>).
        Assert.NotEqual(SeriesNameResolutionStep.ExternalLookup, result.WinningStep);
        _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.PersistExternalLookupAsync(
            It.IsAny<string>(),
            It.IsAny<ExternalSeriesMetadata>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
