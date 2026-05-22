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
}
