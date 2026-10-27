using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="SeriesLanguagePreferenceRetagService"/>: records without usable
/// titles, the global-default early exits and the folder-name fallback edge cases.
/// </summary>
public class SeriesLanguagePreferenceRetagServiceCoverageTests
{
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly AppSettings _settings = new() { WatcherEnableNormalize = true };
    private readonly List<ComicFile> _files = new();
    private List<string>? _flagged;

    public SeriesLanguagePreferenceRetagServiceCoverageTests()
    {
        _fileStore.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.ToList());
        _fileStore.Setup(s => s.MarkFilesNeedingBackfillAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((paths, _) => _flagged = paths.ToList())
            .ReturnsAsync((IEnumerable<string> paths, CancellationToken _) => paths.Count());
        // Mirrors the production key folding closely enough for folder matching: letters/digits only, lower-cased.
        _cache.Setup(c => c.NormalizeKey(It.IsAny<string?>()))
            .Returns<string?>(s => new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant());
    }

    private SeriesLanguagePreferenceRetagService CreateService() => new(
        _fileStore.Object,
        _cache.Object,
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<SeriesLanguagePreferenceRetagService>.Instance);

    private void VerifyFilesNeverLoaded() =>
        _fileStore.Verify(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task QueueRetagForSeriesAsync_NullRecord_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateService().QueueRetagForSeriesAsync(null!));
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_RecordWithoutAnyUsableTitle_ReturnsZeroWithoutScanningFiles()
    {
        var record = new SeriesMetadataCacheRecord
        {
            CanonicalTitle = "   ",
            Aliases = new List<string> { " " },
            UserAliases = new List<string> { "" },
            LocalizedTitles = new List<LocalizedTitle> { null!, new("  ", "ja") },
        };
        _files.Add(new ComicFile { FilePath = "/library/Any/c001.cbz", Metadata = new ComicMetadata { Series = "Any" } });

        var result = await CreateService().QueueRetagForSeriesAsync(record);

        Assert.Equal(0, result);
        VerifyFilesNeverLoaded();
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_BlankCanonicalTitle_StillMatchesAliasesAndLocalizedTitles()
    {
        var record = new SeriesMetadataCacheRecord
        {
            CanonicalTitle = "",
            Aliases = new List<string> { "Alias Title" },
            LocalizedTitles = new List<LocalizedTitle> { null!, new("ローカライズ", "ja") },
        };
        _files.Add(new ComicFile { FilePath = "/library/x/1.cbz", Metadata = new ComicMetadata { Series = "alias title" } });
        _files.Add(new ComicFile { FilePath = "/library/y/2.cbz", Metadata = new ComicMetadata { Series = "ローカライズ" } });
        _files.Add(new ComicFile { FilePath = "/library/z/3.cbz", Metadata = new ComicMetadata { Series = "Unrelated" } });

        var result = await CreateService().QueueRetagForSeriesAsync(record);

        Assert.Equal(2, result);
        Assert.Equal(new[] { "/library/x/1.cbz", "/library/y/2.cbz" }, _flagged);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_FolderFallback_IgnoresRootFilesBlankPathsAndUnkeyableFolders()
    {
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "Re:Zero" };
        _files.Add(new ComicFile { FilePath = "/library/Re_Zero/c001.cbz" });
        _files.Add(new ComicFile { FilePath = "/c002.cbz" });
        _files.Add(new ComicFile { FilePath = "c003.cbz" });
        _files.Add(new ComicFile { FilePath = "/library/!!!/c004.cbz" });
        _files.Add(new ComicFile { FilePath = "   ", Metadata = new ComicMetadata { Series = "Re:Zero" } });
        _files.Add(new ComicFile { FilePath = "/library/Other/c005.cbz", Metadata = new ComicMetadata { Series = "  " } });

        var result = await CreateService().QueueRetagForSeriesAsync(record);

        Assert.Equal(1, result);
        Assert.Equal(new[] { "/library/Re_Zero/c001.cbz" }, _flagged);
    }

    [Fact]
    public async Task QueueRetagForSeriesAsync_ReturnsTheCountReportedByTheStore()
    {
        var record = new SeriesMetadataCacheRecord { CanonicalTitle = "Alpha" };
        _files.Add(new ComicFile { FilePath = "/library/Alpha/1.cbz" });
        _files.Add(new ComicFile { FilePath = "/library/Alpha/2.cbz" });
        _fileStore.Setup(s => s.MarkFilesNeedingBackfillAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await CreateService().QueueRetagForSeriesAsync(record);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task QueueRetagForGlobalDefaultAsync_NormalizeDisabled_ReturnsZeroWithoutReadingTheCache()
    {
        _settings.WatcherEnableNormalize = false;

        var result = await CreateService().QueueRetagForGlobalDefaultAsync();

        Assert.Equal(0, result);
        _cache.Verify(c => c.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        VerifyFilesNeverLoaded();
    }

    [Fact]
    public async Task QueueRetagForGlobalDefaultAsync_NoSeriesFollowsTheGlobalDefault_ReturnsZeroWithoutScanningFiles()
    {
        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new() { CanonicalTitle = "Alpha", PreferredLanguage = "ja" },
                new() { CanonicalTitle = "Beta", PreferredLanguage = "ko" },
                new() { CanonicalTitle = "  ", PreferredLanguage = null },
            });
        _files.Add(new ComicFile { FilePath = "/library/Alpha/1.cbz", Metadata = new ComicMetadata { Series = "Alpha" } });

        var result = await CreateService().QueueRetagForGlobalDefaultAsync();

        Assert.Equal(0, result);
        VerifyFilesNeverLoaded();
    }

    [Fact]
    public async Task QueueRetagForGlobalDefaultAsync_EmptyCache_ReturnsZero()
    {
        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>());

        var result = await CreateService().QueueRetagForGlobalDefaultAsync();

        Assert.Equal(0, result);
        VerifyFilesNeverLoaded();
    }

    [Fact]
    public async Task QueueRetagForGlobalDefaultAsync_MergesTitlesOfEverySeriesWithoutAPreference()
    {
        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesMetadataCacheRecord>
            {
                new() { CanonicalTitle = "Alpha", Aliases = new List<string> { "ALPHA" } },
                new() { CanonicalTitle = "Beta", PreferredLanguage = "   " },
                new() { CanonicalTitle = "Gamma", PreferredLanguage = "ja" },
            });
        _files.Add(new ComicFile { FilePath = "/library/a/1.cbz", Metadata = new ComicMetadata { Series = "alpha" } });
        _files.Add(new ComicFile { FilePath = "/library/Beta/2.cbz" });
        _files.Add(new ComicFile { FilePath = "/library/g/3.cbz", Metadata = new ComicMetadata { Series = "Gamma" } });

        var result = await CreateService().QueueRetagForGlobalDefaultAsync();

        Assert.Equal(2, result);
        Assert.Equal(new[] { "/library/a/1.cbz", "/library/Beta/2.cbz" }, _flagged);
    }
}
