using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using SharpZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace ComicMaintainer.Tests.Services;

public class SeriesArchiveCoverWriterCoverageTests : IDisposable
{
    private const string Key = "series-a";
    private static readonly byte[] PageBytes = [1, 2, 3, 4];
    private static readonly byte[] NewCover = [9, 8, 7, 6, 5];

    private readonly string _root;
    private readonly string _source;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IFileWatcherService> _watcher = new();
    private readonly AppSettings _settings = new() { WriteCoverToFirstArchive = true };

    public SeriesArchiveCoverWriterCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm-archive-cover-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source-cover.jpg");
        File.WriteAllBytes(_source, NewCover);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // ---- WriteAsync guards -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WriteAsync_BlankKey_DoesNothing(string key)
    {
        await CreateWriter().WriteAsync(key, _source, "image/jpeg");

        VerifyLibraryNeverQueried();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.jpg")]
    public async Task WriteAsync_BlankOrMissingSource_DoesNothing(string source)
    {
        var path = source.Length > 1 ? Path.Combine(_root, source) : source;

        await CreateWriter().WriteAsync(Key, path, "image/jpeg");

        VerifyLibraryNeverQueried();
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task WriteAsync_UnsupportedContentType_DoesNothing(string? contentType)
    {
        await CreateWriter().WriteAsync(Key, _source, contentType!);

        VerifyLibraryNeverQueried();
    }

    [Fact]
    public async Task WriteAsync_LibraryThrows_IsSwallowed()
    {
        _library
            .Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("library unavailable"));

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WriteAsync_SourceDisappearsBeforeRead_LeavesArchiveUntouched()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes));
        var before = File.ReadAllBytes(cbz);
        _library
            .Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                File.Delete(_source);
                return cbz;
            });

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(before, File.ReadAllBytes(cbz));
        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WriteAsync_CorruptArchive_IsLoggedAndLeftUntouched()
    {
        var cbz = Path.Combine(_root, "broken.cbz");
        var garbage = Encoding.ASCII.GetBytes("this is definitely not a zip archive");
        File.WriteAllBytes(cbz, garbage);
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(garbage, File.ReadAllBytes(cbz));
        Assert.False(File.Exists(cbz + ".cover.tmp"));
        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WriteAsync_CanceledToken_ThrowsWithoutRewriting()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes));
        var before = File.ReadAllBytes(cbz);
        SetupFirstArchive(cbz);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateWriter().WriteAsync(Key, _source, "image/jpeg", cancellationToken: cts.Token));

        Assert.Equal(before, File.ReadAllBytes(cbz));
    }

    // ---- watcher suppression -------------------------------------------------------

    [Fact]
    public async Task WriteAsync_NoWatcherRegistered_StillEmbedsCover()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes));
        SetupFirstArchive(cbz);

        await CreateWriter(registerWatcher: false).WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_WatcherThrows_StillEmbedsCover()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes));
        SetupFirstArchive(cbz);
        _watcher
            .Setup(w => w.SuppressProcessing(It.IsAny<string>()))
            .Throws(new InvalidOperationException("watcher offline"));

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
        _watcher.Verify(w => w.SuppressProcessing(cbz), Times.Exactly(2));
    }

    [Fact]
    public async Task WriteAsync_ReplaceFails_RemovesTempFileAndSwallowsError()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes));
        SetupFirstArchive(cbz);
        // Swap the archive for a directory just before the atomic move so the
        // move into place fails.
        _watcher
            .Setup(w => w.SuppressProcessing(cbz))
            .Callback(() =>
            {
                if (File.Exists(cbz))
                {
                    File.Delete(cbz);
                    Directory.CreateDirectory(cbz);
                }
            });

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.True(Directory.Exists(cbz));
        Assert.False(File.Exists(cbz + ".cover.tmp"));
        _watcher.Verify(w => w.SuppressProcessing(cbz), Times.Once);
    }

    // ---- idempotency checks ---------------------------------------------------------

    [Fact]
    public async Task WriteAsync_SameNamedCoverWithDifferentSize_IsReplaced()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes), ("cover.jpg", [1, 2, 3]));
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
        Assert.Equal(PageBytes, ReadEntry(cbz, "01.jpg"));
        _watcher.Verify(w => w.SuppressProcessing(cbz), Times.Exactly(2));
    }

    [Fact]
    public async Task WriteAsync_SameNamedCoverWithSameSizeButDifferentBytes_IsReplaced()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes), ("COVER.JPG", [5, 6, 7, 8, 9]));
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(["01.jpg", "cover.jpg"], ListEntries(cbz).Order());
        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_UnreadableExistingCover_IsReplaced()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes), ("cover.jpg", [5, 6, 7, 8, 9]));
        // Compression method 11 is reserved, so opening the cover entry throws.
        PatchCompressionMethod(cbz, "cover.jpg", 11);
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
        Assert.Equal(PageBytes, ReadEntry(cbz, "01.jpg"));
    }

    [Fact]
    public async Task WriteAsync_EntryWithEmptyName_IsPreserved()
    {
        var cbz = Path.Combine(_root, "issue1.cbz");
        using (var archive = SharpZipArchive.CreateArchive())
        {
            archive.AddEntry("01.jpg", new MemoryStream(PageBytes), true, PageBytes.Length, DateTime.Now);
            archive.AddEntry("", new MemoryStream([7, 7]), true, 2, DateTime.Now);
            using var stream = File.Create(cbz);
            archive.SaveTo(stream, new ZipWriterOptions(CompressionType.Deflate));
        }
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/png");

        using var result = SharpZipArchive.OpenArchive(cbz);
        Assert.Equal(["", "01.jpg", "cover.png"], result.Entries.Select(e => e.Key ?? "<null>").Order());
    }

    // ---- RemoveAsync -----------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    public async Task RemoveAsync_BlankKey_DoesNothing(string key)
    {
        await CreateWriter().RemoveAsync(key);

        VerifyLibraryNeverQueried();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing.cbz")]
    public async Task RemoveAsync_NoUsableFirstArchive_DoesNothing(string? archive)
    {
        SetupFirstArchive(archive is { Length: > 0 } ? Path.Combine(_root, archive) : archive);

        await CreateWriter().RemoveAsync(Key);

        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_ReadOnlyArchiveFormat_IsSkipped()
    {
        var cbr = Path.Combine(_root, "issue1.cbr");
        File.WriteAllBytes(cbr, [0x52, 0x61, 0x72, 0x21]);
        SetupFirstArchive(cbr);

        await CreateWriter().RemoveAsync(Key);

        Assert.Equal([0x52, 0x61, 0x72, 0x21], File.ReadAllBytes(cbr));
        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_CorruptArchive_IsSwallowed()
    {
        var cbz = Path.Combine(_root, "broken.cbz");
        var garbage = Encoding.ASCII.GetBytes("still not a zip archive");
        File.WriteAllBytes(cbz, garbage);
        SetupFirstArchive(cbz);

        await CreateWriter().RemoveAsync(Key);

        Assert.Equal(garbage, File.ReadAllBytes(cbz));
    }

    [Fact]
    public async Task RemoveAsync_LibraryThrows_IsSwallowed()
    {
        _library
            .Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("library unavailable"));

        await CreateWriter().RemoveAsync(Key);

        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_CanceledToken_ThrowsWithoutRewriting()
    {
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes), ("cover.png", [3, 3]));
        SetupFirstArchive(cbz);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateWriter().RemoveAsync(Key, cts.Token));

        Assert.Equal(["01.jpg", "cover.png"], ListEntries(cbz).Order());
    }

    [Fact]
    public async Task RemoveAsync_RemovesEveryManagedCoverVariant()
    {
        var cbz = CreateCbz(
            "issue1.cbz",
            ("01.jpg", PageBytes),
            ("cover.jpg", [1]),
            ("Cover.PNG", [2]),
            ("cover.webp", [3]),
            ("cover.gif", [4]));
        SetupFirstArchive(cbz);

        await CreateWriter().RemoveAsync(Key);

        Assert.Equal(["01.jpg", "cover.gif"], ListEntries(cbz).Order());
        _watcher.Verify(w => w.SuppressProcessing(cbz), Times.Exactly(2));
    }

    // ---- helpers -----------------------------------------------------------------------

    private SeriesArchiveCoverWriter CreateWriter(bool registerWatcher = true)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _library.Object);
        if (registerWatcher)
        {
            services.AddScoped(_ => _watcher.Object);
        }
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(_settings);
        return new SeriesArchiveCoverWriter(scopeFactory, options.Object, Mock.Of<ILogger<SeriesArchiveCoverWriter>>());
    }

    private void SetupFirstArchive(string? path)
    {
        _library
            .Setup(l => l.GetFirstIssueFilePathForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(path);
    }

    private void VerifyLibraryNeverQueried()
    {
        _library.Verify(
            l => l.GetFirstIssueFilePathForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private string CreateCbz(string name, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(data);
        }
        return path;
    }

    private static List<string> ListEntries(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static byte[] ReadEntry(string cbzPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        using var stream = zip.GetEntry(entryName)!.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Rewrites the compression method of <paramref name="entryName"/> in both
    /// its local file header and its central directory record.
    /// </summary>
    private static void PatchCompressionMethod(string zipPath, string entryName, ushort method)
    {
        const uint LocalHeaderSignature = 0x04034b50;
        const uint CentralHeaderSignature = 0x02014b50;
        var bytes = File.ReadAllBytes(zipPath);
        var name = Encoding.UTF8.GetBytes(entryName);
        var patched = 0;
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == LocalHeaderSignature)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26));
                if (i + 30 + nameLength <= bytes.Length && bytes.AsSpan(i + 30, nameLength).SequenceEqual(name))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i + 8), method);
                    patched++;
                }
            }
            else if (signature == CentralHeaderSignature)
            {
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
                if (i + 46 + nameLength <= bytes.Length && bytes.AsSpan(i + 46, nameLength).SequenceEqual(name))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i + 10), method);
                    patched++;
                }
            }
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(zipPath, bytes);
    }
}
