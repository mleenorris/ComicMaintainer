using System.IO.Compression;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the failure path of <see cref="ComicArchive.WriteTags"/> when the original archive
/// can no longer be replaced, and the "Archive not opened" guards that follow it.
/// </summary>
public class ComicArchiveCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "carc-cov-" + Guid.NewGuid().ToString("N"));

    public ComicArchiveCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void WriteTags_OriginalPathTurnedIntoDirectory_FailsAndClosesArchive()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Path.Combine(_root, "book.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var info = zip.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(info.Open()))
            {
                writer.Write("<ComicInfo><Series>Old</Series></ComicInfo>");
            }

            var page = zip.CreateEntry("page001.jpg");
            using var stream = page.Open();
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        var archive = new ComicArchive(path);
        Assert.Equal("Old", archive.ReadTags()!.Series);

        // The archive keeps its open handle to the unlinked file, but the path itself can no
        // longer be deleted and replaced once a directory occupies it.
        File.Delete(path);
        Directory.CreateDirectory(path);

        var ex = Assert.Throws<InvalidOperationException>(() => archive.WriteTags(new ComicInfo { Series = "New" }));

        Assert.StartsWith("Failed to write tags to archive", ex.Message);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
        Assert.True(Directory.Exists(path));

        var readEx = Assert.Throws<InvalidOperationException>(() => archive.ReadTags());
        Assert.Equal("Archive not opened", readEx.Message);
        var writeEx = Assert.Throws<InvalidOperationException>(() => archive.WriteTags(new ComicInfo()));
        Assert.Equal("Archive not opened", writeEx.Message);

        archive.Dispose();
        archive.Dispose();
    }
}
