using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Additional coverage for <see cref="SeriesLanguageAuditJobHandler"/>: alias matching, the resolver
/// path, defensive handling of sparse cache rows, auto-correct accounting, options and cancellation.
/// </summary>
public class SeriesLanguageAuditJobHandlerCoverageTests
{
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<ISeriesLanguagePreferenceRetagService> _retag = new();
    private readonly AppSettings _settings = new();
    private readonly RecordingLogger _logger = new();
    private readonly List<SeriesMetadataCacheRecord> _records = new();
    private readonly List<ComicFile> _files = new();

    public SeriesLanguageAuditJobHandlerCoverageTests()
    {
        _cache.Setup(c => c.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _records.ToList());
        _fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _files.ToList());
    }

    private SeriesLanguageAuditJobHandler CreateHandler(ISeriesNameResolver? resolver = null) => new(
        _cache.Object,
        _fileStore.Object,
        _retag.Object,
        new TestOptionsMonitor<AppSettings>(_settings),
        _logger,
        resolver);

    private void AddFile(string path, string? series) =>
        _files.Add(new ComicFile { FilePath = path, Metadata = new ComicMetadata { Series = series } });

    private static SeriesMetadataCacheRecord Record(string canonical, string? preferred = null) => new()
    {
        NormalizedKey = canonical.ToLowerInvariant(),
        CanonicalTitle = canonical,
        PreferredLanguage = preferred,
    };

    [Fact]
    public async Task ExecuteAsync_FilesTaggedWithAnyAlias_AreCountedAsMismatches()
    {
        _settings.DefaultPreferredLanguage = "en";
        var record = Record("Attack on Titan");
        record.Aliases = new List<string> { "Shingeki no Kyojin", "   " };
        record.UserAliases = new List<string> { "AoT", "" };
        _records.Add(record);
        AddFile("/lib/a1.cbz", "shingeki no kyojin");
        AddFile("/lib/a2.cbz", "AoT");
        AddFile("/lib/a3.cbz", "Attack on Titan");
        AddFile("/lib/a4.cbz", "   ");
        _files.Add(new ComicFile { FilePath = "/lib/a5.cbz", Metadata = null });

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1 series: 1 with language mismatch (2 file(s)). Enable autoCorrect to retag.", summary);
        Assert.Contains(_logger.Messages, m => m.Contains("'Attack on Titan' (preferred='en', expected='Attack on Titan') has 2 file(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_RecordWithNullTitleCollections_IsScannedWithoutFailing()
    {
        var record = Record("Berserk");
        record.Aliases = null!;
        record.UserAliases = null!;
        record.LocalizedTitles = null!;
        _records.Add(record);
        AddFile("/lib/b1.cbz", "Berserk");

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1 series: 0 with language mismatch (0 file(s)). Enable autoCorrect to retag.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_NoPreferredLanguageAnywhere_SkipsBlankLocalizedEntriesAndLogsNone()
    {
        _settings.DefaultPreferredLanguage = null;
        var record = Record("Attack on Titan");
        record.LocalizedTitles = new List<LocalizedTitle>
        {
            null!,
            new("進撃の巨人", "ja"),
            new("  ", "ko"),
        };
        _records.Add(record);
        AddFile("/lib/c1.cbz", "進撃の巨人");
        AddFile("/lib/c2.cbz", "Attack on Titan");

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1 series: 1 with language mismatch (1 file(s)). Enable autoCorrect to retag.", summary);
        Assert.Contains(_logger.Messages, m => m.Contains("(preferred='<none>', expected='Attack on Titan') has 1 file(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_PinnedNameWithBlankCanonical_MatchesFilesThroughAliases()
    {
        var record = Record("");
        record.SeriesName = "Pinned Name";
        record.PreferredLanguage = "ja";
        record.Aliases = new List<string> { "Old Name" };
        _records.Add(record);
        AddFile("/lib/d1.cbz", "Old Name");
        AddFile("/lib/d2.cbz", "Pinned Name");

        var summary = await CreateHandler().ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 1 series: 1 with language mismatch (1 file(s)). Enable autoCorrect to retag.", summary);
        Assert.Contains(_logger.Messages, m => m.Contains("(preferred='ja', expected='Pinned Name') has 1 file(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_PinnedNameWithoutAnyKnownTitles_IsScannedButNeverMismatched()
    {
        var record = Record("");
        record.SeriesName = "Pinned Name";
        _records.Add(record);
        AddFile("/lib/e1.cbz", "Something Else");

        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal("Scanned 1 series: 0 with language mismatch (0 file(s)); flagged 0 series for metadata backfill.", summary);
        _retag.Verify(r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithSeriesNameResolver_UsesItsExpectedTitle()
    {
        var mismatched = Record("Frieren");
        var unresolvable = Record("Dungeon Meshi");
        _records.Add(mismatched);
        _records.Add(unresolvable);
        AddFile("/lib/f1.cbz", "Frieren");
        AddFile("/lib/f2.cbz", "frieren");
        AddFile("/lib/f3.cbz", "Dungeon Meshi");
        var resolver = new Mock<ISeriesNameResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ResolveForRecord(mismatched)).Returns("Sousou no Frieren");
        resolver.Setup(r => r.ResolveForRecord(unresolvable)).Returns(" ");

        var summary = await CreateHandler(resolver.Object).ExecuteAsync(null, CancellationToken.None);

        Assert.Equal("Scanned 2 series: 1 with language mismatch (2 file(s)). Enable autoCorrect to retag.", summary);
        resolver.Verify(r => r.ResolveForRecord(It.IsAny<SeriesMetadataCacheRecord>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_AutoCorrect_CountsOnlySeriesWithFilesActuallyFlagged()
    {
        _settings.DefaultPreferredLanguage = "ja";
        var flagged = Record("One Piece");
        flagged.LocalizedTitles = new List<LocalizedTitle> { new("ワンピース", "ja") };
        var nothingFlagged = Record("Naruto");
        nothingFlagged.LocalizedTitles = new List<LocalizedTitle> { new("ナルト", "ja") };
        _records.Add(flagged);
        _records.Add(nothingFlagged);
        AddFile("/lib/g1.cbz", "One Piece");
        AddFile("/lib/g2.cbz", "Naruto");
        AddFile("/lib/g3.cbz", "Naruto");
        _retag.Setup(r => r.QueueRetagForSeriesAsync(flagged, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _retag.Setup(r => r.QueueRetagForSeriesAsync(nothingFlagged, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var summary = await CreateHandler().ExecuteAsync("""{"AUTOCORRECT":true}""", CancellationToken.None);

        Assert.Equal("Scanned 2 series: 2 with language mismatch (3 file(s)); flagged 1 series for metadata backfill.", summary);
        Assert.Contains(_logger.Messages, m => m == summary);
        _retag.Verify(r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"autoCorrect":"yes"}""")]
    public async Task ExecuteAsync_MissingOrInvalidOptions_RunInReportOnlyMode(string? optionsJson)
    {
        var record = Record("Alpha", preferred: "ja");
        record.LocalizedTitles = new List<LocalizedTitle> { new("アルファ", "ja") };
        _records.Add(record);
        AddFile("/lib/h1.cbz", "Alpha");

        var summary = await CreateHandler().ExecuteAsync(optionsJson, CancellationToken.None);

        Assert.Equal("Scanned 1 series: 1 with language mismatch (1 file(s)). Enable autoCorrect to retag.", summary);
        _retag.Verify(r => r.QueueRetagForSeriesAsync(It.IsAny<SeriesMetadataCacheRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyLibrary_ReportsNothingScanned()
    {
        var summary = await CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", CancellationToken.None);

        Assert.Equal("Scanned 0 series: 0 with language mismatch (0 file(s)); flagged 0 series for metadata backfill.", summary);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileQueueingARetag_StopsBeforeTheNextSeries()
    {
        var first = Record("Alpha", preferred: "ja");
        first.LocalizedTitles = new List<LocalizedTitle> { new("アルファ", "ja") };
        var second = Record("Beta", preferred: "ja");
        second.LocalizedTitles = new List<LocalizedTitle> { new("ベータ", "ja") };
        _records.Add(first);
        _records.Add(second);
        AddFile("/lib/i1.cbz", "Alpha");
        AddFile("/lib/i2.cbz", "Beta");
        using var cts = new CancellationTokenSource();
        _retag.Setup(r => r.QueueRetagForSeriesAsync(first, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateHandler().ExecuteAsync("""{"autoCorrect":true}""", cts.Token));

        _retag.Verify(r => r.QueueRetagForSeriesAsync(second, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Metadata_DescribesTheJob()
    {
        var handler = CreateHandler();

        Assert.Equal("series-language-audit", handler.JobKey);
        Assert.Equal("Series Language Audit", handler.DisplayName);
        Assert.Contains("autoCorrect", handler.Description);
    }

    private sealed class RecordingLogger : ILogger<SeriesLanguageAuditJobHandler>
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
