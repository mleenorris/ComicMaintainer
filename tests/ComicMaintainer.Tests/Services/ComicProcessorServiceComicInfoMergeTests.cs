using System.IO.Compression;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Every metadata write merges into the archive's existing ComicInfo.xml: only the fields being
/// written change, and credits, Genre, Web, LanguageISO, Manga, Pages and the rest survive edits,
/// batch updates and normalize.
/// </summary>
public sealed class ComicProcessorServiceComicInfoMergeTests : IDisposable
{
    private const string SuwayomiComicInfo = """
        <?xml version="1.0" encoding="utf-8"?>
        <ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Title>The Black Swordsman</Title>
          <Series>Berserk</Series>
          <Number>1</Number>
          <Summary>Guts arrives.</Summary>
          <Writer>Kentaro Miura</Writer>
          <Penciller>Kentaro Miura</Penciller>
          <Translator>Duane Johnson</Translator>
          <Publisher>Hakusensha</Publisher>
          <Genre>Action, Dark Fantasy</Genre>
          <Tags>Seinen</Tags>
          <Web>https://example.org/berserk/1</Web>
          <LanguageISO>en</LanguageISO>
          <Manga>YesAndRightToLeft</Manga>
          <Pages>
            <Page Image="0" Type="FrontCover" />
          </Pages>
        </ComicInfo>
        """;

    /// <summary>Elements the application does not model, which every write must keep.</summary>
    private static readonly string[] UnmodelledElements =
        { "Penciller", "Translator", "Genre", "Web", "LanguageISO", "Manga", "Pages" };

    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly List<ComicProcessorService> _services = new();

    public ComicProcessorServiceComicInfoMergeTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cps-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = AppSettings.DefaultFilenameFormat,
            IssueNumberPadding = 4
        };
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task BatchUpdate_WithOnlyPublisher_KeepsEveryOtherTagOfEachFile()
    {
        // The "Batch Update Tags" scenario: 'Kodansha' entered as the only field used to leave
        // <ComicInfo><Publisher>Kodansha</Publisher></ComicInfo> in every selected file.
        var service = CreateService();
        var first = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);
        var second = WriteArchive("Berserk/Berserk - Chapter 0002.cbz", SuwayomiComicInfo.Replace("<Number>1</Number>", "<Number>2</Number>"));

        var jobId = await service.UpdateMetadataAsync(
            new[] { first, second },
            new ComicMetadata { Publisher = "Kodansha" },
            ComicMetadataFieldFlags.Publisher);
        var job = await WaitForJobAsync(service, jobId);

        Assert.Equal(2, job.ProcessedFiles);
        Assert.Equal(0, job.FailedFiles);
        foreach (var (path, number) in new[] { (first, "1"), (second, "2") })
        {
            var info = ReadComicInfo(path);
            Assert.Equal("Kodansha", info.Element("Publisher")?.Value);
            Assert.Equal("Berserk", info.Element("Series")?.Value);
            Assert.Equal(number, info.Element("Number")?.Value);
            Assert.Equal("The Black Swordsman", info.Element("Title")?.Value);
            Assert.Equal("Guts arrives.", info.Element("Summary")?.Value);
            Assert.Equal("Kentaro Miura", info.Element("Writer")?.Value);
            AssertUnmodelledElementsKept(info);
        }
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ChangedTitle_KeepsCreditsAndUnmodelledTags()
    {
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);

        var result = await service.ApplyMetadataEditAsync(
            path, new ComicMetadata { Title = "Prologue" }, ComicMetadataFieldFlags.Title);

        Assert.Equal(MetadataWriteOutcome.Written, result.Outcome);
        Assert.Equal(path, result.FilePath);
        var info = ReadComicInfo(path);
        Assert.Equal("Prologue", info.Element("Title")?.Value);
        Assert.Equal("Kentaro Miura", info.Element("Writer")?.Value);
        Assert.Equal("Seinen", info.Element("Tags")?.Value);
        AssertUnmodelledElementsKept(info);
        Assert.Contains("page001.jpg", EntryNames(path));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ListedFieldWithoutValue_RemovesOnlyThatElement()
    {
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);

        var result = await service.ApplyMetadataEditAsync(
            path, new ComicMetadata(), ComicMetadataFieldFlags.Publisher | ComicMetadataFieldFlags.Authors);

        Assert.True(result.Succeeded);
        var info = ReadComicInfo(path);
        Assert.Null(info.Element("Publisher"));
        Assert.Null(info.Element("Writer"));
        Assert.Equal("Berserk", info.Element("Series")?.Value);
        AssertUnmodelledElementsKept(info);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ValuesAlreadyInTheArchive_LeavesItUntouched()
    {
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);
        var before = await File.ReadAllBytesAsync(path);

        var result = await service.ApplyMetadataEditAsync(
            path, new ComicMetadata { Series = "Berserk", Issue = "1" }, ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Issue);

        Assert.Equal(MetadataWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_MalformedComicInfo_FailsAndLeavesTheArchiveUntouched()
    {
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", "<ComicInfo><Series>Berserk</Series><Genre>");
        var before = await File.ReadAllBytesAsync(path);

        var result = await service.ApplyMetadataEditAsync(
            path, new ComicMetadata { Title = "Prologue" }, ComicMetadataFieldFlags.Title);

        Assert.Equal(MetadataWriteOutcome.Failed, result.Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UpdateMetadataAsync_WithoutAMask_AppliesOnlyTheFieldsThatCarryAValue()
    {
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);

        Assert.True(await service.UpdateMetadataAsync(path, new ComicMetadata { Volume = "1" }));

        var info = ReadComicInfo(path);
        Assert.Equal("1", info.Element("Volume")?.Value);
        Assert.Equal("The Black Swordsman", info.Element("Title")?.Value);
        Assert.Equal("Hakusensha", info.Element("Publisher")?.Value);
        AssertUnmodelledElementsKept(info);
    }

    [Fact]
    public async Task ProcessFileAsync_NormalizedSeriesAndNumberWithARealTitle_DoesNotRewriteTheArchive()
    {
        // Suwayomi writes a real chapter title. Normalize used to rewrite every such file just
        // because the title was not "Chapter <n>", dropping every unmodelled element.
        _settings.WatcherEnableRename = false;
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo);
        var before = await File.ReadAllBytesAsync(path);

        Assert.True(await service.ProcessFileAsync(path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(path, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_NormalizeThatFillsInTheSeries_KeepsTheTitleAndEveryOtherElement()
    {
        _settings.WatcherEnableRename = false;
        var service = CreateService();
        var path = WriteArchive("Berserk/Berserk - Chapter 0001.cbz", SuwayomiComicInfo.Replace("<Series>Berserk</Series>", string.Empty));

        Assert.True(await service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal("Berserk", info.Element("Series")?.Value);
        Assert.Equal("The Black Swordsman", info.Element("Title")?.Value);
        Assert.Equal("Kentaro Miura", info.Element("Writer")?.Value);
        Assert.Equal("Hakusensha", info.Element("Publisher")?.Value);
        Assert.Equal("Seinen", info.Element("Tags")?.Value);
        AssertUnmodelledElementsKept(info);
    }

    [Theory]
    [InlineData("Chapter 0012", "Chapter 12")]
    [InlineData("Chapter Unknown", "Chapter 12")]
    [InlineData("", "Chapter 12")]
    public async Task ProcessFileAsync_MissingOrStaleGeneratedTitle_IsNormalized(string title, string expected)
    {
        _settings.WatcherEnableRename = false;
        var service = CreateService();
        var xml = $"<ComicInfo><Title>{title}</Title><Series>Berserk</Series><Number>12</Number><Genre>Action</Genre></ComicInfo>";
        var path = WriteArchive("Berserk/Berserk - Chapter 0012.cbz", xml);

        Assert.True(await service.ProcessFileAsync(path));

        var info = ReadComicInfo(path);
        Assert.Equal(expected, info.Element("Title")?.Value);
        Assert.Equal("Action", info.Element("Genre")?.Value);
    }

    [Fact]
    public async Task ProcessFileAsync_NormalizeDisabled_RenamesWithTheChapterButNeverRewritesTheArchive()
    {
        // The rename path used to write the chapter it recovered from the file name into
        // ComicInfo.xml even with normalize turned off.
        _settings.WatcherEnableNormalize = false;
        _settings.WatcherEnableRename = true;
        var service = CreateService();
        var xml = "<ComicInfo><Series>Berserk</Series><Genre>Action</Genre></ComicInfo>";
        var path = WriteArchive("Berserk/Berserk Ch 5.cbz", xml);
        var before = ReadComicInfoText(path);

        Assert.True(await service.ProcessFileAsync(path));

        var renamed = Path.Join(_root, "Berserk", "Berserk - Chapter 0005.cbz");
        Assert.True(File.Exists(renamed));
        Assert.Equal(before, ReadComicInfoText(renamed));
    }

    [Fact]
    public async Task ProcessFileAsync_NormalizeEnabled_WritesTheRecoveredChapterOnly()
    {
        var service = CreateService();
        var xml = "<ComicInfo><Series>Berserk</Series><Title>The Brand</Title><Genre>Action</Genre></ComicInfo>";
        var path = WriteArchive("Berserk/Berserk Ch 5.cbz", xml);

        Assert.True(await service.ProcessFileAsync(path));

        var renamed = Path.Join(_root, "Berserk", "Berserk - Chapter 0005.cbz");
        var info = ReadComicInfo(renamed);
        Assert.Equal("5", info.Element("Number")?.Value);
        Assert.Equal("The Brand", info.Element("Title")?.Value);
        Assert.Equal("Action", info.Element("Genre")?.Value);
    }

    private BatchJobRegistry? _jobRegistryInstance;

    private BatchJobRegistry Jobs => _jobRegistryInstance ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService()
    {
        var service = TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<ComicProcessorService>.Instance,
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            jobRegistry: Jobs);
        _services.Add(service);
        return service;
    }

    private async Task<ProcessingJob> WaitForJobAsync(ComicProcessorService service, Guid jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var job = Jobs.GetJob(jobId);
            if (job is { IsTerminal: true })
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job {jobId} did not finish");
    }

    private static void AssertUnmodelledElementsKept(XElement info)
    {
        foreach (var name in UnmodelledElements)
        {
            Assert.True(info.Element(name) is not null, $"<{name}> was dropped");
        }

        Assert.Equal("FrontCover", info.Element("Pages")?.Element("Page")?.Attribute("Type")?.Value);
    }

    private string WriteArchive(string relativePath, string comicInfo)
    {
        var path = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write(comicInfo);
        }

        using var page = zip.CreateEntry("page001.jpg").Open();
        page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        return path;
    }

    private static XElement ReadComicInfo(string path) => XElement.Parse(ReadComicInfoText(path));

    private static string ReadComicInfoText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("ComicInfo.xml")!.Open());
        return reader.ReadToEnd();
    }

    private static string[] EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }
}
