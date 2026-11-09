using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// A rename that only changes the case of a file's name (e.g. the provider returns
/// "Tower of God" for a folder named "Tower Of God") is a rename, not a collision with the
/// file itself, on every file system.
/// </summary>
public sealed class ComicProcessorServiceCaseOnlyRenameTests : IDisposable
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Join(Path.GetTempPath(), $"case_rename_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly AppSettings _settings;

    public ComicProcessorServiceCaseOnlyRenameTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettings
        {
            WatchedDirectory = _root,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = Path.Join(_root, "temp"),
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task ProcessFileAsync_TargetDiffersOnlyInCase_RenamesTheFileInsteadOfFlaggingADuplicate()
    {
        var file = CreateArchive("Tower of God", "Tower Of God - Chapter 0001.cbz", series: "Tower of God", number: "1");
        var expected = Path.Join(_root, "Tower of God", "Tower of God - Chapter 0001.cbz");
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file));

        Assert.Equal(new[] { "Tower of God - Chapter 0001.cbz" }, ListNames("Tower of God"));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileRenamedAsync(expected, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameFilesAsync_TargetDiffersOnlyInCase_RenamesTheFile()
    {
        var file = CreateArchive("Tower of God", "TOWER OF GOD - CHAPTER 0002.cbz", series: "Tower of God", number: "2");
        var expected = Path.Join(_root, "Tower of God", "Tower of God - Chapter 0002.cbz");
        using var service = CreateService();

        var job = await WaitForJobAsync(service, await service.RenameFilesAsync(new[] { file }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Equal(new[] { "Tower of God - Chapter 0002.cbz" }, ListNames("Tower of God"));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(s => s.UpdateFilePathAsync(file, expected, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_FileWronglyFlaggedAsDuplicate_ClearsTheFlagOnceRenamed()
    {
        // Files flagged by the earlier case-only bug must not stay in the duplicates list,
        // where "Delete All Duplicates" would delete them.
        var file = CreateArchive("Tower of God", "Tower Of God - Chapter 0003.cbz", series: "Tower of God", number: "3");
        var expected = Path.Join(_root, "Tower of God", "Tower of God - Chapter 0003.cbz");
        _fileStore.Setup(s => s.GetFileAsync(expected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = expected, IsDuplicate = true });
        using var service = CreateService();

        Assert.True(await service.ProcessFileAsync(file, forceReprocess: true));

        _fileStore.Verify(s => s.MarkFileDuplicateAsync(expected, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFileAsync_DistinctFileWithTheTargetNameInAnotherCase_IsStillADuplicate()
    {
        // Only a case-sensitive directory can hold both names; elsewhere this is the test above.
        var directory = Path.Join(_root, "Tower of God");
        Directory.CreateDirectory(directory);
        if (!DirectoryIsCaseSensitive(directory))
        {
            return;
        }

        CreateArchive("Tower of God", "Tower of God - Chapter 0004.cbz", series: "Tower of God", number: "4");
        var file = CreateArchive("Tower of God", "Tower Of God - Chapter 0004.cbz", series: "Tower of God", number: "4");
        using var service = CreateService();

        Assert.False(await service.ProcessFileAsync(file));

        _fileStore.Verify(s => s.MarkFileDuplicateAsync(file, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, ListNames("Tower of God").Length);
    }

    [Theory]
    // A directory that ignores case lists the one file under its stored name, whichever it is.
    [InlineData(new[] { "Tower Of God 1.cbz" }, true)]
    [InlineData(new[] { "TOWER OF GOD 1.cbz", "Other.cbz" }, true)]
    // A directory that keeps case apart and holds both names holds two files.
    [InlineData(new[] { "Tower Of God 1.cbz", "Tower of God 1.cbz" }, false)]
    public void IsSameFileUnderAnotherCase_DecidesFromTheDirectoryListing(string[] listing, bool expected)
    {
        var source = Path.Join(_root, "Tower Of God 1.cbz");
        var target = Path.Join(_root, "Tower of God 1.cbz");

        Assert.Equal(expected, ComicProcessorService.IsSameFileUnderAnotherCase(source, target, listing));
    }

    [Fact]
    public void IsSameFileUnderAnotherCase_DifferentNameOrDirectory_IsNeverTheSameFile()
    {
        var source = Path.Join(_root, "a", "Saga 1.cbz");

        Assert.False(ComicProcessorService.IsSameFileUnderAnotherCase(source, Path.Join(_root, "a", "Saga 2.cbz"), new[] { "Saga 1.cbz" }));
        Assert.False(ComicProcessorService.IsSameFileUnderAnotherCase(source, Path.Join(_root, "b", "SAGA 1.cbz"), new[] { "Saga 1.cbz" }));
    }

    private BatchJobRegistry? _jobRegistryInstance;

    private BatchJobRegistry Jobs => _jobRegistryInstance ??= new BatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService() =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            Mock.Of<IProcessingHistoryService>(),
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: Jobs);

    private string[] ListNames(string folder) =>
        Directory.EnumerateFiles(Path.Join(_root, folder)).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    private static bool DirectoryIsCaseSensitive(string directory)
    {
        var probe = Path.Join(directory, $"probe_{Guid.NewGuid():N}.txt");
        File.WriteAllText(probe, string.Empty);
        try
        {
            return !File.Exists(probe.ToUpperInvariant());
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private string CreateArchive(string folder, string fileName, string series, string number)
    {
        var directory = Path.Join(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write($"<?xml version=\"1.0\"?><ComicInfo><Series>{series}</Series><Number>{number}</Number><Title>Chapter {number}</Title></ComicInfo>");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }

        return path;
    }

    private async Task<ProcessingJob> WaitForJobAsync(ComicProcessorService service, Guid jobId)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (true)
        {
            var job = Jobs.GetJob(jobId);
            if (job is { Status: not (JobStatus.Queued or JobStatus.Running) })
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not finish.");
            }

            await Task.Delay(20);
        }
    }
}
