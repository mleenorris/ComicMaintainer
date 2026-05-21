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
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings = new() { WatcherEnableNormalize = true };

    private SeriesLanguagePreferenceRetagService BuildService()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        return new SeriesLanguagePreferenceRetagService(
            _fileStore.Object,
            _processor.Object,
            _cache.Object,
            _options.Object,
            new Mock<ILogger<SeriesLanguagePreferenceRetagService>>().Object);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_QueuesEveryFileMatchingTitleOrAliasOrLocalized()
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

        var jobId = Guid.NewGuid();
        List<string>? capturedFiles = null;
        _processor.Setup(p => p.NormalizeFilesAsync(
                It.IsAny<IEnumerable<string>>(),
                true,
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, _, _) => capturedFiles = f.ToList())
            .ReturnsAsync(jobId);

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Equal(jobId, result);
        Assert.NotNull(capturedFiles);
        Assert.Equal(3, capturedFiles!.Count);
        Assert.DoesNotContain("/d/Other - 1.cbz", capturedFiles);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_NoMatchingFiles_ReturnsNull()
    {
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "One Piece" };
        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ComicFile>());

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Null(result);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_NormalizeDisabled_ReturnsNull()
    {
        _settings.WatcherEnableNormalize = false;
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "One Piece" };

        var service = BuildService();
        var result = await service.QueueRetagForSeriesAsync(record);

        Assert.Null(result);
        _fileStore.Verify(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.NormalizeFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var jobId = Guid.NewGuid();
        List<string>? captured = null;
        _processor.Setup(p => p.NormalizeFilesAsync(
                It.IsAny<IEnumerable<string>>(),
                true,
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((f, _, _) => captured = f.ToList())
            .ReturnsAsync(jobId);

        var service = BuildService();
        var result = await service.QueueRetagForGlobalDefaultAsync();

        Assert.Equal(jobId, result);
        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("/a/g.cbz", captured![0]);
    }
}
