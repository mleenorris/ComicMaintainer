using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Re-checking a duplicate works out the name processing would give it now: a file whose name
/// is free again loses its flag and is processed, a file whose name is still taken is left as it
/// is. Swapping gives a duplicate the name and makes the other file the duplicate, deleting nothing.
/// </summary>
public sealed class ComicProcessorServiceDuplicateTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"duplicate_tests_{Guid.NewGuid():N}");
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IProcessingHistoryService> _history = new();
    private readonly AppSettings _settings;
    private BatchJobRegistry? _jobs;

    public ComicProcessorServiceDuplicateTests()
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

    private string Folder => Path.Join(_root, "Saga");

    private string ProperPath => Path.Join(Folder, "Saga - Chapter 0001.cbz");

    // ---- working out the clash ----------------------------------------------------------------

    [Fact]
    public async Task FindDuplicateClash_NamesTheFileThatHasTheProcessedName()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(duplicate);

        Assert.Equal(DuplicateClashStatus.Clashes, clash.Status);
        Assert.Equal(ProperPath, clash.TargetPath);
        Assert.Equal(ProperPath, clash.ClashingPath);
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task FindDuplicateClash_NameNotTaken_IsFree()
    {
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(duplicate);

        Assert.Equal(DuplicateClashStatus.Free, clash.Status);
        Assert.Equal(ProperPath, clash.TargetPath);
        Assert.Null(clash.ClashingPath);
        // Working it out changes nothing.
        Assert.True(File.Exists(duplicate));
        Assert.False(File.Exists(ProperPath));
    }

    [Fact]
    public async Task FindDuplicateClash_FileAlreadyHasItsName_IsFree()
    {
        var file = CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(file);

        Assert.Equal(DuplicateClashStatus.Free, clash.Status);
        Assert.Equal(file, clash.TargetPath);
    }

    [Fact]
    public async Task FindDuplicateClash_RenameTurnedOff_KeepsTheFilesOwnName()
    {
        _settings.WatcherEnableRename = false;
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(duplicate);

        Assert.Equal(DuplicateClashStatus.Free, clash.Status);
        Assert.Equal(duplicate, clash.TargetPath);
    }

    [Fact]
    public async Task FindDuplicateClash_ChapterMarkedOnlyInTheFileName_IsUsedAsProcessingWould()
    {
        Directory.CreateDirectory(Folder);
        CreateArchive("Saga - Chapter 0007.cbz", "7", "holder");
        var duplicate = Path.Join(Folder, "Saga Ch 7.cbz");
        CreateZip(duplicate, "<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series></ComicInfo>", "duplicate");
        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(duplicate);

        Assert.Equal(DuplicateClashStatus.Clashes, clash.Status);
        Assert.Equal(Path.Join(Folder, "Saga - Chapter 0007.cbz"), clash.ClashingPath);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-an-archive")]
    [InlineData("unreadable")]
    [InlineData("outside")]
    [InlineData("no-series")]
    public async Task FindDuplicateClash_NameCannotBeWorkedOut_IsUnknown(string kind)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Join(Folder, "Saga 1.cbz");
        switch (kind)
        {
            case "not-an-archive":
                path = Path.Join(Folder, "notes.txt");
                await File.WriteAllTextAsync(path, "text");
                break;
            case "unreadable":
                await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
                break;
            case "outside":
                path = Path.Join(Path.GetTempPath(), "elsewhere", "Saga 1.cbz");
                break;
            case "no-series":
                _settings.WatcherEnableNormalize = false;
                path = Path.Join(_root, "loose.cbz");
                CreateZip(path, "<?xml version=\"1.0\"?><ComicInfo><Title>No series</Title></ComicInfo>", "x");
                break;
        }

        using var service = CreateService();

        var clash = await service.FindDuplicateClashAsync(path);

        Assert.Equal(DuplicateClashStatus.Unknown, clash.Status);
        Assert.False(string.IsNullOrEmpty(clash.Reason));
        Assert.Null(clash.TargetPath);
    }

    // ---- re-checking ------------------------------------------------------------------------------

    [Fact]
    public async Task Recheck_NameStillTaken_KeepsTheFlagAndNamesTheOtherFile()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { duplicate }));

        Assert.Equal(DuplicateRecheck.OperationName, job.OperationName);
        Assert.Equal(1, job.FailedFiles);
        Assert.Equal(ProperPath, DuplicateRecheck.ClashingPathOf(job.Errors[duplicate]));
        Assert.True(File.Exists(duplicate));
        Assert.Equal("holder", ReadMarker(ProperPath));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recheck_NameFreeAgain_ClearsTheFlagAndProcessesTheFile()
    {
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { duplicate }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.Empty(job.Errors);
        Assert.False(File.Exists(duplicate));
        Assert.Equal("duplicate", ReadMarker(ProperPath));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(duplicate, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.UpdateFilePathAsync(duplicate, ProperPath, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Recheck_FileNoLongerFlagged_IsLeftAlone()
    {
        var file = CreateArchive("Saga 1.cbz", "1", "duplicate");
        _fileStore.Setup(s => s.GetFileAsync(file, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = file, IsDuplicate = false });
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { file }));

        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Recheck_AFileThatThrows_FailsAloneAndTheJobGoesOn()
    {
        var broken = CreateArchive("Saga 2.cbz", "2", "broken");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        _fileStore.Setup(s => s.GetFileAsync(broken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { broken, duplicate }));

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("Re-check failed", job.Errors[broken]);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.True(File.Exists(broken));
        Assert.Equal("duplicate", ReadMarker(ProperPath));
    }

    [Fact]
    public async Task Recheck_NameCannotBeWorkedOut_KeepsTheFlagAndSaysWhy()
    {
        Directory.CreateDirectory(Folder);
        var corrupt = Path.Join(Folder, "Saga 1.cbz");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 1, 2, 3, 4 });
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { corrupt }));

        Assert.Equal(1, job.FailedFiles);
        Assert.StartsWith("Could not work out the processed name: ", job.Errors[corrupt]);
        Assert.Null(DuplicateRecheck.ClashingPathOf(job.Errors[corrupt]));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recheck_ProcessingFailsAfterTheFlagIsCleared_ReportsTheProcessingError()
    {
        // The name is free, but the archive cannot be rewritten: CBR conversion is off and the
        // archive is a ZIP, so normalize leaves the file unprocessed.
        _settings.ConvertRarToCbzOnWrite = false;
        Directory.CreateDirectory(Folder);
        var zip = Path.Join(Folder, "Saga 1.zip");
        CreateZip(zip, "<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series><Number>1</Number></ComicInfo>", "duplicate");
        using var service = CreateService();

        var job = await RunAsync(await service.RecheckDuplicatesAsync(new[] { zip }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Null(DuplicateRecheck.ClashingPathOf(job.Errors.Values.Single()));
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(zip, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- swapping ---------------------------------------------------------------------------------

    [Fact]
    public async Task Swap_GivesTheDuplicateTheNameAndFlagsTheOtherFile()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        FlagAsDuplicate(duplicate);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(duplicate);

        var aside = Path.Join(Folder, "Saga - Chapter 0001 (duplicate).cbz");
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProperPath, result.ProperPath);
        Assert.Equal(aside, result.DuplicatePath);
        Assert.Equal("duplicate", ReadMarker(ProperPath));
        Assert.Equal("holder", ReadMarker(aside));
        Assert.False(File.Exists(duplicate));
        _fileStore.Verify(s => s.UpdateFilePathAsync(ProperPath, aside, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(aside, true, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(duplicate, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Swap_AsideNameTaken_UsesTheNextFreeOne()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        CreateArchive("Saga - Chapter 0001 (duplicate).cbz", "1", "older duplicate");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        FlagAsDuplicate(duplicate);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(duplicate);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.Join(Folder, "Saga - Chapter 0001 (duplicate 2).cbz"), result.DuplicatePath);
        Assert.Equal("older duplicate", ReadMarker(Path.Join(Folder, "Saga - Chapter 0001 (duplicate).cbz")));
    }

    [Fact]
    public async Task Swap_DuplicateCannotTakeTheName_PutsTheOtherFileBack()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        FlagAsDuplicate(duplicate);
        // The duplicate becomes unreadable once the other file is out of the way.
        _fileStore.Setup(s => s.MarkFileDuplicateAsync(duplicate, false, It.IsAny<CancellationToken>()))
            .Callback(() => File.WriteAllBytes(duplicate, new byte[] { 1, 2, 3, 4 }))
            .Returns(Task.CompletedTask);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(duplicate);

        var aside = Path.Join(Folder, "Saga - Chapter 0001 (duplicate).cbz");
        Assert.False(result.Succeeded);
        Assert.False(result.Conflict);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal("holder", ReadMarker(ProperPath));
        Assert.False(File.Exists(aside));
        Assert.True(File.Exists(duplicate));
        _fileStore.Verify(s => s.UpdateFilePathAsync(aside, ProperPath, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(ProperPath, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(s => s.MarkFileDuplicateAsync(duplicate, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Swap_OtherFileCannotBePutBack_SaysWhereItIs()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        FlagAsDuplicate(duplicate);
        // A third file takes the name the moment the other file leaves it.
        _fileStore.Setup(s => s.MarkFileDuplicateAsync(duplicate, false, It.IsAny<CancellationToken>()))
            .Callback(() => CreateArchive("Saga - Chapter 0001.cbz", "1", "newcomer"))
            .Returns(Task.CompletedTask);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(duplicate);

        var aside = Path.Join(Folder, "Saga - Chapter 0001 (duplicate).cbz");
        Assert.False(result.Succeeded);
        Assert.Contains("Saga - Chapter 0001 (duplicate).cbz", result.Error);
        Assert.Equal("holder", ReadMarker(aside));
        Assert.Equal("newcomer", ReadMarker(ProperPath));
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task Swap_FileNotFlagged_ChangesNothing()
    {
        CreateArchive("Saga - Chapter 0001.cbz", "1", "holder");
        var file = CreateArchive("Saga 1.cbz", "1", "duplicate");
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(file);

        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);
        Assert.Equal("holder", ReadMarker(ProperPath));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Swap_NameNotTaken_ChangesNothing()
    {
        var duplicate = CreateArchive("Saga 1.cbz", "1", "duplicate");
        FlagAsDuplicate(duplicate);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(duplicate);

        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);
        Assert.True(File.Exists(duplicate));
    }

    [Fact]
    public async Task Swap_NameCannotBeWorkedOut_IsRefused()
    {
        Directory.CreateDirectory(Folder);
        var corrupt = Path.Join(Folder, "Saga 1.cbz");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 1, 2, 3, 4 });
        FlagAsDuplicate(corrupt);
        using var service = CreateService();

        var result = await service.SwapDuplicateAsync(corrupt);

        Assert.False(result.Succeeded);
        Assert.False(result.Conflict);
        Assert.StartsWith("Could not work out the processed name: ", result.Error);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private void FlagAsDuplicate(string path) =>
        _fileStore.Setup(s => s.GetFileAsync(path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = path, IsDuplicate = true });

    private BatchJobRegistry Jobs => _jobs ??= TestServices.CreateBatchJobRegistry(
        new TestOptionsMonitor<AppSettings>(_settings),
        NullLogger<BatchJobRegistry>.Instance);

    private ComicProcessorService CreateService() =>
        TestServices.CreateComicProcessor(
            new TestOptionsMonitor<AppSettings>(_settings),
            Mock.Of<ILogger<ComicProcessorService>>(),
            _fileStore.Object,
            _history.Object,
            externalSeriesMetadata: Mock.Of<IExternalSeriesMetadataService>(),
            jobRegistry: Jobs);

    private async Task<ProcessingJob> RunAsync(Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return (await Jobs.WaitForCompletionAsync(jobId, timeout.Token))!;
    }

    private string CreateArchive(string fileName, string number, string marker)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Join(Folder, fileName);
        CreateZip(path, $"<?xml version=\"1.0\"?><ComicInfo><Series>Saga</Series><Number>{number}</Number><Title>Chapter {number}</Title></ComicInfo>", marker);
        return path;
    }

    // Each archive carries a marker page, so a test can tell which file ended up under a name.
    private static void CreateZip(string path, string comicInfo, string marker)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write(comicInfo);
        }

        using (var writer = new StreamWriter(archive.CreateEntry("marker.txt").Open()))
        {
            writer.Write(marker);
        }

        using (var writer = new StreamWriter(archive.CreateEntry("page001.jpg").Open()))
        {
            writer.Write("page");
        }
    }

    private static string ReadMarker(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("marker.txt")!.Open());
        return reader.ReadToEnd();
    }
}
