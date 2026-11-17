using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StoredRar4 = ComicMaintainer.Tests.Services.ComicReaderServiceCoverageTests.StoredRar4;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// <see cref="ComicProcessorService.PreviewProcessingAsync"/>: the dry run of processing names the
/// path and the tag changes the real run produces, and changes nothing itself.
/// </summary>
public sealed class ComicProcessorServicePreviewTests : IDisposable
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly Mock<ISeriesNameResolver> _resolver = new();
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly List<ComicProcessorService> _services = new();

    public ComicProcessorServicePreviewTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cps-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = AppSettings.DefaultFilenameFormat,
            IssueNumberPadding = 4
        };
        ResolveTo("Batman");
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TestIO.TryDeleteDirectory(_root);
    }

    private void ResolveTo(string series) =>
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = series, WinningStep = SeriesNameResolutionStep.MatchedCache });

    private ComicProcessorService CreateService()
    {
        var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            _history.Object,
            seriesMetadataCache: _cache.Object,
            seriesNameResolver: _resolver.Object);
        _services.Add(service);
        return service;
    }

    private string WriteArchive(string? comicInfo, string relativePath)
    {
        var path = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (comicInfo is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            writer.Write(comicInfo);
        }

        using var page = zip.CreateEntry("page001.jpg").Open();
        page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        return path;
    }

    private string WriteCbr(string comicInfo, string relativePath)
    {
        var path = Path.GetFullPath(Path.Join(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StoredRar4.Write(
            path,
            ("page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }),
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(comicInfo)));
        return path;
    }

    private static XDocument? ReadComicInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("ComicInfo.xml");
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return XDocument.Parse(reader.ReadToEnd());
    }

    /// <summary>Nothing but reads reached the file store, and nothing reached the history.</summary>
    private void AssertNothingWritten()
    {
        Assert.All(_fileStore.Invocations, call => Assert.StartsWith("Get", call.Method.Name));
        Assert.Empty(_history.Invocations);
        _resolver.Verify(
            r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("<ComicInfo><Series>batman</Series><Number>001</Number></ComicInfo>", "Batman/batman 1.cbz")]
    [InlineData("<ComicInfo><Series>Batman</Series><Title>The Court</Title><Number>7</Number><Volume>2</Volume></ComicInfo>", "Batman/whatever.cbz")]
    [InlineData("<ComicInfo><Series>Batman</Series></ComicInfo>", "Batman/Batman Ch 12.cbz")]
    [InlineData(null, "Batman/Batman 003.cbz")]
    [InlineData("<ComicInfo><Series>Batman Annual</Series><Number>Annual 2</Number></ComicInfo>", "Batman/Batman Annual 02.cbz")]
    public async Task Preview_NamesThePathAndTagsTheForcedRunProduces(string? comicInfo, string relativePath)
    {
        var path = WriteArchive(comicInfo, relativePath);
        var bytes = await File.ReadAllBytesAsync(path);
        var before = ReadComicInfo(path);
        var service = CreateService();

        var preview = await service.PreviewProcessingAsync(path);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        AssertNothingWritten();
        Assert.Null(preview.Error);
        Assert.Equal(ProcessingPreviewClash.None, preview.Clash);
        Assert.True(preview.HasChanges);

        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));

        var after = preview.NewFilePath ?? path;
        Assert.True(File.Exists(after), $"Processing did not leave the file at {after}");
        Assert.Equal(preview.NewFilePath is not null, !File.Exists(path));
        Assert.Equal(
            ComicInfoXml.Diff(before, ReadComicInfo(after)!),
            preview.TagChanges.Where(change => change.Field != nameof(ComicMetadata.VolumeYear)));
        Assert.Equal("Batman", preview.Series?.Replace(" Annual", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_OfAProcessedFile_ShowsNothingToDo()
    {
        var path = WriteArchive("<ComicInfo><Series>batman</Series><Number>001</Number></ComicInfo>", "Batman/batman 1.cbz");
        var service = CreateService();
        var moved = (await service.PreviewProcessingAsync(path)).NewFilePath!;
        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));

        var preview = await service.PreviewProcessingAsync(moved);

        Assert.False(preview.HasChanges);
        Assert.False(preview.Renames);
        Assert.Null(preview.NewFilePath);
        Assert.Empty(preview.TagChanges);
        Assert.Equal("Batman", preview.Series);
    }

    [Fact]
    public async Task Preview_ListsTheVolumeStartYearKeptInTheDatabase()
    {
        var path = WriteArchive("<ComicInfo><Series>Batman (2016)</Series><Number>1</Number><Title>I Am Gotham</Title></ComicInfo>", "Batman (2016)/Batman 001.cbz");
        var service = CreateService();

        var preview = await service.PreviewProcessingAsync(path);

        Assert.Contains(new ComicInfoFieldChange("VolumeYear", null, "2016"), preview.TagChanges);
        Assert.Contains(new ComicInfoFieldChange("Series", "Batman (2016)", "Batman"), preview.TagChanges);
        AssertNothingWritten();

        Assert.True(await service.ProcessFileAsync(path, forceReprocess: true));
        _fileStore.Verify(f => f.SetVolumeYearAsync(path, 2016, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(File.Exists(preview.NewFilePath ?? path));
    }

    [Fact]
    public async Task Preview_FlagsANewNameAnotherFileHas()
    {
        var path = WriteArchive("<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>", "Batman/batman 1.cbz");
        var service = CreateService();
        var target = (await service.PreviewProcessingAsync(path)).NewFilePath!;
        WriteArchive("<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>", Path.GetRelativePath(_root, target));

        var preview = await service.PreviewProcessingAsync(path);

        Assert.Equal(ProcessingPreviewClash.ExistingFile, preview.Clash);
        Assert.Equal(target, preview.NewFilePath);
        Assert.Equal(target, preview.ClashWith);
        Assert.False(preview.Renames);
        Assert.True(preview.HasChanges);

        // The real run flags it as a duplicate instead of renaming it.
        Assert.False(await service.ProcessFileAsync(path, forceReprocess: true));
        _fileStore.Verify(f => f.MarkFileDuplicateAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Preview_FollowsTheSettings()
    {
        var path = WriteArchive("<ComicInfo><Series>batman</Series><Number>001</Number></ComicInfo>", "Batman/batman 1.cbz");
        var service = CreateService();

        // Rename off: only the tags.
        _settings.WatcherEnableRename = false;
        var tagsOnly = await service.PreviewProcessingAsync(path);
        Assert.Null(tagsOnly.NewFilePath);
        Assert.Contains(new ComicInfoFieldChange("Series", "batman", "Batman"), tagsOnly.TagChanges);

        // Normalize off: the name from the file's own tags, and no tag changes.
        _settings.WatcherEnableRename = true;
        _settings.WatcherEnableNormalize = false;
        var nameOnly = await service.PreviewProcessingAsync(path);
        Assert.Empty(nameOnly.TagChanges);
        Assert.StartsWith("batman - Chapter 0001", Path.GetFileName(nameOnly.NewFilePath));

        // Both off: nothing.
        _settings.WatcherEnableRename = false;
        Assert.False((await service.PreviewProcessingAsync(path)).HasChanges);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Preview_WithNormalizeOff_RecoversAMarkedChapterForTheNameOnly()
    {
        _settings.WatcherEnableNormalize = false;
        var path = WriteArchive("<ComicInfo><Series>Batman</Series></ComicInfo>", "Batman/Batman Ch 12.cbz");

        var preview = await CreateService().PreviewProcessingAsync(path);

        Assert.Empty(preview.TagChanges);
        Assert.Contains("0012", Path.GetFileName(preview.NewFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_ReportsWhyProcessingWouldFail()
    {
        var service = CreateService();
        var missing = Path.Join(_root, "Batman", "missing.cbz");
        var text = Path.Join(_root, "Batman", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(text)!);
        await File.WriteAllTextAsync(text, "not a comic");
        var outside = Path.Join(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "x.cbz");
        var broken = Path.Join(_root, "Batman", "broken.cbz");
        await File.WriteAllTextAsync(broken, "not a zip");
        var loose = WriteArchive(null, "001.cbz");
        _resolver.Setup(r => r.ResolveAsync(loose, It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Unknown Series", WinningStep = SeriesNameResolutionStep.UnknownSentinel });

        Assert.Equal("File not found", (await service.PreviewProcessingAsync(missing)).Error);
        Assert.Equal("File is not a comic archive", (await service.PreviewProcessingAsync(text)).Error);
        Assert.Equal("File is outside the allowed directories", (await service.PreviewProcessingAsync(outside)).Error);
        Assert.NotNull((await service.PreviewProcessingAsync(broken)).Error);
        var looseItem = await service.PreviewProcessingAsync(loose);
        Assert.StartsWith("Loose file in the library root", looseItem.Error);
        Assert.Equal("Batman", (await service.PreviewProcessingAsync(missing)).Series);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Preview_OfAnUnreadableArchive_NamesTheFailingPhases()
    {
        var broken = Path.Join(_root, "Batman", "broken.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllBytesAsync(broken, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        var service = CreateService();

        var both = await service.PreviewProcessingAsync(broken);
        Assert.NotNull(both.Error);

        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = false;
        var neither = await service.PreviewProcessingAsync(broken);
        Assert.False(neither.HasChanges);
    }

    [Fact]
    public async Task Preview_OfACbr_ShowsTheConversionOrWhyTheTagsStayUnwritten()
    {
        var cbr = WriteCbr("<ComicInfo><Series>batman</Series><Number>1</Number></ComicInfo>", "Batman/batman 1.cbr");
        var service = CreateService();

        var converted = await service.PreviewProcessingAsync(cbr);
        Assert.EndsWith(".cbz", converted.NewFilePath, StringComparison.Ordinal);
        Assert.Null(converted.Warning);
        Assert.Contains(new ComicInfoFieldChange("Series", "batman", "Batman"), converted.TagChanges);

        // Renaming off: the file only becomes a CBZ.
        _settings.WatcherEnableRename = false;
        Assert.Equal(Path.ChangeExtension(cbr, ".cbz"), (await service.PreviewProcessingAsync(cbr)).NewFilePath);

        // A CBZ already next to it stops the conversion.
        WriteArchive(null, "Batman/batman 1.cbz");
        var blocked = await service.PreviewProcessingAsync(cbr);
        Assert.StartsWith("Normalize failed: Cannot convert the archive to CBZ", blocked.Error);
        Assert.NotEmpty(blocked.TagChanges);
        File.Delete(Path.ChangeExtension(cbr, ".cbz"));

        // Conversion off: the tags are not written, but the file is still renamed.
        _settings.WatcherEnableRename = true;
        _settings.ConvertRarToCbzOnWrite = false;
        var kept = await service.PreviewProcessingAsync(cbr);
        Assert.Equal(ComicProcessorService.NormalizeNotWritableMessage, kept.Warning);
        Assert.EndsWith(".cbr", kept.NewFilePath, StringComparison.Ordinal);
        Assert.True(kept.Renames);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Preview_FillsEmptyElementsFromTheProviderMatch()
    {
        _settings.FillComicInfoFromProviders = true;
        _cache.Setup(c => c.GetByTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { NormalizedKey = "batman", CanonicalTitle = "Batman", LookupStatus = "success", Genres = ["Superhero"] });
        var path = WriteArchive("<ComicInfo><Title>Chapter 1</Title><Series>Batman</Series><Number>1</Number></ComicInfo>", "Batman/Batman - Chapter 0001.cbz");

        var preview = await CreateService().PreviewProcessingAsync(path);

        Assert.Equal(new[] { new ComicInfoFieldChange("Genre", null, "Superhero") }, preview.TagChanges);
        Assert.Null(preview.NewFilePath);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Preview_OfASymlink_SaysItIsNotProcessed()
    {
        var target = WriteArchive("<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>", "Batman/real.cbz");
        var link = Path.Join(_root, "Batman", "link.cbz");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Creating links needs rights this machine does not grant.
        }

        Assert.Equal("File symlinks are not processed", (await CreateService().PreviewProcessingAsync(link)).Error);
    }

    [Fact]
    public async Task Preview_WhenAReadThrows_ReportsTheError()
    {
        var path = WriteArchive("<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>", "Batman/batman 1.cbz");
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver down"));

        var preview = await CreateService().PreviewProcessingAsync(path);

        Assert.Equal("resolver down", preview.Error);
        Assert.Equal("Batman", preview.Series);
    }

    [Fact]
    public async Task Preview_WhenCancelled_Throws()
    {
        var path = WriteArchive("<ComicInfo><Series>Batman</Series><Number>1</Number></ComicInfo>", "Batman/batman 1.cbz");
        using var cancel = new CancellationTokenSource();
        _resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<ComicMetadata?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().PreviewProcessingAsync(path, cancel.Token));
    }
}
