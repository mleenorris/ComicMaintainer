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
    public async Task SetUserAliasesAsync_CreatesRecordAndDedupes()
    {
        var record = await _service.SetUserAliasesAsync(
            "Series A",
            new[] { "Alias One", "alias one", "Alias Two", "" },
            canonicalTitleOverride: null);

        Assert.Equal("series-a", record.NormalizedKey);
        Assert.Equal("Series A", record.CanonicalTitle);
        Assert.Equal(2, record.UserAliases.Count);
        Assert.Contains("Alias One", record.UserAliases);
        Assert.Contains("Alias Two", record.UserAliases);
        Assert.False(record.IsUserCanonical);
    }

    [Fact]
    public async Task SetUserAliasesAsync_AppliesCanonicalOverride()
    {
        var record = await _service.SetUserAliasesAsync(
            "Series A",
            new[] { "Alias" },
            canonicalTitleOverride: "Canonical A");

        Assert.Equal("Canonical A", record.CanonicalTitle);
        Assert.True(record.IsUserCanonical);
    }

    [Fact]
    public async Task RemoveUserAliasAsync_RemovesCaseInsensitive()
    {
        await _service.SetUserAliasesAsync("Series A", new[] { "Alias One", "Alias Two" }, null);
        var updated = await _service.RemoveUserAliasAsync("series-a", "alias one");
        Assert.NotNull(updated);
        Assert.Single(updated!.UserAliases);
        Assert.Contains("Alias Two", updated.UserAliases);
    }

    [Fact]
    public async Task RefreshAsync_UpsertsProviderResultAndPreservesUserAliases()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" }, null);

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
    public async Task RefreshAsync_PreservesUserCanonicalOverride()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" }, canonicalTitleOverride: "My Batman");

        _external.Setup(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman",
                Aliases = new List<string> { "Dark Knight" },
                Source = "ComicVine"
            });

        var record = await _service.RefreshAsync("Batman");

        // User-overridden canonical title must NOT be overwritten by the provider.
        Assert.Equal("My Batman", record.CanonicalTitle);
        Assert.True(record.IsUserCanonical);
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
    public async Task ApplyExternalMatchAsync_UpsertsCandidateAndPreservesUserAliases()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" }, null);

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
    public async Task ApplyExternalMatchAsync_PreservesUserCanonicalOverride()
    {
        await _service.SetUserAliasesAsync("Batman", Array.Empty<string>(), canonicalTitleOverride: "My Batman");

        var record = await _service.ApplyExternalMatchAsync(
            "Batman",
            new ExternalSeriesMetadata
            {
                CanonicalTitle = "Batman (1940)",
                Aliases = new List<string> { "Detective Comics" },
                Source = "ComicVine"
            });

        Assert.Equal("My Batman", record.CanonicalTitle);
        Assert.True(record.IsUserCanonical);
        Assert.Contains("Detective Comics", record.Aliases);
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
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" }, null);

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
    public async Task ClearExternalMetadataAsync_PreservesUserCanonicalAndUserImage()
    {
        await _service.SetUserAliasesAsync("Batman", new[] { "Caped Crusader" }, canonicalTitleOverride: "My Batman");

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
        Assert.Equal("My Batman", cleared!.CanonicalTitle);
        Assert.True(cleared.IsUserCanonical);
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
}
