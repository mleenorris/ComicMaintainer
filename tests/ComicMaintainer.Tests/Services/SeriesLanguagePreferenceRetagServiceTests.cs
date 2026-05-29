using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLanguagePreferenceRetagServiceTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings = new() { WatcherEnableNormalize = true };

    private SeriesLanguagePreferenceRetagService BuildService()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        return new SeriesLanguagePreferenceRetagService(
            _fileStore.Object,
            _cache.Object,
            _options.Object,
            new Mock<ILogger<SeriesLanguagePreferenceRetagService>>().Object);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_FlagsEveryFileMatchingTitleOrAliasOrLocalized()
    {
        var record = new SeriesMetadataCacheRecord
        {
            CanonicalTitle = "One Piece",
            Aliases = new List<string> { "ワンピース" },
            UserAliases = new List<string> { "Dark Knight" },
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("One Piece", "en"),
                new("ワンピース", "ja"),
            }
        };

        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/One Piece - Chapter 1.cbz", Metadata = new ComicMetadata { Series = "One Piece" } },
                new ComicFile { FilePath = "/b/Wan - Chapter 1.cbz", Metadata = new ComicMetadata { Series = "ワンピース" } },
                new ComicFile { FilePath = "/c/Dark Knight - 1.cbz", Metadata = new ComicMetadata { Series = "Dark Knight" } },
                new ComicFile { FilePath = "/d/Other - 1.cbz", Metadata = new ComicMetadata { Series = "Naruto" } },
            });

        List<string>? capturedFiles = null;
        _fileStore.Setup(s => s.MarkFilesNeedingBackfillAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, _) => capturedFiles = f.ToList())
            .ReturnsAsync((IEnumerable<string> f, CancellationToken _) => f.Count());

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Equal(3, result);
        Assert.NotNull(capturedFiles);
        Assert.Equal(3, capturedFiles!.Count);
        Assert.DoesNotContain("/d/Other - 1.cbz", capturedFiles);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_NoMatchingFiles_ReturnsZero()
    {
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "One Piece" };
        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ComicFile>());

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Equal(0, result);
        _fileStore.Verify(s => s.MarkFilesNeedingBackfillAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_NormalizeDisabled_ReturnsZero()
    {
        _settings.WatcherEnableNormalize = false;
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "One Piece" };

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Equal(0, result);
        _fileStore.Verify(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(s => s.MarkFilesNeedingBackfillAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueRetagForGlobalDefaultAsync_OnlyIncludesSeriesWithoutPerSeriesPreference()
    {
        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new() { CanonicalTitle = "GlobalSeries", PreferredLanguage = null },
                new() { CanonicalTitle = "OverriddenSeries", PreferredLanguage = "ja" },
            });

        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/g.cbz", Metadata = new ComicMetadata { Series = "GlobalSeries" } },
                new ComicFile { FilePath = "/b/o.cbz", Metadata = new ComicMetadata { Series = "OverriddenSeries" } },
            });

        List<string>? captured = null;
        _fileStore.Setup(s => s.MarkFilesNeedingBackfillAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, _) => captured = f.ToList())
            .ReturnsAsync((IEnumerable<string> f, CancellationToken _) => f.Count());

        var service = BuildService();
        var result = await service.QueueRetagForGlobalDefaultAsync();

        Assert.Equal(1, result);
        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("/a/g.cbz", captured![0]);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_MatchesByFolderNameWhenMetadataSeriesNotPopulated()
    {
        // Regression test for the case where ComicFile.Metadata is null
        // (which is the production reality — that field is never populated).
        // The retag scan must still pick up files whose parent folder name
        // matches one of the record's titles via the cache's normalized key,
        // otherwise a per-series preferred-language change silently flags
        // zero work.
        var record = new SeriesMetadataCacheRecord
        {
            CanonicalTitle = "One Piece",
            LocalizedTitles = new List<LocalizedTitle>
            {
                new("One Piece", "en"),
                new("ワンピース", "ja"),
            }
        };

        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                // Metadata is null — production reality. Folder name matches
                // the canonical title.
                new ComicFile { FilePath = "/library/One Piece/One Piece - Chapter 1.cbz" },
                // Folder name matches a localized title (alias-by-folder).
                new ComicFile { FilePath = "/library/ワンピース/ワンピース - Chapter 1.cbz" },
                // Different folder — must not be flagged.
                new ComicFile { FilePath = "/library/Naruto/Naruto - Chapter 1.cbz" },
            });

        _cache.Setup(c => c.NormalizeKey(It.IsAny<string>()))
            .Returns<string>(s => (s ?? string.Empty).Trim().ToLowerInvariant());

        List<string>? capturedFiles = null;
        _fileStore.Setup(s => s.MarkFilesNeedingBackfillAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((f, _) => capturedFiles = f.ToList())
            .ReturnsAsync((IEnumerable<string> f, CancellationToken _) => f.Count());

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Equal(2, result);
        Assert.NotNull(capturedFiles);
        Assert.Equal(2, capturedFiles!.Count);
        Assert.Contains("/library/One Piece/One Piece - Chapter 1.cbz", capturedFiles);
        Assert.Contains("/library/ワンピース/ワンピース - Chapter 1.cbz", capturedFiles);
        Assert.DoesNotContain("/library/Naruto/Naruto - Chapter 1.cbz", capturedFiles);
    }
}
