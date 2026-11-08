using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StoredRar4 = ComicMaintainer.Tests.Services.ComicReaderServiceCoverageTests.StoredRar4;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Writing tags to a CBR, RAR or ZIP: with "Convert CBR/RAR to CBZ when writing tags" on, the
/// tagged archive becomes a real CBZ (never a ZIP that keeps the .cbr name), the library record
/// follows it and the original is removed; with it off the archive is never rewritten and a
/// user's edit is kept in the database, where reads pick it up.
/// </summary>
public partial class ComicProcessorServiceCoverageTests
{
    private static readonly byte[] RarPage = Encoding.ASCII.GetBytes("jpg page one");

    [Fact]
    public async Task ApplyMetadataEditAsync_CbrWithConversionOn_WritesATaggedCbzAndRemovesTheCbr()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", "A Title", ("Penciller", "Artist")));
        var cbz = Path.ChangeExtension(cbr, ".cbz");

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.ConvertedToCbz, cbz), result);
        Assert.False(File.Exists(cbr));
        Assert.Equal(new[] { "pages/page001.jpg", "page002.jpg", "ComicInfo.xml" }, EntryNames(cbz));
        Assert.Equal(RarPage, ReadZipEntry(cbz, "pages/page001.jpg"));
        var info = ReadComicInfo(cbz)!;
        Assert.Equal("New", info.Element("Series")?.Value);
        Assert.Equal("3", info.Element("Number")?.Value);
        Assert.Equal("A Title", info.Element("Title")?.Value);
        Assert.Equal("Artist", info.Element("Penciller")?.Value);
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        Assert.Empty(TempFilesNextTo(cbz));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_SolidRar_IsReadInOrderAndConverted()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/solid.cbr", Xml("Old", "3", null), solid: true);
        var cbz = Path.ChangeExtension(cbr, ".cbz");

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Title = "Tagged" }, ComicMetadataFieldFlags.Title);

        Assert.Equal(MetadataWriteOutcome.ConvertedToCbz, result.Outcome);
        Assert.Equal(RarPage, ReadZipEntry(cbz, "pages/page001.jpg"));
        Assert.Equal("Tagged", ReadComicInfo(cbz)!.Element("Title")?.Value);
        Assert.False(File.Exists(cbr));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ZipWithConversionOn_BecomesACbz()
    {
        var service = CreateService();
        var zip = Path.GetFullPath(WriteCbz("Series/book.zip", Xml("Old", "1", "Chapter 1")));
        var cbz = Path.ChangeExtension(zip, ".cbz");

        var result = await service.ApplyMetadataEditAsync(zip, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.ConvertedToCbz, cbz), result);
        Assert.False(File.Exists(zip));
        Assert.Equal("New", ReadComicInfo(cbz)!.Element("Series")?.Value);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_CbrAlreadyHoldingTheValues_IsNotConverted()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Same", "3", "A Title"));
        var before = await File.ReadAllBytesAsync(cbr);

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "Same" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(Path.ChangeExtension(cbr, ".cbz")));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_CbzAlreadyNextToTheCbr_FailsAndKeepsBothFiles()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = WriteCbz("Series/book.cbz", Xml("Other", "9", null));
        var cbrBefore = await File.ReadAllBytesAsync(cbr);
        var cbzBefore = await File.ReadAllBytesAsync(cbz);

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteResult.Failed(cbr), result);
        Assert.Equal(cbrBefore, await File.ReadAllBytesAsync(cbr));
        Assert.Equal(cbzBefore, await File.ReadAllBytesAsync(cbz));
        Assert.Empty(TempFilesNextTo(cbr));
        _fileStore.Verify(f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_OriginalCannotBeDeleted_UndoesTheConversion()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete a file that is held open without delete sharing.
            return;
        }

        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var before = await File.ReadAllBytesAsync(cbr);

        MetadataWriteResult result;
        using (new FileStream(cbr, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);
        }

        Assert.Equal(MetadataWriteResult.Failed(cbr), result);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(cbz));
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbz, cbr, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_OriginalCannotBeDeletedAndRecordCannotMoveBack_StillRemovesTheCbz()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete a file that is held open without delete sharing.
            return;
        }

        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        _fileStore.Setup(f => f.UpdateFilePathAsync(cbz, cbr, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        MetadataWriteResult result;
        using (new FileStream(cbr, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);
        }

        Assert.Equal(MetadataWriteResult.Failed(cbr), result);
        Assert.True(File.Exists(cbr));
        Assert.False(File.Exists(cbz));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_LibraryRecordCannotBeMoved_UndoesTheConversionSoALaterWriteWorks()
    {
        // A CBZ left behind would become a second library entry and make every later write to
        // the CBR fail with "already exists".
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var before = await File.ReadAllBytesAsync(cbr);
        _fileStore.Setup(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var failed = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteResult.Failed(cbr), failed);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(cbz));
        _fileStore.Verify(f => f.UpdateFilePathAsync(cbz, cbr, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);

        _fileStore.Setup(f => f.UpdateFilePathAsync(cbr, cbz, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var retried = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.ConvertedToCbz, cbz), retried);
        Assert.False(File.Exists(cbr));
        Assert.Equal("New", ReadComicInfo(cbz)!.Element("Series")?.Value);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_CbrThatIsAFileSymlink_KeepsTheEditInTheDatabaseAndTheTargetIntact()
    {
        var service = CreateService();
        var target = WriteCbr("Series/target.cbr", Xml("Old", "3", null));
        var before = await File.ReadAllBytesAsync(target);
        var link = Path.Join(_library, "Series", "alias.cbr");
        if (!TryCreateFileSymlink(link, target))
        {
            return;
        }

        _fileStore.Setup(f => f.GetFileAsync(link, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile { FilePath = link });

        var result = await service.ApplyMetadataEditAsync(link, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.KeptInDatabase, link), result);
        Assert.Equal(before, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(Path.ChangeExtension(target, ".cbz")));
        Assert.False(File.Exists(Path.ChangeExtension(link, ".cbz")));
        Assert.Equal(target, new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)!.FullName);
        Assert.True(_logger.Has(
            LogLevel.Warning,
            "Not converting {FilePath} to CBZ: it is a symbolic link, and converting it would delete the file the link points to"));
        _fileStore.Verify(f => f.UpdateFilePathAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RemoveMetadataAsync_CbrThatIsAFileSymlink_FailsAndLeavesTheTargetIntact()
    {
        var service = CreateService();
        var target = WriteCbr("Series/target.cbr", Xml("Old", "3", null));
        var before = await File.ReadAllBytesAsync(target);
        var link = Path.Join(_library, "Series", "alias.cbr");
        if (!TryCreateFileSymlink(link, target))
        {
            return;
        }

        var result = await service.RemoveMetadataAsync(link);

        Assert.Equal(MetadataWriteResult.Failed(link), result);
        Assert.Equal(before, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(Path.ChangeExtension(target, ".cbz")));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_CbrInALinkedFolder_IsConvertedAndStaysReachable()
    {
        // Only a link on the file itself is refused: a converted file stays reachable through a
        // linked folder, as a renamed one does.
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var alias = Path.Join(_library, "Alias");
        try
        {
            Directory.CreateSymbolicLink(alias, Path.Join(_library, "Series"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating links needs a privilege Windows accounts usually do not have.
            return;
        }

        var result = await service.ApplyMetadataEditAsync(
            Path.Join(alias, "book.cbr"), new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteOutcome.ConvertedToCbz, result.Outcome);
        Assert.False(File.Exists(cbr));
        Assert.True(File.Exists(cbz));
        Assert.True(File.Exists(Path.Join(alias, "book.cbz")));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_Conversion_MovesTheLibraryRecordReadStatusAndProgress()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var provider = services.BuildServiceProvider();
        var fileStore = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<FileStoreService>.Instance,
            provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>(),
            userContext: new TestUserContextAccessor("reader"));
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        await fileStore.AddFileAsync(cbr);
        await fileStore.MarkFileNormalizedAsync(cbr, true);
        await fileStore.MarkFileReadAsync(cbr, true);
        await fileStore.SaveReadingProgressAsync(cbr, 2);
        var service = CreateService(fileStore: fileStore);

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(cbz, result.FilePath);
        Assert.Null(await fileStore.GetFileAsync(cbr));
        var moved = await fileStore.GetFileAsync(cbz);
        Assert.NotNull(moved);
        Assert.True(moved!.IsNormalized);
        Assert.True(Assert.Single(await fileStore.GetAllFilesAsync()).IsRead);
        Assert.Equal(2, await fileStore.GetReadingProgressAsync(cbz));
    }

    [Fact]
    public async Task ProcessFileAsync_CbrWithConversionOn_NormalizesIntoACbzAndRenamesIt()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/Series Ch 5.cbr", Xml(null, null, null));
        var renamed = Path.Join(_library, "Series", "Series - Chapter 0005.cbz");

        Assert.True(await service.ProcessFileAsync(cbr));

        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(cbr));
        Assert.False(File.Exists(Path.ChangeExtension(cbr, ".cbz")));
        var info = ReadComicInfo(renamed)!;
        Assert.Equal("Series", info.Element("Series")?.Value);
        Assert.Equal("5", info.Element("Number")?.Value);
        _fileStore.Verify(
            f => f.UpdateFilePathAsync(Path.ChangeExtension(cbr, ".cbz"), renamed, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task NormalizeAndRenameFilesAsync_CbrWithConversionOn_RenamesTheConvertedCbz()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/Series Ch 7.cbr", Xml(null, null, null));
        var renamed = Path.Join(_library, "Series", "Series - Chapter 0007.cbz");

        var job = await RunJobAsync(service, s => s.NormalizeAndRenameFilesAsync(new[] { cbr }));

        Assert.Equal(0, job.FailedFiles);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(cbr));
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ConversionOff_KeepsTheEditInTheDatabaseAndLeavesTheArchive()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", "A Title"));
        var before = await File.ReadAllBytesAsync(cbr);
        _fileStore.Setup(f => f.GetFileAsync(cbr, It.IsAny<CancellationToken>())).ReturnsAsync(new ComicFile { FilePath = cbr });

        var result = await service.ApplyMetadataEditAsync(
            cbr, new ComicMetadata { Series = "New", Summary = null }, ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Summary);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.KeptInDatabase, cbr), result);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(Path.ChangeExtension(cbr, ".cbz")));
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(
            cbr,
            It.Is<ComicMetadata>(m => m.Series == "New" && m.Issue == "3" && m.Title == "A Title" && m.Summary == null),
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Summary,
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_ConversionOffAndFileNotTracked_Fails()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteResult.Failed(cbr), result);
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(
            It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteMetadataAsync_ConversionOff_DoesNotTouchTheArchiveOrTheDatabase()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var rar = WriteCbr("Series/book.rar", Xml("Old", "3", null));
        var before = await File.ReadAllBytesAsync(rar);

        var result = await service.WriteMetadataAsync(rar, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.NotWritable, rar), result);
        Assert.True(result.Succeeded);
        Assert.Equal(before, await File.ReadAllBytesAsync(rar));
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(
            It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NormalizeFilesAsync_CbrWithConversionOff_LeavesTheArchiveAndTheFileNotNormalized()
    {
        // The normalize could not be written, so the file must not be marked normalized: it
        // would never be normalized once the setting is turned on.
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var cbr = WriteCbr("Series/Series Ch 4.cbr", Xml("Wrong", null, null));
        var before = await File.ReadAllBytesAsync(cbr);

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { cbr }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.False(File.Exists(Path.ChangeExtension(cbr, ".cbz")));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(cbr, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && !e.Success &&
            e.ErrorMessage == ComicProcessorService.NormalizeNotWritableMessage);
    }

    [Fact]
    public async Task NormalizeFilesAsync_CbrWithConversionOff_IsStampedSoTheStaleMetadataPassDoesNotRetryIt()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var cache = CreateCache();
        cache.Setup(c => c.GetAsync("series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord { MetadataVersion = 7 });
        var service = CreateService(cache: cache.Object);
        var cbr = WriteCbr("Series/Series Ch 4.cbr", Xml(null, null, null));

        await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { cbr }));

        _fileStore.Verify(f => f.SetFileSeriesMetadataVersionAsync(cbr, 7, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(cbr, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeAndRenameFilesAsync_CbrWithConversionOff_RenamesButDoesNotMarkItNormalized()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var cbr = WriteCbr("Series/Series Ch 7.cbr", Xml("Series", "7", null));
        var before = await File.ReadAllBytesAsync(cbr);
        var renamed = Path.Join(_library, "Series", "Series - Chapter 0007.cbr");

        var job = await RunJobAsync(service, s => s.NormalizeAndRenameFilesAsync(new[] { cbr }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Equal(before, await File.ReadAllBytesAsync(renamed));
        Assert.False(File.Exists(cbr));
        _fileStore.Verify(f => f.MarkFileNormalizedAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessFileAsync_CbrWithConversionOff_StaysUnprocessedUntilConversionIsTurnedOn()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var provider = services.BuildServiceProvider();
        var fileStore = new FileStoreService(
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<FileStoreService>.Instance,
            provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>());
        _settings.ConvertRarToCbzOnWrite = false;
        var cbr = WriteCbr("Series/Series Ch 4.cbr", Xml(null, null, null));
        var before = await File.ReadAllBytesAsync(cbr);
        await fileStore.AddFileAsync(cbr);
        var service = CreateService(fileStore: fileStore);

        // Off: the archive is renamed (with the normalized values) but never rewritten, and the
        // file shows as unprocessed.
        Assert.True(await service.ProcessFileAsync(cbr));

        var renamedCbr = Path.Join(_library, "Series", "Series - Chapter 0004.cbr");
        Assert.Equal(before, await File.ReadAllBytesAsync(renamedCbr));
        var pending = await fileStore.GetFileAsync(renamedCbr);
        Assert.NotNull(pending);
        Assert.True(pending!.IsRenamed);
        Assert.False(pending.IsNormalized);
        Assert.Contains(await fileStore.GetFilteredFilesAsync("unprocessed"), f => f.FilePath == renamedCbr);

        // On: the next processing run normalizes it into a CBZ.
        _settings.ConvertRarToCbzOnWrite = true;
        Assert.True(await service.ProcessFileAsync(renamedCbr));

        var cbz = Path.ChangeExtension(renamedCbr, ".cbz");
        Assert.False(File.Exists(renamedCbr));
        var info = ReadComicInfo(cbz)!;
        Assert.Equal("Series", info.Element("Series")?.Value);
        Assert.Equal("4", info.Element("Number")?.Value);
        var processed = await fileStore.GetFileAsync(cbz);
        Assert.NotNull(processed);
        Assert.True(processed!.IsNormalized);
        Assert.True(processed.IsProcessed);
    }

    [Fact]
    public async Task RemoveMetadataAsync_CbrWithConversionOff_FailsWithoutTouchingIt()
    {
        _settings.ConvertRarToCbzOnWrite = false;
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var before = await File.ReadAllBytesAsync(cbr);

        var result = await service.RemoveMetadataAsync(cbr);

        Assert.Equal(MetadataWriteResult.Failed(cbr), result);
        Assert.Equal(before, await File.ReadAllBytesAsync(cbr));
        Assert.True(_logger.Has(
            LogLevel.Warning,
            "Cannot remove metadata from {FilePath}: this CBR/RAR/ZIP archive is not converted to CBZ, so it is not rewritten"));
    }

    [Fact]
    public async Task RemoveMetadataAsync_CbrWithConversionOn_ConvertsToACbzWithoutComicInfo()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");

        var result = await service.RemoveMetadataAsync(cbr);

        Assert.Equal(new MetadataWriteResult(MetadataWriteOutcome.ConvertedToCbz, cbz), result);
        Assert.Equal(new[] { "pages/page001.jpg", "page002.jpg" }, EntryNames(cbz));
        Assert.False(File.Exists(cbr));
    }

    [Fact]
    public async Task GetMetadataAsync_CbrWithAnEditKeptInTheDatabase_ShowsTheStoredValues()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Archive Series", "3", "Archive Title"));
        _fileStore.Setup(f => f.GetFileAsync(cbr, It.IsAny<CancellationToken>())).ReturnsAsync(PendingEdit(
            cbr, new ComicMetadata { Series = "Edited Series", Title = "Stale", Summary = null },
            ComicMetadataFieldFlags.Series));

        var metadata = await service.GetMetadataAsync(cbr);
        var seriesMetadata = await service.GetSeriesMetadataAsync(cbr);

        Assert.Equal("Edited Series", metadata!.Series);
        Assert.Equal("Archive Title", metadata.Title);
        Assert.Equal("3", metadata.Issue);
        Assert.Equal("Edited Series", seriesMetadata!.Series);
        Assert.Equal("Archive Title", seriesMetadata.Title);
    }

    [Fact]
    public async Task GetMetadataAsync_EditAlreadyWritten_ReadsTheArchive()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Archive Series", "3", null));
        var file = PendingEdit(cbr, new ComicMetadata { Series = "Edited Series" }, ComicMetadataFieldFlags.Series);
        file.WrittenMetadataVersion = file.MetadataVersion;
        _fileStore.Setup(f => f.GetFileAsync(cbr, It.IsAny<CancellationToken>())).ReturnsAsync(file);

        Assert.Equal("Archive Series", (await service.GetMetadataAsync(cbr))!.Series);
    }

    [Fact]
    public async Task GetMetadataAsync_Cbz_IsNeverOverlaidWithStoredValues()
    {
        var service = CreateService();
        var cbz = WriteCbz("Series/book.cbz", Xml("Archive Series", "3", null));
        _fileStore.Setup(f => f.GetFileAsync(cbz, It.IsAny<CancellationToken>())).ReturnsAsync(PendingEdit(
            cbz, new ComicMetadata { Series = "Edited Series" }, ComicMetadataFieldFlags.Series));

        Assert.Equal("Archive Series", (await service.GetMetadataAsync(cbz))!.Series);
        _fileStore.Verify(f => f.GetFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_Conversion_CarriesEditsKeptInTheDatabaseIntoTheCbz()
    {
        var service = CreateService();
        var cbr = WriteCbr("Series/book.cbr", Xml("Old", "3", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var pending = PendingEdit(
            cbr,
            new ComicMetadata { Series = "Kept Series", Publisher = "Kept Publisher" },
            ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Publisher);
        _fileStore.Setup(f => f.GetFileAsync(cbr, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        var result = await service.ApplyMetadataEditAsync(cbr, new ComicMetadata { Series = "Newest" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(cbz, result.FilePath);
        var info = ReadComicInfo(cbz)!;
        Assert.Equal("Newest", info.Element("Series")?.Value);
        Assert.Equal("Kept Publisher", info.Element("Publisher")?.Value);
        Assert.Equal("3", info.Element("Number")?.Value);

        // The stored Series is brought in line with what the user just wrote, and the edits are
        // no longer pending.
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(
            cbz, It.Is<ComicMetadata>(m => m.Series == "Newest"), ComicMetadataFieldFlags.Series, false, It.IsAny<CancellationToken>()), Times.Once);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(cbz, pending.MetadataVersion, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NormalizeFilesAsync_Conversion_KeepsALockedSeriesTheUserSetInTheDatabase()
    {
        var service = CreateService();
        var cbr = WriteCbr("Folder Series/Folder Series Ch 2.cbr", Xml("Old", "2", null));
        var cbz = Path.ChangeExtension(cbr, ".cbz");
        var pending = PendingEdit(cbr, new ComicMetadata { Series = "User Series" }, ComicMetadataFieldFlags.Series);
        _fileStore.Setup(f => f.GetFileAsync(cbr, It.IsAny<CancellationToken>())).ReturnsAsync(pending);

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { cbr }));

        Assert.Equal(0, job.FailedFiles);
        var info = ReadComicInfo(cbz)!;
        Assert.Equal("User Series", info.Element("Series")?.Value);
        Assert.Equal("Chapter 2", info.Element("Title")?.Value);
        _fileStore.Verify(f => f.RecordUserMetadataEditAsync(
            It.IsAny<string>(), It.IsAny<ComicMetadata>(), It.IsAny<ComicMetadataFieldFlags>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _fileStore.Verify(f => f.MarkFileBackfilledAsync(cbz, pending.MetadataVersion, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A tracked file whose <paramref name="fields"/> were edited in the database but not written.</summary>
    private static ComicFile PendingEdit(string path, ComicMetadata stored, ComicMetadataFieldFlags fields)
    {
        stored.IsUserEdited = true;
        stored.UserLockedFieldsMask = (long)fields;
        return new ComicFile { FilePath = path, Metadata = stored, MetadataVersion = 3, WrittenMetadataVersion = 1 };
    }

    /// <summary>
    /// A RAR 4 archive (stored entries) holding a page in a folder written with a Windows
    /// separator, a root page and, when given, ComicInfo.xml; <paramref name="solid"/> marks the
    /// whole archive solid.
    /// </summary>
    private string WriteCbr(string relativePath, string? comicInfoXml, bool solid = false)
    {
        var path = Path.GetFullPath(Path.Join(_library, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new List<(string Name, byte[]? Data)>
        {
            ("pages\\page001.jpg", RarPage),
            ("page002.jpg", Encoding.ASCII.GetBytes("jpg page two"))
        };
        if (comicInfoXml != null)
        {
            entries.Add(("ComicInfo.xml", Encoding.UTF8.GetBytes(comicInfoXml)));
        }

        StoredRar4.Write(path, entries.ToArray());
        if (solid)
        {
            // The main archive header follows the 7-byte signature: CRC16, type, flags, size and
            // six reserved bytes. 0x0008 is the solid-archive flag; the CRC covers type to end.
            var bytes = File.ReadAllBytes(path);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)(flags | 0x0008));
            var crc = (ushort)(StoredRar4.Crc32(bytes.AsSpan(9, 11)) & 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(7), crc);
            File.WriteAllBytes(path, bytes);
        }

        return path;
    }

    /// <summary>
    /// Creates a file symlink; false where it is not allowed (creating links needs a privilege
    /// Windows accounts usually do not have), so the test has nothing to check there.
    /// </summary>
    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] ReadZipEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
