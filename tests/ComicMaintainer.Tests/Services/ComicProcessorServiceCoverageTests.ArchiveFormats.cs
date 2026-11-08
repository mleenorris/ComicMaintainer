using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using StoredRar4 = ComicMaintainer.Tests.Services.ComicReaderServiceCoverageTests.StoredRar4;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Tagging through the shared archive reader: what a file is depends on its content, the
/// ComicInfo.xml rules are the reader's, and a refusal by the archive limits reaches the job
/// result and the processing history instead of a generic message.
/// </summary>
public partial class ComicProcessorServiceCoverageTests
{
    private const string OverLimitReasonFragment = "over the 8 MiB limit";

    private string WriteZipNamed(string relativePath, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.GetFullPath(Path.Join(_library, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            stream.Write(data);
        }

        return path;
    }

    private string WriteRarNamed(string relativePath, params (string Name, byte[]? Data)[] entries)
    {
        var path = Path.GetFullPath(Path.Join(_library, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StoredRar4.Write(path, entries);
        return path;
    }

    /// <summary>An archive refused by the read limits: its ComicInfo.xml is over 8 MiB.</summary>
    private string WriteCbzWithHugeComicInfo(string relativePath) =>
        WriteZipNamed(
            relativePath,
            ("page001.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }),
            ("ComicInfo.xml", new byte[9 * 1024 * 1024]));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task GetMetadataAsync_ZipNamedCbr_ReadsItsComicInfo()
    {
        var service = CreateService();
        var path = WriteZipNamed("Series/book.cbr", ("ComicInfo.xml", Utf8(Xml("From Zip", "1", null))));

        var metadata = await service.GetMetadataAsync(path);

        Assert.Equal("From Zip", metadata!.Series);
    }

    [Fact]
    public async Task GetMetadataAsync_RarNamedCbz_ReadsItsComicInfo()
    {
        var service = CreateService();
        var path = WriteRarNamed("Series/book.cbz", ("ComicInfo.xml", Utf8(Xml("From Rar", "1", null))));

        var metadata = await service.GetMetadataAsync(path);

        Assert.Equal("From Rar", metadata!.Series);
    }

    [Fact]
    public async Task GetMetadataAsync_NestedComicInfo_IsRead()
    {
        // Conversion has always honoured a ComicInfo.xml in a folder; tagging did not, so the same
        // file had different metadata in different features.
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/nested.cbz",
            ("Book/ComicInfo.xml", Utf8(Xml("Nested", "1", null))),
            ("Book/page001.jpg", new byte[] { 1, 2, 3 }));

        var metadata = await service.GetMetadataAsync(path);

        Assert.Equal("Nested", metadata!.Series);
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_RarNamedCbz_BecomesARealCbzInPlace()
    {
        var service = CreateService();
        var path = WriteRarNamed(
            "Series/book.cbz",
            ("page001.jpg", new byte[] { 1, 2, 3 }),
            ("ComicInfo.xml", Utf8(Xml("Old", "1", null))));

        var result = await service.ApplyMetadataEditAsync(path, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.Equal(MetadataWriteOutcome.Written, result.Outcome);
        Assert.Equal("New", ReadComicInfo(path)!.Element("Series")?.Value);
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "page001.jpg");
    }

    [Fact]
    public async Task ApplyMetadataEditAsync_NestedComicInfo_ReplacesItWithOneAtTheRoot()
    {
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/nested.cbz",
            ("Book/ComicInfo.xml", Utf8(Xml("Old", "1", null))),
            ("Book/page001.jpg", new byte[] { 1, 2, 3 }),
            ("Other/ComicInfo.xml", Utf8(Xml("Other", "2", null))));

        var result = await service.ApplyMetadataEditAsync(path, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series);

        Assert.True(result.Succeeded);
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("ComicInfo.xml", names);
        Assert.DoesNotContain("Book/ComicInfo.xml", names);
        // A ComicInfo.xml it did not read stays: the write only owns the one it replaced.
        Assert.Contains("Other/ComicInfo.xml", names);
        Assert.Equal("New", ReadComicInfo(path)!.Element("Series")?.Value);
    }

    [Fact]
    public async Task RemoveMetadataAsync_DropsComicInfoAtAnyDepth()
    {
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/nested.cbz",
            ("ComicInfo.xml", Utf8(Xml("Old", "1", null))),
            ("Book/ComicInfo.xml", Utf8(Xml("Nested", "2", null))),
            ("Book/page001.jpg", new byte[] { 1, 2, 3 }));

        var result = await service.RemoveMetadataAsync(path);

        Assert.True(result.Succeeded);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(new[] { "Book/page001.jpg" }, zip.Entries.Select(e => e.FullName));
    }

    // ---- Refusals reach the job result and history -----------------------------------------

    [Fact]
    public async Task ProcessFilesAsync_ArchiveOverTheReadLimits_ReportsTheSpecificReason()
    {
        var service = CreateService();
        var path = WriteCbzWithHugeComicInfo("Series/huge.cbz");

        var job = await RunJobAsync(service, s => s.ProcessFilesAsync(new[] { path }, forceReprocess: true));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains(OverLimitReasonFragment, job.Errors[path]);
        Assert.Contains("ComicInfo.xml", job.Errors[path]);
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && !e.Success && e.ErrorMessage == job.Errors[path]);
        Assert.Contains(_historyEntries, e => e.Action == "Process" && !e.Success && e.ErrorMessage == job.Errors[path]);
        Assert.True(_logger.Has(LogLevel.Warning, "Refused to read {FilePath}: {Reason}"));
    }

    [Fact]
    public async Task NormalizeFilesAsync_ArchiveOverTheReadLimits_ReportsTheSpecificReason()
    {
        var service = CreateService();
        var path = WriteCbzWithHugeComicInfo("Series/huge.cbz");

        var job = await RunJobAsync(service, s => s.NormalizeFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains(OverLimitReasonFragment, job.Errors[path]);
        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && e.ErrorMessage == job.Errors[path]);
    }

    [Fact]
    public async Task RenameFilesAsync_ArchiveOverTheReadLimits_ReportsTheSpecificReason()
    {
        var service = CreateService();
        var path = WriteCbzWithHugeComicInfo("Series/huge.cbz");

        var job = await RunJobAsync(service, s => s.RenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains(OverLimitReasonFragment, job.Errors[path]);
        Assert.Contains(_historyEntries, e => e.Action == "Rename" && e.ErrorMessage == job.Errors[path]);
    }

    [Fact]
    public async Task UpdateMetadataAsync_ArchiveRefusedOnRewrite_FailsThatFileWithTheReason()
    {
        // A 32 MiB entry of zeros expands far more than 100:1, so the rewrite refuses it. The
        // ComicInfo.xml is fine, so the read before the rewrite succeeds.
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/bomb.cbz",
            ("ComicInfo.xml", Utf8(Xml("Old", "1", null))),
            ("page001.jpg", new byte[32 * 1024 * 1024]));
        var before = await File.ReadAllBytesAsync(path);

        var job = await RunJobAsync(
            service,
            s => s.UpdateMetadataAsync(new[] { path }, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains("more than 100:1", job.Errors[path]);
        Assert.Contains("page001.jpg", job.Errors[path]);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(TempFilesNextTo(path));
    }

    [Fact]
    public async Task ProcessFileAsync_ArchiveRefusedOnRewrite_ShowsTheReasonInHistory()
    {
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/bomb.cbz",
            ("ComicInfo.xml", Utf8(Xml(null, "1", null))),
            ("page001.jpg", new byte[32 * 1024 * 1024]));

        await service.ProcessFileAsync(path, forceReprocess: true);

        Assert.Contains(_historyEntries, e => e.Action == "Normalize" && !e.Success && e.ErrorMessage!.Contains("more than 100:1"));
        Assert.Contains(_historyEntries, e => e.Action == "Process" && !e.Success && e.ErrorMessage!.Contains("more than 100:1"));
        Assert.DoesNotContain(_historyEntries, e => e.ErrorMessage == "Failed to update metadata");
    }

    [Fact]
    public async Task RemoveMetadataFromFilesAsync_RefusedRewrite_ReportsTheReason()
    {
        var service = CreateService();
        var path = WriteZipNamed(
            "Series/bomb.cbz",
            ("ComicInfo.xml", Utf8(Xml("Old", "1", null))),
            ("page001.jpg", new byte[32 * 1024 * 1024]));
        var before = await File.ReadAllBytesAsync(path);

        var job = await RunJobAsync(service, s => s.RemoveMetadataFromFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains("more than 100:1", job.Errors[path]);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True(_logger.Has(LogLevel.Warning, "Refused to read {FilePath}: {Reason}"));
    }

    [Fact]
    public async Task NormalizeAndRenameFilesAsync_ArchiveOverTheLimits_ReportsTheSpecificReason()
    {
        var service = CreateService();
        var path = WriteCbzWithHugeComicInfo("Series/huge.cbz");

        var job = await RunJobAsync(service, s => s.NormalizeAndRenameFilesAsync(new[] { path }));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains(OverLimitReasonFragment, job.Errors[path]);
    }

    [Fact]
    public async Task UpdateMetadataAsync_MalformedComicInfo_ReportsWhyItWasLeftAlone()
    {
        var service = CreateService();
        var path = WriteZipNamed("Series/bad.cbz", ("ComicInfo.xml", Utf8("<ComicInfo")));

        var job = await RunJobAsync(
            service,
            s => s.UpdateMetadataAsync(new[] { path }, new ComicMetadata { Series = "New" }, ComicMetadataFieldFlags.Series));

        Assert.Equal(1, job.FailedFiles);
        Assert.Contains("malformed", job.Errors[path]);
    }
}
