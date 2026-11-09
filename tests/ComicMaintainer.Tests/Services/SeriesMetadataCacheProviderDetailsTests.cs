using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// How a lookup's series details reach the cache record: on a refresh, on a new match and when
/// metadata is cleared, without marking the series' files out of date.
/// </summary>
public sealed class SeriesMetadataCacheProviderDetailsTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly SeriesMetadataCacheService _service;

    public SeriesMetadataCacheProviderDetailsTests()
    {
        var dbName = $"SeriesMetadataCacheDetails_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns(new AppSettings { DownloadExternalSeriesImages = false });
        _service = new SeriesMetadataCacheService(
            _dbFactory,
            _external.Object,
            new Mock<ISeriesImageStore>().Object,
            new Mock<ISeriesCoverArtifactQueue>().Object,
            settings.Object,
            NullLogger<SeriesMetadataCacheService>.Instance);
    }

    public void Dispose() => _provider.Dispose();

    private static ExternalSeriesMetadata Lookup(string title = "Berserk", string source = "AniListManga", string? sourceId = "30002") => new()
    {
        CanonicalTitle = title,
        Source = source,
        SourceId = sourceId,
        Synopsis = "<p>Guts   fights.</p>",
        SiteUrl = "https://anilist.co/manga/30002",
        Genres = new List<string> { "Action", "Drama" },
        PublicationStatus = SeriesPublicationStatus.Completed,
        TotalChapters = 364,
        Origin = "ja"
    };

    /// <summary>A record cached before this release: matched, with none of the new details.</summary>
    private async Task<SeriesMetadataCacheEntity> SeedLegacyAsync(string title = "Berserk", string status = "success", string? source = "AniListManga", string? sourceId = "30002")
    {
        var entity = new SeriesMetadataCacheEntity
        {
            NormalizedKey = _service.NormalizeKey(title),
            CanonicalTitle = title,
            Aliases = new List<string>(),
            UserAliases = new List<string>(),
            Source = source,
            SourceId = sourceId,
            LookupStatus = status,
            LocalizedTitlesJson = "[{\"title\":\"Berserk\",\"language\":\"en\"}]",
            MetadataVersion = 3
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SeriesMetadataCache.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private void ProviderAnswers(ExternalSeriesMetadata? lookup)
    {
        _external.Setup(e => e.LookupMatchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);
    }

    private static void AssertHasDetails(SeriesMetadataCacheRecord record)
    {
        Assert.Equal("Guts fights.", record.Synopsis);
        Assert.Equal("https://anilist.co/manga/30002", record.SiteUrl);
        Assert.Equal(new[] { "Action", "Drama" }, record.Genres);
        Assert.Equal(SeriesPublicationStatus.Completed, record.PublicationStatus);
        Assert.Equal(364, record.TotalChapters);
        Assert.Equal("ja", record.Origin);
    }

    [Fact]
    public async Task Refresh_FillsInAnEntryCachedBeforeThisRelease_WithoutMarkingItsFilesStale()
    {
        await SeedLegacyAsync();
        var lookup = Lookup();
        lookup.LocalizedTitles = new List<LocalizedTitle> { new("Berserk", "en") };
        ProviderAnswers(lookup);

        var record = await _service.RefreshAsync("Berserk");

        AssertHasDetails(record);

        // Same names, so the library scan's retag pass must not pick this series' files up.
        Assert.Equal(3, record.MetadataVersion);
    }

    [Fact]
    public async Task Refresh_OfANewSeries_StoresTheDetails()
    {
        ProviderAnswers(Lookup());

        var record = await _service.RefreshAsync("Berserk");

        AssertHasDetails(record);
    }

    [Fact]
    public async Task Refresh_ThatFindsNothing_KeepsWhatTheMatchHeld()
    {
        var entity = await SeedLegacyAsync();
        ProviderAnswers(Lookup());
        await _service.RefreshAsync("Berserk");
        _external.Reset();
        ProviderAnswers(null);

        var record = await _service.RefreshAsync("Berserk");

        Assert.Equal(entity.NormalizedKey, record.NormalizedKey);
        AssertHasDetails(record);
    }

    [Fact]
    public async Task Refresh_FromTheSameEntry_KeepsDetailsTheAnswerLeftOut()
    {
        await SeedLegacyAsync();
        ProviderAnswers(Lookup());
        await _service.RefreshAsync("Berserk");
        _external.Reset();
        ProviderAnswers(new ExternalSeriesMetadata { CanonicalTitle = "Berserk", Source = "AniListManga", SourceId = "30002" });

        var record = await _service.RefreshAsync("Berserk");

        AssertHasDetails(record);
    }

    [Fact]
    public async Task ManualMatch_ToAnotherEntry_DropsTheOldEntrysDetails()
    {
        await SeedLegacyAsync();
        await _service.ApplyExternalMatchAsync("Berserk", Lookup());

        var record = await _service.ApplyExternalMatchAsync("Berserk", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Berserk of Gluttony",
            Source = "AniListManga",
            SourceId = "99999"
        });

        Assert.Null(record.Synopsis);
        Assert.Null(record.SiteUrl);
        Assert.Empty(record.Genres);
        Assert.Null(record.PublicationStatus);
        Assert.Null(record.TotalChapters);
        Assert.Null(record.Origin);
    }

    [Fact]
    public async Task ManualMatch_FromAnotherProvider_DropsTheOldProvidersDetails()
    {
        await SeedLegacyAsync();
        await _service.ApplyExternalMatchAsync("Berserk", Lookup());

        var record = await _service.ApplyExternalMatchAsync("Berserk", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Berserk",
            Source = "ComicVine",
            SourceId = "30002",
            Genres = new List<string> { "Fantasy" }
        });

        Assert.Equal(new[] { "Fantasy" }, record.Genres);
        Assert.Null(record.Origin);
    }

    [Fact]
    public async Task ManualMatch_WithoutAnIdAtTheSameProvider_KeepsTheDetails()
    {
        await SeedLegacyAsync(sourceId: null);
        await _service.ApplyExternalMatchAsync("Berserk", Lookup());

        var record = await _service.ApplyExternalMatchAsync("Berserk", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Berserk",
            Source = "AniListManga",
            SourceId = null
        });

        AssertHasDetails(record);
    }

    [Fact]
    public async Task ManualMatch_OnANeverMatchedRecord_HasNothingToDrop()
    {
        await SeedLegacyAsync(status: "not_found", source: null, sourceId: null);

        var record = await _service.ApplyExternalMatchAsync("Berserk", Lookup());

        AssertHasDetails(record);
    }

    [Fact]
    public async Task AutomaticLookup_OnANewSeries_StoresTheDetails()
    {
        var record = await _service.PersistExternalLookupAsync("Berserk", Lookup());

        AssertHasDetails(record);
    }

    [Fact]
    public async Task ValuesPassedBackThroughTheClientAreCleaned()
    {
        // A manual match sends the candidate back from the browser.
        var record = await _service.ApplyExternalMatchAsync("Berserk", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Berserk",
            Source = "AniListManga",
            SiteUrl = "javascript:alert(1)",
            Genres = new List<string> { "<b>Action</b>", "  ", "action" },
            PublicationStatus = "<script>",
            TotalChapters = -4,
            Origin = "<ja>"
        });

        Assert.Null(record.SiteUrl);
        Assert.Equal(new[] { "Action" }, record.Genres);
        Assert.Null(record.PublicationStatus);
        Assert.Null(record.TotalChapters);
        Assert.Null(record.Origin);
    }

    [Fact]
    public async Task ClearExternalMetadata_RemovesTheDetails()
    {
        await SeedLegacyAsync();
        await _service.ApplyExternalMatchAsync("Berserk", Lookup());

        var record = await _service.ClearExternalMetadataAsync(_service.NormalizeKey("Berserk"));

        Assert.Null(record!.Synopsis);
        Assert.Null(record.SiteUrl);
        Assert.Empty(record.Genres);
        Assert.Null(record.PublicationStatus);
        Assert.Null(record.TotalChapters);
        Assert.Null(record.Origin);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task UnreadableStoredGenresReadAsNone(string stored)
    {
        await SeedLegacyAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var row = await db.SeriesMetadataCache.SingleAsync();
            row.GenresJson = stored;
            await db.SaveChangesAsync();
        }

        var record = await _service.GetAsync(_service.NormalizeKey("Berserk"));

        Assert.Empty(record!.Genres);
    }
}
