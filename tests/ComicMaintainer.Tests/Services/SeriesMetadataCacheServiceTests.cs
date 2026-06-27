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

public class SeriesMetadataCacheServiceTests
{
    private readonly SeriesMetadataCacheService _service;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly Mock<ISeriesFolderCoverWriter> _folderCoverWriter = new();
    private readonly Mock<ISeriesArchiveCoverWriteQueue> _archiveCoverQueue = new();

    public SeriesMetadataCacheServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        _dbContextFactory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var settingsMonitor = new Mock<IOptionsMonitor<AppSettings>>();
        settingsMonitor.Setup(s => s.CurrentValue).Returns(new AppSettings
        {
            DownloadExternalSeriesImages = true
        });
        _service = new SeriesMetadataCacheService(
            _dbContextFactory,
            _external.Object,
            _imageStore.Object,
            _folderCoverWriter.Object,
            _archiveCoverQueue.Object,
            settingsMonitor.Object,
            new Mock<ILogger<SeriesMetadataCacheService>>().Object);
    }

    [Fact]
    public void NormalizeKey_LowercasesAndReplacesSeparators()
    {
        Assert.Equal("the-dark-knight", _service.NormalizeKey("The Dark Knight!"));
        Assert.Equal("unknown-series", _service.NormalizeKey(""));
        Assert.Equal("unknown-series", _service.NormalizeKey("   "));
    }

    [Fact]
    public void NormalizeKey_PreservesNonAsciiLettersAndDistinguishesSimilarCjkTitles()
    {
        // Regression: the previous [^a-z0-9]+ sanitizer stripped all CJK
        // characters, collapsing "怪獣8号" and "8階級魔法使い" both to "8"
        // and producing false-positive merges between unrelated series. The
        // Unicode-aware sanitizer must keep Unicode letters so these two
        // titles produce different keys.
        var kaiju = _service.NormalizeKey("怪獣8号");
        var mage = _service.NormalizeKey("8階級魔法使い");

        Assert.NotEqual("8", kaiju);
        Assert.NotEqual("8", mage);
        Assert.NotEqual(kaiju, mage);

        // Accented Latin characters should be preserved too (lowercased).
        Assert.Equal("pokémon", _service.NormalizeKey("Pokémon"));
    }

    [Fact]
    public async Task SetUserAliasesAsync_CreatesRecordAndDedupes()
    {
        var record = await _service.SetUserAliasesAsync(
            "Series A",
            new[] { "Alias One", "alias one", "Alias Two", "" });

        Assert.Equal("series-a", record.NormalizedKey);
        Assert.Equal("Series A", record.CanonicalTitle);
        Assert.Equal(2, record.UserAliases.Count);
        Assert.Contains("Alias One", record.UserAliases);
        Assert.Contains("Alias Two", record.UserAliases);
        Assert.False(record.IsUserSelectedName);
    }

    [Fact]
    public async Task SetSeriesNameAsync_SetsUserSelectedName()
    {
        await _service.SetUserAliasesAsync("Series A", new[] { "Alias" });

        var record = await _service.SetSeriesNameAsync("Series A", "Canonical A");

        Assert.NotNull(record);
        Assert.Equal("Series A", record!.CanonicalTitle);
        Assert.Equal("Canonical A", record.SeriesName);
        Assert.True(record.IsUserSelectedName);
        Assert.Contains("Canonical A", record.UserAliases);
    }

    [Fact]
    public async Task RemoveUserAliasAsync_RemovesCaseInsensitive()
    {
        await _service.SetUserAliasesAsync("Series A", new[] { "Alias One", "Alias Two" });
        var updated = await _service.RemoveUserAliasAsync("series-a", "alias one");
        Assert.NotNull(updated);
        Assert.Single(updated!.UserAliases);
        Assert.Contains("Alias Two", updated.UserAliases);
    }

    [Fact]
    public async Task RefreshAsync_UpsertsProviderResultAndPreservesUserAliases()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });

        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "Dark Knight", "Bat-Man" },
                Source = "ComicVine"
            });

        var record = await _service.RefreshAsync("Batman");

        Assert.Equal("Batman", record.CanonicalTitle);
        Assert.Equal("ComicVine", record.Source);
        Assert.Equal("success", record.LookupStatus);
        Assert.Contains("Dark Knight", record.Aliases);
        Assert.Contains("Caped Crusader", record.UserAliases);
        Assert.NotNull(record.LastLookupUtc);
    }

    [Fact]
    public async Task RefreshAsync_RecordsNotFoundStatusWhenProviderReturnsNull()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var record = await _service.RefreshAsync("Unknown Title");

        Assert.Equal("not_found", record.LookupStatus);
        Assert.Equal("Unknown Title", record.CanonicalTitle);
    }

    [Fact]
    public async Task RefreshAsync_PreservesUserSelectedName()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });
        await _service.SetSeriesNameAsync("Batman", "My Batman");

        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "Dark Knight" },
                Source = "ComicVine"
            });

        var record = await _service.RefreshAsync("Batman");

        Assert.Equal("Batman", record.CanonicalTitle);
        Assert.Equal("My Batman", record.SeriesName);
        Assert.Equal("My Batman", record.ResolvedSeriesName);
        Assert.True(record.IsUserSelectedName);
        Assert.Contains("Dark Knight", record.Aliases);
    }

    [Fact]
    public async Task RefreshAsync_DownloadsImage_WhenProviderReturnsImageUrl()
    {
        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/batman.jpg"
            });
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(),
                "https://example.com/batman.jpg",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-abc.jpg", "image/jpeg", 1234));

        var record = await _service.RefreshAsync("Batman");

        Assert.Equal("downloaded", record.ImageStatus);
        Assert.Equal("batman-abc.jpg", record.LocalImageFile);
        Assert.Equal("image/jpeg", record.ImageContentType);
        Assert.True(record.HasImage);
        Assert.False(record.IsUserImage);
    }

    [Fact]
    public async Task RefreshAsync_RecordsFailedImage_WhenDownloadThrows()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/batman.jpg"
            });
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network down"));

        // Image-download failure must NOT fail the metadata refresh — the
        // record is still saved with a 'failed' image status so a future
        // refresh can retry.
        var record = await _service.RefreshAsync("Batman");

        Assert.Equal("success", record.LookupStatus);
        Assert.Equal("failed", record.ImageStatus);
        Assert.False(record.HasImage);
    }

    [Fact]
    public async Task RefreshAsync_DoesNotOverwriteUserUploadedImage()
    {
        // Seed a record with a user-uploaded image.
        await using (var content = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            _imageStore.Setup(s => s.SaveUserImageAsync(
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesImageStoreResult("user-abc.png", "image/png", 3));
            await _service.SetUserImageAsync("Batman", content, "image/png");
        }

        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/batman.jpg"
            });

        var record = await _service.RefreshAsync("Batman");

        // User image must remain sticky.
        Assert.Equal("user", record.ImageStatus);
        Assert.Equal("user-abc.png", record.LocalImageFile);
        _imageStore.Verify(s => s.DownloadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ClearImageAsync_RemovesCachedImage()
    {
        // Seed a downloaded image.
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/batman.jpg"
            });
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-abc.jpg", "image/jpeg", 100));
        var refreshed = await _service.RefreshAsync("Batman");
        Assert.True(refreshed.HasImage);

        var cleared = await _service.ClearImageAsync(refreshed.NormalizedKey);

        Assert.NotNull(cleared);
        Assert.Equal("none", cleared!.ImageStatus);
        Assert.Null(cleared.LocalImageFile);
        Assert.False(cleared.HasImage);
        _imageStore.Verify(s => s.Delete("batman-abc.jpg"), Times.Once);
    }

    [Fact]
    public async Task ApplyExternalImageAsync_CreatesRecordAndDownloads_WhenMissing()
    {
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(),
                "https://example.com/cover.png",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-xyz.png", "image/png", 4096));

        var record = await _service.ApplyExternalImageAsync(
            "Batman",
            "https://example.com/cover.png",
            source: "ComicVine");

        Assert.Equal("batman", record.NormalizedKey);
        Assert.Equal("downloaded", record.ImageStatus);
        Assert.Equal("batman-xyz.png", record.LocalImageFile);
        Assert.Equal("image/png", record.ImageContentType);
        Assert.Equal("https://example.com/cover.png", record.RemoteImageUrl);
        Assert.Equal("ComicVine", record.Source);
        Assert.True(record.HasImage);
        Assert.False(record.IsUserImage);
    }

    [Fact]
    public async Task ApplyExternalImageAsync_ReplacesPreviousDownloadedImage()
    {
        // Seed an existing downloaded image so we can verify the previous
        // filename is passed to DownloadAsync for cleanup.
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/old.jpg"
            });
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(), "https://example.com/old.jpg", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-old.jpg", "image/jpeg", 100));
        await _service.RefreshAsync("Batman");

        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(), "https://example.com/new.png", "batman-old.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-new.png", "image/png", 200));

        var record = await _service.ApplyExternalImageAsync(
            "Batman",
            "https://example.com/new.png",
            source: "MangaDex");

        Assert.Equal("batman-new.png", record.LocalImageFile);
        Assert.Equal("MangaDex", record.Source);
        _imageStore.Verify(s => s.DownloadAsync(
                It.IsAny<string>(), "https://example.com/new.png", "batman-old.jpg", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyExternalImageAsync_Throws_WhenCurrentImageIsUserUploaded()
    {
        // Seed a user-uploaded image.
        await using (var content = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            _imageStore.Setup(s => s.SaveUserImageAsync(
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesImageStoreResult("user-abc.png", "image/png", 3));
            await _service.SetUserImageAsync("Batman", content, "image/png");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ApplyExternalImageAsync("Batman", "https://example.com/cover.png", source: null));

        Assert.Contains("user-uploaded", ex.Message, StringComparison.OrdinalIgnoreCase);
        _imageStore.Verify(s => s.DownloadAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyExternalImageAsync_RejectsEmptyArguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyExternalImageAsync("", "https://example.com/x.png", null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyExternalImageAsync("Batman", "", null));
    }

    [Fact]
    public async Task ReapplyImageArtifactsAsync_ForcesCoverWriters_WhenCachedImageExists()
    {
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(),
                "https://example.com/cover.png",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman-xyz.png", "image/png", 4096));
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-xyz.png"))
            .Returns("/library/cache/batman-xyz.png");

        await _service.ApplyExternalImageAsync(
            "Batman",
            "https://example.com/cover.png",
            source: "ComicVine");

        var updated = await _service.ReapplyImageArtifactsAsync("Batman");

        Assert.True(updated);
        _folderCoverWriter.Verify(w => w.WriteAsync("batman", "/library/cache/batman-xyz.png", "image/png", true, It.IsAny<CancellationToken>()), Times.Once);
        _archiveCoverQueue.Verify(w => w.EnqueueWrite("batman", "/library/cache/batman-xyz.png", "image/png", true), Times.Once);
    }

    [Fact]
    public async Task ReapplyImageArtifactsAsync_ReturnsFalse_WhenSeriesHasNoCachedImage()
    {
        await _service.SetUserAliasesAsync("Batman", Array.Empty<string>());

        var updated = await _service.ReapplyImageArtifactsAsync("Batman");

        Assert.False(updated);
        _folderCoverWriter.Verify(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _archiveCoverQueue.Verify(w => w.EnqueueWrite(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ApplyExternalMatchAsync_UpsertsCandidateAndPreservesUserAliases()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });

        var record = await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            });

        Assert.Equal("Batman: Year One", record.CanonicalTitle);
        Assert.Equal("ComicVine", record.Source);
        Assert.Equal("manual_match", record.LookupStatus);
        Assert.NotNull(record.LastLookupUtc);
        Assert.Contains("Year One", record.Aliases);
        // User-managed aliases must survive a manual match override.
        Assert.Contains("Caped Crusader", record.UserAliases);
    }

    [Fact]
    public async Task ApplyExternalMatchAsync_PreservesUserSelectedName()
    {
        await _service.SetUserAliasesAsync("Batman", Array.Empty<string>());
        await _service.SetSeriesNameAsync("Batman", "My Batman");

        var record = await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman (1940)",
                Aliases = new List<string> { "Detective Comics" },
                Source = "ComicVine"
            });

        Assert.Equal("Batman (1940)", record.CanonicalTitle);
        Assert.Equal("My Batman", record.SeriesName);
        Assert.Equal("My Batman", record.ResolvedSeriesName);
        Assert.True(record.IsUserSelectedName);
        Assert.Contains("Detective Comics", record.Aliases);
    }

    [Fact]
    public async Task SetSeriesNameAsync_ResolvesByMatchedCanonicalTitle_AfterManualMatch()
    {
        // Reproduces the bug: a series matched to a different canonical title
        // stays keyed by its original folder title, but the UI now refers to
        // the series by its matched canonical title. Setting the name keyed by
        // the matched title must update the original record (not miss it).
        await _service.SetUserAliasesAsync("Naono Folder", Array.Empty<string>());
        await _service.ApplyExternalMatchAsync(
            "Naono Folder",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Berlin",
                Aliases = new List<string> { "ベルリン" },
                Source = "AniList"
            });

        // The original record key is derived from "Naono Folder", but the
        // caller now uses the matched canonical title "Berlin".
        var record = await _service.SetSeriesNameAsync("Berlin", "ベルリン");

        Assert.NotNull(record);
        Assert.Equal("naono-folder", record!.NormalizedKey);
        Assert.Equal("ベルリン", record.SeriesName);
        Assert.Equal("ベルリン", record.ResolvedSeriesName);

        // A subsequent change to a different name must also take effect.
        var changed = await _service.SetSeriesNameAsync("Berlin", "Berlin");
        Assert.NotNull(changed);
        Assert.Equal("naono-folder", changed!.NormalizedKey);
        Assert.Equal("Berlin", changed.SeriesName);
        Assert.Equal("Berlin", changed.ResolvedSeriesName);
    }

    [Fact]
    public async Task SetSeriesNameAsync_UpdatesAuthoritativeRecord_WhenSiblingsShareTitle()
    {
        // Reproduces the user-visible bug: a folder refresh issues a separate
        // lookup for the canonical title AND each alias, so several cache
        // records can end up describing the same logical series — typically one
        // authoritative "success" record and one or more stale "not_found"
        // siblings. The library renders the series using the most authoritative
        // record (highest lookup-status rank, newest lookup). When the
        // title-based resolver picked an arbitrary sibling, SetSeriesNameAsync
        // pinned the name onto the wrong record, so the page reported success
        // but the displayed name never changed. The resolver must therefore
        // update the SAME authoritative record the library displays.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            // Stale sibling, inserted first so a naive FirstOrDefault would win.
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "berlin-saga-stale",
                CanonicalTitle = "Berlin Saga",
                LookupStatus = "not_found",
                LastLookupUtc = DateTime.UtcNow.AddDays(-2),
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                UpdatedAt = DateTime.UtcNow.AddDays(-2)
            });
            // Authoritative record the library would display.
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "berlin-saga-tv",
                CanonicalTitle = "Berlin Saga",
                LookupStatus = "success",
                Source = "AniList",
                LastLookupUtc = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Neither record is keyed by NormalizeKey("Berlin Saga"), so resolution
        // falls back to scanning known titles where both records match.
        var record = await _service.SetSeriesNameAsync("Berlin Saga", "Berlin Saga DX");

        Assert.NotNull(record);
        Assert.Equal("berlin-saga-tv", record!.NormalizedKey);
        Assert.Equal("Berlin Saga DX", record.SeriesName);

        // The authoritative record carries the pinned name; the stale sibling
        // is left untouched so it cannot shadow the display title.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var authoritative = await db.SeriesMetadataCache.FindAsync("berlin-saga-tv");
            var stale = await db.SeriesMetadataCache.FindAsync("berlin-saga-stale");
            Assert.Equal("Berlin Saga DX", authoritative!.SeriesName);
            Assert.Null(stale!.SeriesName);
        }
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_RemovesSubsumedNotFoundSibling()
    {
        // The authoritative record owns the alias "Beruferu" (a not_found
        // sibling got keyed by that alias during a refresh sweep). The sibling
        // carries no unique data and is subsumed, so it should be removed.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "berlin-saga",
                CanonicalTitle = "Berlin Saga",
                Aliases = new List<string> { "Beruferu" },
                LookupStatus = "success",
                Source = "AniList",
                LastLookupUtc = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "beruferu",
                CanonicalTitle = "Beruferu",
                LookupStatus = "not_found",
                LastLookupUtc = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var removed = await _service.CleanupStaleSiblingRecordsAsync();

        Assert.Equal(1, removed);
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            Assert.NotNull(await db.SeriesMetadataCache.FindAsync("berlin-saga"));
            Assert.Null(await db.SeriesMetadataCache.FindAsync("beruferu"));
        }
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_PreservesRecordsWithUserData()
    {
        // A subsumed sibling that carries user data (pinned name, user alias,
        // preferred language, or a user image) must never be deleted.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "berlin-saga",
                CanonicalTitle = "Berlin Saga",
                Aliases = new List<string> { "Beruferu" },
                LookupStatus = "success",
                LastLookupUtc = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "beruferu",
                CanonicalTitle = "Beruferu",
                LookupStatus = "not_found",
                PreferredLanguage = "ja",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var removed = await _service.CleanupStaleSiblingRecordsAsync();

        Assert.Equal(0, removed);
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            Assert.NotNull(await db.SeriesMetadataCache.FindAsync("beruferu"));
        }
    }

    [Fact]
    public async Task CleanupStaleSiblingRecordsAsync_KeepsUnrelatedAndAuthoritativeRecords()
    {
        // Two unrelated series plus a positive-status record sharing no key
        // with a stronger record: nothing should be removed.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "naruto",
                CanonicalTitle = "Naruto",
                LookupStatus = "success",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = "bleach",
                CanonicalTitle = "Bleach",
                LookupStatus = "not_found",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var removed = await _service.CleanupStaleSiblingRecordsAsync();

        Assert.Equal(0, removed);
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.SeriesMetadataCache.CountAsync());
        }
    }

    [Fact]
    public async Task GetByTitleAsync_ResolvesByMatchedCanonicalTitle_AfterManualMatch()
    {
        await _service.SetUserAliasesAsync("Naono Folder", new[] { "MyAlias" });
        await _service.ApplyExternalMatchAsync(
            "Naono Folder",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Berlin",
                Aliases = new List<string> { "ベルリン" },
                Source = "AniList"
            });

        // Looked up by the matched canonical title (the UI's display title).
        var byCanonical = await _service.GetByTitleAsync("Berlin");
        Assert.NotNull(byCanonical);
        Assert.Equal("naono-folder", byCanonical!.NormalizedKey);
        Assert.Contains("MyAlias", byCanonical.UserAliases);

        // Looked up by a provider alias.
        var byAlias = await _service.GetByTitleAsync("ベルリン");
        Assert.NotNull(byAlias);
        Assert.Equal("naono-folder", byAlias!.NormalizedKey);

        // Exact-key lookup still works for the original title.
        var byOriginal = await _service.GetByTitleAsync("Naono Folder");
        Assert.NotNull(byOriginal);
        Assert.Equal("naono-folder", byOriginal!.NormalizedKey);
    }

    [Fact]
    public async Task GetByTitleAsync_ReturnsNull_WhenNoRecordMatches()
    {
        await _service.SetUserAliasesAsync("Batman", Array.Empty<string>());

        Assert.Null(await _service.GetByTitleAsync("Totally Unrelated Series"));
    }

    [Fact]
    public async Task ApplyExternalMatchAsync_RejectsEmptyCandidate()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyExternalMatchAsync("Batman", new ExternalSeriesMetadata()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyExternalMatchAsync("", new ExternalSeriesMetadata { CanonicalTitle = "X" }));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.ApplyExternalMatchAsync("Batman", null!));
    }

    [Fact]
    public async Task PersistExternalLookupAsync_UpsertsAsSuccessAndPreservesUserAliases()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });

        var record = await _service.PersistExternalLookupAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "Bruce Wayne" },
                Source = "ComicVine"
            });

        // Auto-persisted external lookups are recorded as "success" so they
        // can be distinguished from user-driven manual matches, while still
        // satisfying the matched-cache pass on subsequent resolves.
        Assert.Equal("success", record.LookupStatus);
        Assert.Equal("Batman", record.CanonicalTitle);
        Assert.Equal("ComicVine", record.Source);
        Assert.NotNull(record.LastLookupUtc);
        Assert.Contains("Bruce Wayne", record.Aliases);
        Assert.Contains("Caped Crusader", record.UserAliases);
    }

    [Fact]
    public async Task PersistExternalLookupAsync_PreservesUserSelectedName()
    {
        await _service.SetUserAliasesAsync("Batman", Array.Empty<string>());
        await _service.SetSeriesNameAsync("Batman", "My Batman");

        var record = await _service.PersistExternalLookupAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman (1940)",
                Source = "ComicVine"
            });

        Assert.Equal("Batman (1940)", record.CanonicalTitle);
        Assert.Equal("My Batman", record.SeriesName);
        Assert.Equal("My Batman", record.ResolvedSeriesName);
        Assert.True(record.IsUserSelectedName);
        Assert.Equal("success", record.LookupStatus);
    }

    [Fact]
    public async Task PersistExternalLookupAsync_RejectsInvalidInput()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.PersistExternalLookupAsync("Batman", new ExternalSeriesMetadata()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.PersistExternalLookupAsync("", new ExternalSeriesMetadata { CanonicalTitle = "X" }));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.PersistExternalLookupAsync("Batman", null!));
    }

    [Fact]
    public async Task RefreshAsync_AfterManualMatch_LooksUpByManualCanonicalAndPreservesStatus()
    {
        // User manually matched "Batman" to a specific candidate ("Batman: Year One").
        await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            });

        // Subsequent refresh must re-resolve using the manually-selected
        // canonical title (not the original input), and must keep the series
        // marked as manually matched.
        _external.Setup(e => e.LookupSeriesAsync("Batman: Year One", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One", "Frank Miller" },
                Source = "ComicVine"
            });

        var refreshed = await _service.RefreshAsync("Batman");

        Assert.Equal("manual_match", refreshed.LookupStatus);
        Assert.Equal("Batman: Year One", refreshed.CanonicalTitle);
        Assert.Equal("ComicVine", refreshed.Source);
        Assert.Contains("Frank Miller", refreshed.Aliases);
        _external.Verify(e => e.LookupSeriesAsync("Batman: Year One", It.IsAny<CancellationToken>()), Times.Once);
        _external.Verify(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_AfterManualMatch_PreservesMatchOnLookupFailure()
    {
        await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            });

        // Provider can't find the match on this refresh — must not clobber
        // the user's manual selection.
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalSeriesMetadata?)null);

        var refreshed = await _service.RefreshAsync("Batman");

        Assert.Equal("manual_match", refreshed.LookupStatus);
        Assert.Equal("Batman: Year One", refreshed.CanonicalTitle);
        Assert.Equal("ComicVine", refreshed.Source);
        Assert.Contains("Year One", refreshed.Aliases);
    }

    [Fact]
    public async Task RefreshAsync_AfterManualMatch_PreservesMatchOnLookupError()
    {
        await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman: Year One",
                Aliases = new List<string> { "Year One" },
                Source = "ComicVine"
            });

        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var refreshed = await _service.RefreshAsync("Batman");

        Assert.Equal("manual_match", refreshed.LookupStatus);
        Assert.Equal("Batman: Year One", refreshed.CanonicalTitle);
        Assert.Equal("ComicVine", refreshed.Source);
    }

    [Fact]
    public async Task ClearExternalMetadataAsync_DropsProviderFieldsAndKeepsUserAliases()
    {
        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "Dark Knight" },
                Source = "ComicVine"
            });
        await _service.RefreshAsync("Batman");
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });

        var cleared = await _service.ClearExternalMetadataAsync("batman");

        Assert.NotNull(cleared);
        Assert.Empty(cleared!.Aliases);
        Assert.Null(cleared.Source);
        Assert.Null(cleared.LastLookupUtc);
        Assert.Equal("cleared", cleared.LookupStatus);
        // Provider-supplied canonical should revert to the normalized key
        // placeholder when there is no user override.
        Assert.Equal("batman", cleared.CanonicalTitle);
        // User-managed aliases must survive the clear.
        Assert.Contains("Caped Crusader", cleared.UserAliases);
    }

    [Fact]
    public async Task ClearExternalMetadataAsync_PreservesUserSelectedNameAndUserImage()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" });
        await _service.SetSeriesNameAsync("Batman", "My Batman");

        // Simulate a user-uploaded image already attached to the record.
        await using (var db = await _dbContextFactory.CreateDbContextAsync())
        {
            var entity = db.SeriesMetadataCache.Single(e => e.NormalizedKey == "batman");
            entity.LocalImageFile = "batman-user.jpg";
            entity.ImageContentType = "image/jpeg";
            entity.ImageStatus = "user";
            entity.Source = "ComicVine";
            entity.Aliases = new List<string> { "Dark Knight" };
            await db.SaveChangesAsync();
        }

        var cleared = await _service.ClearExternalMetadataAsync("batman");

        Assert.NotNull(cleared);
        Assert.Equal("batman", cleared!.CanonicalTitle);
        Assert.Equal("My Batman", cleared.SeriesName);
        Assert.Equal("My Batman", cleared.ResolvedSeriesName);
        Assert.True(cleared.IsUserSelectedName);
        // User image must be sticky across a metadata clear.
        Assert.Equal("user", cleared.ImageStatus);
        Assert.Equal("batman-user.jpg", cleared.LocalImageFile);
        _imageStore.Verify(s => s.Delete(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ClearExternalMetadataAsync_DropsProviderDownloadedImage()
    {
        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string>(),
                Source = "ComicVine",
                ImageUrl = "https://example.com/b.jpg"
            });
        _imageStore.Setup(s => s.DownloadAsync(
                It.IsAny<string>(),
                "https://example.com/b.jpg",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesImageStoreResult("batman.jpg", "image/jpeg", 100));
        await _service.RefreshAsync("Batman");

        var cleared = await _service.ClearExternalMetadataAsync("batman");

        Assert.NotNull(cleared);
        Assert.Equal("none", cleared!.ImageStatus);
        Assert.Null(cleared.LocalImageFile);
        _imageStore.Verify(s => s.Delete("batman.jpg"), Times.Once);
    }

    [Fact]
    public async Task ClearExternalMetadataAsync_ReturnsNullWhenRecordMissing()
    {
        var result = await _service.ClearExternalMetadataAsync("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshAsync_PersistsLocalizedTitles_FromProvider()
    {
        _external.Setup(e => e.LookupSeriesAsync("One Piece", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "One Piece",
                Aliases = new List<string> { "ワンピース" },
                Source = "AniListManga",
                LocalizedTitles = new List<LocalizedTitle>
                {
                    new LocalizedTitle("One Piece", "en"),
                    new LocalizedTitle("ワンピース", "ja")
                }
            });

        var record = await _service.RefreshAsync("One Piece");

        Assert.Equal(2, record.LocalizedTitles.Count);
        Assert.Equal("en", record.LocalizedTitles[0].Language);
        Assert.Equal("ja", record.LocalizedTitles[1].Language);
        Assert.Equal("ワンピース", record.LocalizedTitles[1].Title);
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_UpsertsRecord_WithNormalizedLanguage()
    {
        var record = await _service.SetPreferredLanguageAsync("Berserk", "JA");

        Assert.Equal("berserk", record.NormalizedKey);
        Assert.Equal("ja", record.PreferredLanguage);
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_ClearsPreference_WhenLanguageIsNullOrEmpty()
    {
        await _service.SetPreferredLanguageAsync("Berserk", "ja");
        var cleared = await _service.SetPreferredLanguageAsync("Berserk", null);

        Assert.Null(cleared.PreferredLanguage);
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_ThrowsArgumentException_ForUnknownLanguage()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetPreferredLanguageAsync("Berserk", "fr"));
    }

    [Fact]
    public async Task SetPreferredLanguageAsync_PreservesPreferenceAcrossRefresh()
    {
        await _service.SetPreferredLanguageAsync("Bleach", "ja");

        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Bleach",
                Aliases = new List<string>(),
                Source = "AniListManga",
                LocalizedTitles = new List<LocalizedTitle>
                {
                    new LocalizedTitle("Bleach", "en"),
                    new LocalizedTitle("ブリーチ", "ja")
                }
            });

        var refreshed = await _service.RefreshAsync("Bleach");

        Assert.Equal("ja", refreshed.PreferredLanguage);
        Assert.NotEmpty(refreshed.LocalizedTitles);
    }

    [Fact]
    public async Task SetSeriesNameAsync_CreatesRecord_WhenNoCacheRecordExists()
    {
        // A series with no cache record (e.g. grouped purely from files on
        // disk) used to be unfixable because SetSeriesNameAsync returned null.
        // Pinning a non-empty name now creates a minimal manual record so the
        // series can be fixed and its on-disk <Series> retagged.
        var result = await _service.SetSeriesNameAsync("Unknown Series", "Fixed Name");

        Assert.NotNull(result);
        Assert.Equal("Unknown Series", result!.CanonicalTitle);
        Assert.Equal("Fixed Name", result.SeriesName);
        Assert.True(result.IsUserSelectedName);
        Assert.Contains("Fixed Name", result.UserAliases);

        // The new record is resolvable by both the original title and the
        // pinned name.
        Assert.NotNull(await _service.GetByTitleAsync("Unknown Series"));
        Assert.NotNull(await _service.GetByTitleAsync("Fixed Name"));
    }

    [Fact]
    public async Task SetSeriesNameAsync_ReturnsNull_WhenRevertingAutomaticWithNoRecord()
    {
        // Reverting to automatic (empty name) for a series that has no record
        // is a no-op: there is nothing pinned to clear.
        var result = await _service.SetSeriesNameAsync("Unknown Series", "");
        Assert.Null(result);
    }

    [Fact]
    public async Task SetSeriesNameAsync_AcceptsCanonicalTitle()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Naruto",
                LocalizedTitles = new List<LocalizedTitle>
                {
                    new("Naruto", "en"),
                    new("ナルト", "ja")
                }
            });
        await _service.RefreshAsync("Naruto");

        var record = await _service.SetSeriesNameAsync("Naruto", "Naruto");
        Assert.NotNull(record);
        Assert.Equal("Naruto", record!.SeriesName);
        Assert.True(record.IsUserSelectedName);
    }

    [Fact]
    public async Task SetSeriesNameAsync_AcceptsLocalizedTitle_CaseInsensitively()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "One Piece",
                LocalizedTitles = new List<LocalizedTitle>
                {
                    new("One Piece", "en"),
                    new("ワンピース", "ja"),
                    new("Wan Piisu", "ja-Latn")
                }
            });
        await _service.RefreshAsync("One Piece");

        // Select using a different case + whitespace; the persisted value
        // should be the canonical record-side casing.
        var record = await _service.SetSeriesNameAsync("One Piece", "  wan piisu  ");
        Assert.NotNull(record);
        Assert.Equal("Wan Piisu", record!.SeriesName);
    }

    [Fact]
    public async Task SetSeriesNameAsync_AddsUnknownNameAsUserAlias()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Bleach",
                LocalizedTitles = new List<LocalizedTitle> { new("Bleach", "en"), new("ブリーチ", "ja") }
            });
        await _service.RefreshAsync("Bleach");

        var record = await _service.SetSeriesNameAsync("Bleach", "Not A Title");

        Assert.NotNull(record);
        Assert.Equal("Not A Title", record!.SeriesName);
        Assert.Contains("Not A Title", record.UserAliases);
    }

    [Fact]
    public async Task SetSeriesNameAsync_ClearsName_WhenNullOrEmpty()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Bleach",
                LocalizedTitles = new List<LocalizedTitle> { new("Bleach", "en") }
            });
        await _service.RefreshAsync("Bleach");
        await _service.SetSeriesNameAsync("Bleach", "Bleach");

        var cleared = await _service.SetSeriesNameAsync("Bleach", null);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.SeriesName);
        Assert.False(cleared.IsUserSelectedName);
    }

    [Fact]
    public async Task SetSeriesNameAsync_BumpsMetadataVersion()
    {
        _external.Setup(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Berserk",
                LocalizedTitles = new List<LocalizedTitle> { new("Berserk", "en"), new("ベルセルク", "ja") }
            });
        var initial = await _service.RefreshAsync("Berserk");
        var initialVersion = initial.MetadataVersion;

        var selected = await _service.SetSeriesNameAsync("Berserk", "ベルセルク");
        Assert.NotNull(selected);
        Assert.True(selected!.MetadataVersion > initialVersion,
            $"Expected MetadataVersion to be bumped (was {initialVersion}, now {selected.MetadataVersion}).");
    }
}
