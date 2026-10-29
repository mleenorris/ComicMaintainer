using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public sealed class SeriesMetadataCacheServiceCoverageTests : IDisposable
{
    private const string SharedTitleJson = "[{\"title\":\"Shared Title\",\"language\":\"en\"}]";

    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbFactory;
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly Mock<ISeriesFolderCoverWriter> _folderCoverWriter = new();
    private readonly Mock<ISeriesArchiveCoverWriteQueue> _archiveCoverQueue = new();
    private readonly Mock<ISeriesArchiveCoverWriter> _archiveCoverWriter = new();
    private readonly AppSettings _settings = new() { DownloadExternalSeriesImages = true };
    private readonly ListLogger _logger = new();
    private readonly SeriesMetadataCacheService _service;

    public SeriesMetadataCacheServiceCoverageTests()
    {
        var dbName = $"SeriesMetadataCacheCoverage_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        // Null checks are disabled so rows written by older schema versions
        // (null alias columns) can be seeded and read back.
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName, b => b.EnableNullChecks(false)));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var settingsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        settingsMonitor.Setup(s => s.CurrentValue).Returns(_settings);
        _service = new SeriesMetadataCacheService(
            _dbFactory,
            _external.Object,
            _imageStore.Object,
            _folderCoverWriter.Object,
            _archiveCoverQueue.Object,
            _archiveCoverWriter.Object,
            settingsMonitor.Object,
            _logger);
    }

    public void Dispose() => _provider.Dispose();

    private static SeriesMetadataCacheEntity Entity(string key, string canonical, string? status = "success") => new()
    {
        NormalizedKey = key,
        CanonicalTitle = canonical,
        LookupStatus = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private async Task SeedAsync(params SeriesMetadataCacheEntity[] entities)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SeriesMetadataCache.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> AllKeysAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SeriesMetadataCache.Select(e => e.NormalizedKey).OrderBy(k => k).ToListAsync();
    }

    [Theory]
    [InlineData("!!! ???")]
    [InlineData("---")]
    public void NormalizeKey_PunctuationOnly_ReturnsUnknownSeries(string value)
    {
        Assert.Equal("unknown-series", _service.NormalizeKey(value));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForBlankOrMissingKey_AndMapsExistingRecord()
    {
        var entity = Entity("series-a", "Series A");
        entity.Aliases = new List<string> { "Alias A" };
        entity.UserAliases = new List<string> { "User A" };
        entity.Source = "MangaDex";
        entity.Synopsis = "About A";
        entity.MetadataVersion = 4;
        await SeedAsync(entity);

        Assert.Null(await _service.GetAsync(" "));
        Assert.Null(await _service.GetAsync("missing"));
        var record = await _service.GetAsync("series-a");

        Assert.NotNull(record);
        Assert.Equal("Series A", record!.CanonicalTitle);
        Assert.Equal(new[] { "Alias A" }, record.Aliases);
        Assert.Equal(new[] { "User A" }, record.UserAliases);
        Assert.Equal("MangaDex", record.Source);
        Assert.Equal("About A", record.Synopsis);
        Assert.Equal(4, record.MetadataVersion);
        Assert.Equal("Series A", record.ResolvedSeriesName);
    }

    [Fact]
    public async Task GetAsync_RowWithNullAliasColumns_MapsToEmptyLists()
    {
        var entity = Entity("legacy", "Legacy Series");
        entity.Aliases = null!;
        entity.UserAliases = null!;
        await SeedAsync(entity);

        var record = await _service.GetAsync("legacy");

        Assert.NotNull(record);
        Assert.Empty(record!.Aliases);
        Assert.Empty(record.UserAliases);
    }

    [Fact]
    public async Task GetByTitleAsync_BlankTitle_ReturnsNull()
    {
        await SeedAsync(Entity("series-a", "Series A"));

        Assert.Null(await _service.GetByTitleAsync("   "));
    }

    [Fact]
    public async Task GetByTitleAsync_LocalizedTitleMatch_PrefersMostAuthoritativeMatchingStatus()
    {
        var older = DateTime.UtcNow.AddDays(-2);
        var error = Entity("rank-error", "Error Series", "error");
        error.LocalizedTitlesJson = SharedTitleJson;
        error.LastLookupUtc = older;
        var cleared = Entity("rank-cleared", "Cleared Series", "cleared");
        cleared.LocalizedTitlesJson = SharedTitleJson;
        var noStatus = Entity("rank-null", "Null Status Series", null);
        noStatus.LocalizedTitlesJson = SharedTitleJson;
        var unknownStatus = Entity("rank-pending", "Pending Series", "pending");
        unknownStatus.LocalizedTitlesJson = SharedTitleJson;
        // Higher-ranked rows whose localized titles cannot be read must not match.
        var malformed = Entity("rank-malformed", "Malformed Series", "manual_match");
        malformed.LocalizedTitlesJson = "{not json";
        var nullJson = Entity("rank-null-json", "Null Json Series", "manual");
        nullJson.LocalizedTitlesJson = "null";
        var nullLists = Entity("rank-null-lists", "Null Lists Series", "success");
        nullLists.Aliases = null!;
        nullLists.UserAliases = null!;
        nullLists.LocalizedTitlesJson = "[null,{\"title\":\"Other Title\",\"language\":\"en\"}]";
        await SeedAsync(error, cleared, noStatus, unknownStatus, malformed, nullJson, nullLists);

        var record = await _service.GetByTitleAsync("Shared Title");

        Assert.NotNull(record);
        Assert.Equal("rank-error", record!.NormalizedKey);
        Assert.Equal("Shared Title", Assert.Single(record.LocalizedTitles).Title);
    }

    [Fact]
    public async Task GetAsync_MalformedLocalizedTitlesJson_MapsToEmptyList()
    {
        var entity = Entity("malformed", "Malformed");
        entity.LocalizedTitlesJson = "[{\"title\":";
        await SeedAsync(entity);

        var record = await _service.GetAsync("malformed");

        Assert.NotNull(record);
        Assert.Empty(record!.LocalizedTitles);
        Assert.Equal("Malformed", record.ResolvedSeriesName);
    }

    [Fact]
    public async Task SetUserAliasesAsync_BlankTitle_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.SetUserAliasesAsync(" ", new[] { "a" }));
        Assert.Equal("seriesTitle", ex.ParamName);
    }

    [Fact]
    public async Task SetUserAliasesAsync_NullAliases_CreatesManualRecordWithoutAliases()
    {
        var before = _service.ChangeVersion;

        var record = await _service.SetUserAliasesAsync("Series B", null!);

        Assert.Equal("series-b", record.NormalizedKey);
        Assert.Equal("manual", record.LookupStatus);
        Assert.Empty(record.UserAliases);
        Assert.Equal(before + 1, _service.ChangeVersion);
    }

    [Theory]
    [InlineData(" ", "alias")]
    [InlineData("series-a", " ")]
    [InlineData("missing-key", "alias")]
    public async Task RemoveUserAliasAsync_BlankArgumentsOrMissingRecord_ReturnsNull(string key, string alias)
    {
        await SeedAsync(Entity("series-a", "Series A"));

        Assert.Null(await _service.RemoveUserAliasAsync(key, alias));
    }

    [Fact]
    public async Task RemoveUserAliasAsync_AliasNotPresent_ReturnsRecordUnchanged()
    {
        var entity = Entity("series-a", "Series A");
        entity.UserAliases = new List<string> { "Keep Me" };
        entity.MetadataVersion = 2;
        await SeedAsync(entity);
        var before = _service.ChangeVersion;

        var record = await _service.RemoveUserAliasAsync("series-a", "Not There");

        Assert.NotNull(record);
        Assert.Equal(new[] { "Keep Me" }, record!.UserAliases);
        Assert.Equal(2, record.MetadataVersion);
        Assert.Equal(before, _service.ChangeVersion);
    }

    [Fact]
    public async Task RefreshAsync_BlankTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.RefreshAsync("  "));
        _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, "not_found")]
    [InlineData(true, "error")]
    public async Task RefreshAsync_ExistingAutomaticRecord_LookupMissOrFailure_RecordsStatusOnly(bool throws, string expectedStatus)
    {
        var entity = Entity("series-x", "Series X Canonical");
        entity.Aliases = new List<string> { "Old Alias" };
        entity.Source = "MangaDex";
        entity.MetadataVersion = 3;
        await SeedAsync(entity);
        var setup = _external.Setup(e => e.LookupSeriesAsync("Series X", It.IsAny<CancellationToken>()));
        if (throws)
        {
            setup.ThrowsAsync(new HttpRequestException("provider down"));
        }
        else
        {
            setup.ReturnsAsync((ExternalSeriesMetadata?)null);
        }
        var before = _service.ChangeVersion;

        var record = await _service.RefreshAsync("Series X");

        Assert.Equal(expectedStatus, record.LookupStatus);
        Assert.Equal("Series X Canonical", record.CanonicalTitle);
        Assert.Equal(new[] { "Old Alias" }, record.Aliases);
        Assert.Equal("MangaDex", record.Source);
        Assert.NotNull(record.LastLookupUtc);
        Assert.Equal(3, record.MetadataVersion);
        Assert.Equal(before + 1, _service.ChangeVersion);
        var persisted = await _service.GetAsync("series-x");
        Assert.Equal(expectedStatus, persisted!.LookupStatus);
    }

    [Fact]
    public async Task RefreshAsync_CallerCancelsDuringLookup_RethrowsWithoutLoggingAProviderFailure()
    {
        await SeedAsync(Entity("series-x", "Series X Canonical"));
        using var cts = new CancellationTokenSource();
        _external.Setup(e => e.LookupSeriesAsync("Series X", cts.Token))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var before = _service.ChangeVersion;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RefreshAsync("Series X", cts.Token));

        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(before, _service.ChangeVersion);
        var persisted = await _service.GetAsync("series-x");
        Assert.Equal("success", persisted!.LookupStatus);
        Assert.Null(persisted.LastLookupUtc);
    }

    [Fact]
    public async Task RefreshAsync_ExistingRecord_LookupWithBlankCanonicalAndNullAliases_KeepsCanonical()
    {
        var entity = Entity("series-y", "Series Y");
        entity.Aliases = new List<string> { "Old Alias" };
        entity.LocalizedTitlesJson = "[{\"title\":\"Old Title\",\"language\":\"en\"}]";
        await SeedAsync(entity);
        _external.Setup(e => e.LookupSeriesAsync("Series Y", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "  ",
                Aliases = null!,
                Source = "AniList",
                LocalizedTitles = new List<LocalizedTitle>()
            });

        var record = await _service.RefreshAsync("Series Y");

        Assert.Equal("success", record.LookupStatus);
        Assert.Equal("Series Y", record.CanonicalTitle);
        Assert.Empty(record.Aliases);
        Assert.Equal("AniList", record.Source);
        Assert.Empty(record.LocalizedTitles);
        Assert.Equal(2, record.MetadataVersion);
    }

    [Fact]
    public async Task RefreshAsync_NewRecord_LookupWithNullAliases_PersistsNonBlankLocalizedTitles()
    {
        _external.Setup(e => e.LookupSeriesAsync("Series Z", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Canon Z",
                Aliases = null!,
                Source = "ComicVine",
                LocalizedTitles = new List<LocalizedTitle> { new("Canon Z", "en"), new(" ", "ja") }
            });

        var record = await _service.RefreshAsync("Series Z");

        Assert.Equal("series-z", record.NormalizedKey);
        Assert.Equal("Canon Z", record.CanonicalTitle);
        Assert.Equal("success", record.LookupStatus);
        Assert.Empty(record.Aliases);
        var title = Assert.Single(record.LocalizedTitles);
        Assert.Equal("Canon Z", title.Title);
        Assert.Equal("en", title.Language);
    }

    [Fact]
    public async Task RefreshAsync_ImageDownloadsDisabled_DoesNotDownload()
    {
        _settings.DownloadExternalSeriesImages = false;
        _external.Setup(e => e.LookupSeriesAsync("Series I", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Series I", ImageUrl = "https://img.example/i.jpg" });

        var record = await _service.RefreshAsync("Series I");

        Assert.Null(record.ImageStatus);
        Assert.Null(record.RemoteImageUrl);
        _imageStore.Verify(s => s.DownloadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_SameRemoteImageAlreadyDownloaded_SkipsDownload()
    {
        const string url = "https://img.example/same.jpg";
        var entity = Entity("series-i", "Series I");
        entity.RemoteImageUrl = url;
        entity.LocalImageFile = "series-i.jpg";
        entity.ImageContentType = "image/jpeg";
        entity.ImageStatus = "downloaded";
        await SeedAsync(entity);
        _imageStore.Setup(s => s.ResolveAbsolutePath("series-i.jpg")).Returns("/cache/series-i.jpg");
        _external.Setup(e => e.LookupSeriesAsync("Series I", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Series I", ImageUrl = url });

        var record = await _service.RefreshAsync("Series I");

        Assert.Equal("downloaded", record.ImageStatus);
        Assert.Equal("series-i.jpg", record.LocalImageFile);
        _imageStore.Verify(s => s.DownloadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, true, "downloaded")]
    [InlineData("series-i.jpg", false, "downloaded")]
    [InlineData("series-i.jpg", true, "failed")]
    public async Task RefreshAsync_SameRemoteImageNotReusable_Redownloads(string? localFile, bool resolves, string imageStatus)
    {
        const string url = "https://img.example/same.jpg";
        var entity = Entity("series-i", "Series I");
        entity.RemoteImageUrl = url;
        entity.LocalImageFile = localFile;
        entity.ImageContentType = "image/jpeg";
        entity.ImageStatus = imageStatus;
        await SeedAsync(entity);
        _imageStore.Setup(s => s.ResolveAbsolutePath("series-i.jpg")).Returns(resolves ? "/cache/series-i.jpg" : null);
        _imageStore.Setup(s => s.DownloadAsync("series-i", url, localFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("series-i-new.jpg", "image/jpeg", 42));
        _external.Setup(e => e.LookupSeriesAsync("Series I", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata { CanonicalTitle = "Series I", ImageUrl = url });

        var record = await _service.RefreshAsync("Series I");

        Assert.Equal("downloaded", record.ImageStatus);
        Assert.Equal("series-i-new.jpg", record.LocalImageFile);
        _imageStore.Verify(s => s.DownloadAsync("series-i", url, localFile, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetUserImageAsync_InvalidArguments_Throw()
    {
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var blank = await Assert.ThrowsAsync<ArgumentException>(() => _service.SetUserImageAsync(" ", content, "image/png"));
        var nullContent = await Assert.ThrowsAsync<ArgumentNullException>(() => _service.SetUserImageAsync("Series U", null!, "image/png"));

        Assert.Equal("seriesTitle", blank.ParamName);
        Assert.Equal("content", nullContent.ParamName);
        _imageStore.Verify(s => s.SaveUserImageAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetUserImageAsync_UnresolvableStoredFile_SkipsCoverWriters()
    {
        _imageStore.Setup(s => s.SaveUserImageAsync("series-u", It.IsAny<Stream>(), "image/png", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("series-u.png", "image/png", 3));
        _imageStore.Setup(s => s.ResolveAbsolutePath("series-u.png")).Returns((string?)null);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var record = await _service.SetUserImageAsync("Series U", content, "image/png");

        Assert.Equal("user", record.ImageStatus);
        Assert.Equal("series-u.png", record.LocalImageFile);
        _folderCoverWriter.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _archiveCoverQueue.Verify(q => q.EnqueueWrite(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData("", "image/png")]
    [InlineData("series-u.png", "")]
    public async Task SetUserImageAsync_IncompleteStoreResult_SkipsCoverWriters(string fileName, string contentType)
    {
        _imageStore.Setup(s => s.SaveUserImageAsync("series-u", It.IsAny<Stream>(), "image/png", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult(fileName, contentType, 3));
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var record = await _service.SetUserImageAsync("Series U", content, "image/png");

        Assert.Equal("user", record.ImageStatus);
        _imageStore.Verify(s => s.ResolveAbsolutePath(It.IsAny<string>()), Times.Never);
        _folderCoverWriter.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetUserImageAsync_FolderCoverWriterThrows_IsLoggedAndImageStillSaved()
    {
        _imageStore.Setup(s => s.SaveUserImageAsync("series-u", It.IsAny<Stream>(), "image/png", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("series-u.png", "image/png", 3));
        _imageStore.Setup(s => s.ResolveAbsolutePath("series-u.png")).Returns("/cache/series-u.png");
        _folderCoverWriter.Setup(w => w.WriteAsync(
                "series-u", "/cache/series-u.png", "image/png", false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("read-only folder"));
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var before = _service.ChangeVersion;

        var record = await _service.SetUserImageAsync("Series U", content, "image/png");

        Assert.Equal("user", record.ImageStatus);
        Assert.Equal("user-upload", record.RemoteImageUrl);
        Assert.Equal(before + 1, _service.ChangeVersion);
        Assert.Equal("user", (await _service.GetAsync("series-u"))!.ImageStatus);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Debug
            && e.Exception is IOException
            && e.Message.Contains("Failed to write on-disk series cover for series-u"));
        _archiveCoverQueue.Verify(q => q.EnqueueWrite(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ClearImageAsync_BlankOrMissingKey_ReturnsNull()
    {
        Assert.Null(await _service.ClearImageAsync(" "));
        Assert.Null(await _service.ClearImageAsync("missing"));
        _archiveCoverQueue.Verify(q => q.EnqueueRemove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClearImageAsync_FolderCoverRemovalFails_StillClearsAndQueuesArchiveRemoval()
    {
        var entity = Entity("series-c", "Series C");
        entity.LocalImageFile = "series-c.jpg";
        entity.ImageContentType = "image/jpeg";
        entity.ImageStatus = "downloaded";
        entity.RemoteImageUrl = "https://img.example/c.jpg";
        await SeedAsync(entity);
        _folderCoverWriter.Setup(w => w.RemoveAsync("series-c", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("denied"));

        var record = await _service.ClearImageAsync("series-c");

        Assert.NotNull(record);
        Assert.Equal("none", record!.ImageStatus);
        Assert.Null(record.LocalImageFile);
        Assert.Null(record.RemoteImageUrl);
        Assert.Equal("none", (await _service.GetAsync("series-c"))!.ImageStatus);
        _imageStore.Verify(s => s.Delete("series-c.jpg"), Times.Once);
        _archiveCoverQueue.Verify(q => q.EnqueueRemove("series-c"), Times.Once);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Debug
            && e.Exception is UnauthorizedAccessException
            && e.Message.Contains("Failed to remove on-disk series cover for series-c"));
    }

    [Fact]
    public async Task ReapplyImageArtifactsAsync_BlankTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReapplyImageArtifactsAsync(" ", embedArchiveInline: true));
    }

    [Fact]
    public async Task ReapplyImageArtifactsAsync_UnresolvableImageFile_ReturnsFalse()
    {
        var entity = Entity("series-r", "Series R");
        entity.LocalImageFile = "series-r.jpg";
        entity.ImageContentType = "image/jpeg";
        entity.ImageStatus = "downloaded";
        await SeedAsync(entity);
        _imageStore.Setup(s => s.ResolveAbsolutePath("series-r.jpg")).Returns((string?)null);

        var applied = await _service.ReapplyImageArtifactsAsync("Series R", embedArchiveInline: true);

        Assert.False(applied);
        _folderCoverWriter.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _archiveCoverWriter.Verify(w => w.WriteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyExternalMatchAsync_ExistingRecord_RefreshesSynopsisAndAcceptsNullAliases()
    {
        var entity = Entity("series-m", "Series M", "not_found");
        entity.Aliases = new List<string> { "Stale Alias" };
        entity.Synopsis = "Old synopsis";
        await SeedAsync(entity);

        var record = await _service.ApplyExternalMatchAsync("Series M", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Matched M",
            Aliases = null!,
            Source = "ComicVine",
            Synopsis = "<p>New   synopsis</p>"
        });

        Assert.Equal("manual_match", record.LookupStatus);
        Assert.Equal("Matched M", record.CanonicalTitle);
        Assert.Empty(record.Aliases);
        Assert.Equal("New synopsis", record.Synopsis);
        Assert.Equal(2, record.MetadataVersion);
    }

    [Fact]
    public async Task PersistExternalLookupAsync_NewRecord_AcceptsNullAliases()
    {
        var record = await _service.PersistExternalLookupAsync("Fresh Series", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Fresh Canonical",
            Aliases = null!,
            Source = "MangaDex"
        });

        Assert.Equal("fresh-series", record.NormalizedKey);
        Assert.Equal("success", record.LookupStatus);
        Assert.Empty(record.Aliases);
        Assert.Equal("Fresh Canonical", Assert.Single(record.LocalizedTitles).Title);
    }

    [Fact]
    public async Task ClearExternalMetadataAsync_BlankKey_ReturnsNull()
    {
        var before = _service.ChangeVersion;

        Assert.Null(await _service.ClearExternalMetadataAsync("  "));
        Assert.Equal(before, _service.ChangeVersion);
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_BlankTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetPreferredLanguageAsync(" ", "ja"));
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_MetadataVersionAtMaximum_Saturates()
    {
        var entity = Entity("series-v", "Series V");
        entity.MetadataVersion = int.MaxValue;
        await SeedAsync(entity);

        var record = await _service.SetPreferredLanguageAsync("Series V", "ja");

        Assert.Equal("ja", record.PreferredLanguage);
        Assert.Equal(int.MaxValue, record.MetadataVersion);
    }

    [Fact]
    public async Task SetSeriesNameAsync_BlankTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetSeriesNameAsync(" ", "Name"));
    }

    [Fact]
    public async Task SetSeriesNameAsync_NoRecordAndNameMatchesTitle_CreatesRecordWithoutUserAlias()
    {
        var record = await _service.SetSeriesNameAsync("Series N", " series n ");

        Assert.NotNull(record);
        Assert.Equal("series-n", record!.NormalizedKey);
        Assert.Equal("Series N", record.CanonicalTitle);
        Assert.Equal("series n", record.SeriesName);
        Assert.Empty(record.UserAliases);
        Assert.Equal("manual", record.LookupStatus);
    }

    [Fact]
    public async Task SetSeriesNameAsync_RowWithNullAliasColumns_AddsCustomNameAsUserAlias()
    {
        var entity = Entity("legacy", "Legacy Series");
        entity.Aliases = null!;
        entity.UserAliases = null!;
        await SeedAsync(entity);

        var record = await _service.SetSeriesNameAsync("Legacy Series", "Custom Name");

        Assert.NotNull(record);
        Assert.Equal("Custom Name", record!.SeriesName);
        Assert.Equal(new[] { "Custom Name" }, record.UserAliases);
        Assert.Equal(2, record.MetadataVersion);
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_FewerThanTwoRecords_ReturnsZero()
    {
        await SeedAsync(Entity("only", "Only", "not_found"));
        var before = _service.ChangeVersion;

        Assert.Equal(0, await _service.CleanupStaleSiblingRecordsAsync());
        Assert.Equal(before, _service.ChangeVersion);
        Assert.Equal(new[] { "only" }, await AllKeysAsync());
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_RemovesOnlyUnprotectedSubsumedSiblings_AndLogsKeys()
    {
        var owner = Entity("owner-title", "Owner Title", "success");
        owner.Aliases = new List<string>
        {
            "Null Status Sibling",
            "Named Sibling",
            "User Image Sibling",
            "Null Lists Sibling"
        };
        owner.LocalizedTitlesJson = "[{\"title\":\"Localized Sibling\",\"language\":\"ja\"}]";
        var nullStatus = Entity("null-status-sibling", "Null Status Sibling", null);
        var localized = Entity("localized-sibling", "Localized Sibling", "not_found");
        var named = Entity("named-sibling", "Named Sibling", "not_found");
        named.SeriesName = "Pinned Name";
        var userImage = Entity("user-image-sibling", "User Image Sibling", "error");
        userImage.ImageStatus = "user";
        var nullLists = Entity("null-lists-sibling", "Null Lists Sibling", "error");
        nullLists.Aliases = null!;
        nullLists.UserAliases = null!;
        await SeedAsync(owner, nullStatus, localized, named, userImage, nullLists);
        var before = _service.ChangeVersion;

        var removed = await _service.CleanupStaleSiblingRecordsAsync();

        Assert.Equal(3, removed);
        Assert.Equal(
            new[] { "named-sibling", "owner-title", "user-image-sibling" },
            await AllKeysAsync());
        Assert.Equal(before + 1, _service.ChangeVersion);
        var log = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("Cleaned up 3 stale sibling series-metadata cache record(s)", log.Message);
        Assert.Contains("null-status-sibling", log.Message);
        Assert.Contains("localized-sibling", log.Message);
        Assert.Contains("null-lists-sibling", log.Message);
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_KeepsSubsumedSiblingWithUserAliases()
    {
        var owner = Entity("owner-title", "Owner Title", "success");
        owner.Aliases = new List<string> { "Aliased Sibling" };
        var sibling = Entity("aliased-sibling", "Aliased Sibling", "not_found");
        sibling.UserAliases = new List<string> { "My Own Name" };
        await SeedAsync(owner, sibling);

        var removed = await _service.CleanupStaleSiblingRecordsAsync();

        Assert.Equal(0, removed);
        Assert.Equal(new[] { "aliased-sibling", "owner-title" }, await AllKeysAsync());
    }

    [Fact]
    public async Task GetByTitleAsync_ExactKeyMiss_ResolvesRecordByUserAlias()
    {
        var entity = Entity("folder-title", "Folder Title", "success");
        entity.UserAliases = new List<string> { "My Custom Alias" };
        await SeedAsync(Entity("other-series", "Other Series", "success"), entity);

        var record = await _service.GetByTitleAsync("my custom alias");

        Assert.NotNull(record);
        Assert.Equal("folder-title", record!.NormalizedKey);
        Assert.Equal(new[] { "My Custom Alias" }, record.UserAliases);
    }

    private sealed class ListLogger : ILogger<SeriesMetadataCacheService>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
