using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesLanguageAuditJobHandlerTests
{
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesLanguagePreferenceRetagService> _retag = new();
    private readonly Mock<IOptionsMonitor<AppSettings>> _options = new();
    private readonly AppSettings _settings = new();

    private SeriesLanguageAuditJobHandler BuildHandler()
    {
        _options.Setup(o => o.CurrentValue).Returns(_settings);
        return new SeriesLanguageAuditJobHandler(
            _cache.Object,
            _fileStore.Object,
            _retag.Object,
            _options.Object,
            new Mock<ILogger<SeriesLanguageAuditJobHandler>>().Object);
    }

    private static SeriesMetadataCacheRecord BuildRecord(
        string canonical,
        string? preferred,
        params (string title, string lang)[] localized)
    {
        return new SeriesMetadataCacheRecord
        {
            NormalizedKey = canonical.ToLowerInvariant(),
            CanonicalTitle = canonical,
            PreferredLanguage = preferred,
            LocalizedTitles = localized.Select(t => new LocalizedTitle(t.title, t.lang)).ToList(),
        };
    }

    [Fact]
    public async Task ExecuteAsync_DetectOnly_ReportsMismatchesWithoutQueuingRetag()
    {
        // Series with preferred language "ja" — expected display title is the
        // Japanese localized title, but the on-disk file is still tagged with
        // the English canonical title.
        var record = BuildRecord("One Piece", preferred: "ja",
            ("One Piece", "en"), ("ワンピース", "ja"));

        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/1.cbz", Metadata = new ComicMetadata { Series = "One Piece" } },
                new ComicFile { FilePath = "/a/2.cbz", Metadata = new ComicMetadata { Series = "One Piece" } },
            });

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync("""{"autoCorrect":false}""", default);

        Assert.Contains("1 with language mismatch", summary);
        Assert.Contains("2 file(s)", summary);
        _retag.Verify(
            r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AutoCorrect_QueuesRetagForMismatchedSeries()
    {
        var record = BuildRecord("One Piece", preferred: "ja",
            ("One Piece", "en"), ("ワンピース", "ja"));

        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/1.cbz", Metadata = new ComicMetadata { Series = "One Piece" } },
            });
        _retag.Setup(r => r.QueueRetagForSeriesAsync(record, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync("""{"autoCorrect":true}""", default);

        Assert.Contains("queued 1 retag job(s)", summary);
        _retag.Verify(
            r => r.QueueRetagForSeriesAsync(record, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoMismatch_DoesNothing()
    {
        // File <Series> already matches the expected localized title for
        // the preferred language — nothing to flag or correct.
        var record = BuildRecord("One Piece", preferred: "ja",
            ("One Piece", "en"), ("ワンピース", "ja"));

        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/1.cbz", Metadata = new ComicMetadata { Series = "ワンピース" } },
            });

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync("""{"autoCorrect":true}""", default);

        Assert.Contains("0 with language mismatch", summary);
        _retag.Verify(
            r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UsesGlobalDefaultWhenPerSeriesPreferenceIsNull()
    {
        _settings.DefaultPreferredLanguage = "ja";
        var record = BuildRecord("One Piece", preferred: null,
            ("One Piece", "en"), ("ワンピース", "ja"));

        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/1.cbz", Metadata = new ComicMetadata { Series = "One Piece" } },
            });

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync("""{"autoCorrect":false}""", default);

        Assert.Contains("1 with language mismatch", summary);
    }

    [Fact]
    public async Task ExecuteAsync_UserCanonicalOverrideIsRespected()
    {
        // IsUserCanonical=true means the canonical title wins regardless of
        // language preference — files tagged with the canonical title must
        // therefore NOT be flagged as mismatched.
        var record = BuildRecord("My Custom Title", preferred: "ja",
            ("One Piece", "en"), ("ワンピース", "ja"));
        record.IsUserCanonical = true;

        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ComicFile { FilePath = "/a/1.cbz", Metadata = new ComicMetadata { Series = "My Custom Title" } },
            });

        var handler = BuildHandler();
        var summary = await handler.ExecuteAsync("""{"autoCorrect":true}""", default);

        Assert.Contains("0 with language mismatch", summary);
        _retag.Verify(
            r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Defaults_AreDisabledDailyWithAutoCorrectFalse()
    {
        var handler = BuildHandler();
        Assert.Equal("series-language-audit", handler.JobKey);
        Assert.False(handler.Defaults.Enabled);
        Assert.Equal(60 * 24, handler.Defaults.IntervalMinutes);
        Assert.Contains("autoCorrect", handler.Defaults.OptionsJson);
    }
}
