using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private const int LargeEntrySize = 32 * 1024 * 1024;
    private static readonly byte[] PageBytes = [1, 2, 3, 4];
    private static readonly byte[] NewCover = [9, 8, 7, 6, 5];
    private static readonly DateTimeOffset FixtureTimestamp = new(2021, 3, 4, 5, 6, 8, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _source;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IFileWatcherService> _watcher = new();
    private readonly AppSettings _settings = new() { WriteCoverToFirstArchive = true };

    public SeriesArchiveCoverWriterCoverageTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cm-archive-cover-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Join(_root, "source-cover.jpg");
        File.WriteAllBytes(_source, NewCover);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
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
        var path = source.Length > 1 ? Path.Join(_root, source) : source;

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
        var cbz = Path.Join(_root, "broken.cbz");
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
        var cbz = Path.Join(_root, "issue1.cbz");
        using (var archive = SharpZipArchive.CreateArchive())
        {
            using var page = new MemoryStream(PageBytes);
            using var emptyNamePage = new MemoryStream([7, 7]);
            archive.AddEntry("01.jpg", page, true, PageBytes.Length, DateTime.Now);
            archive.AddEntry("", emptyNamePage, true, 2, DateTime.Now);
            using var stream = File.Create(cbz);
            archive.SaveTo(stream, new ZipWriterOptions(CompressionType.Deflate));
        }
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/png");

        using var result = SharpZipArchive.OpenArchive(cbz);
        Assert.Equal(["", "01.jpg", "cover.png"], result.Entries.Select(e => e.Key ?? "<null>").Order());
    }

    // ---- streaming rewrite -------------------------------------------------------------

    // Regression for #867: every entry used to be buffered in a MemoryStream before the new
    // archive was written, so a rewrite allocated more than the archive's uncompressed size.
    [Fact]
    public void RewriteArchive_LargeEntry_AllocatesFarLessThanTheEntrySize()
    {
        var cbz = Path.Join(_root, "issue1.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            // Zeros, so the archive itself stays tiny. Written in small chunks so the fixture
            // doesn't need a LargeEntrySize buffer.
            var entry = zip.CreateEntry("big.bin", CompressionLevel.Fastest);
            entry.LastWriteTime = FixtureTimestamp;
            using var stream = entry.Open();
            var chunk = new byte[81920];
            for (var remaining = LargeEntrySize; remaining > 0; remaining -= chunk.Length)
            {
                stream.Write(chunk, 0, Math.Min(chunk.Length, remaining));
            }
        }

        // Calling RewriteArchive directly keeps the whole rewrite on this thread, so
        // GC.GetAllocatedBytesForCurrentThread sees all of it; WriteAsync can resume on a
        // pool thread after reading the source cover.
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        SeriesArchiveCoverWriter.RewriteArchive(cbz, "cover.jpg", NewCover);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(
            allocated < LargeEntrySize / 4,
            $"Rewriting an archive with a {LargeEntrySize:N0} byte entry allocated {allocated:N0} bytes.");
        Assert.Equal(["big.bin", "cover.jpg"], ListEntries(cbz));
        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_ArchiveWithLargeEntry_KeepsEveryOtherEntryByteForByte()
    {
        var cbz = Path.Join(_root, "issue1.cbz");
        var big = new byte[3 * 1024 * 1024];
        new Random(867).NextBytes(big);
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            AddEntry(zip, "01.jpg", PageBytes, FixtureTimestamp);
            AddEntry(zip, "cover.png", [3, 3], FixtureTimestamp.AddDays(1));
            AddEntry(zip, "02.jpg", big, FixtureTimestamp.AddDays(2));
            AddEntry(zip, "ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo />"), FixtureTimestamp.AddDays(3));
        }
        var before = SnapshotEntries(cbz);
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        var after = SnapshotEntries(cbz);
        Assert.Equal(before.Where(e => e.Name != "cover.png"), after.SkipLast(1));
        Assert.Equal("cover.jpg", after[^1].Name);
        Assert.Equal(NewCover, ReadEntry(cbz, "cover.jpg"));
    }

    [Fact]
    public async Task WriteAsync_EntryFailsMidStream_LeavesArchiveUntouchedAndRemovesTempFile()
    {
        // Relabelled as deflate, the stored 02.jpg starts with 0xFF (a reserved block type), so
        // it fails while being copied, after 01.jpg was already written to the temp file.
        var cbz = CreateCbz("issue1.cbz", ("01.jpg", PageBytes), ("02.jpg", [0xFF, 0xD8, 0xFF, 0xE0]));
        PatchCompressionMethod(cbz, "02.jpg", 8);
        var before = File.ReadAllBytes(cbz);
        SetupFirstArchive(cbz);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal(before, File.ReadAllBytes(cbz));
        Assert.False(File.Exists(cbz + ".cover.tmp"));
        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
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
        SetupFirstArchive(archive is { Length: > 0 } ? Path.Join(_root, archive) : archive);

        await CreateWriter().RemoveAsync(Key);

        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_ReadOnlyArchiveFormat_IsSkipped()
    {
        var cbr = Path.Join(_root, "issue1.cbr");
        File.WriteAllBytes(cbr, [0x52, 0x61, 0x72, 0x21]);
        SetupFirstArchive(cbr);

        await CreateWriter().RemoveAsync(Key);

        Assert.Equal([0x52, 0x61, 0x72, 0x21], File.ReadAllBytes(cbr));
        _watcher.Verify(w => w.SuppressProcessing(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_CorruptArchive_IsSwallowed()
    {
        var cbz = Path.Join(_root, "broken.cbz");
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
        var path = Path.Join(_root, name);
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

    private static void AddEntry(ZipArchive zip, string name, byte[] content, DateTimeOffset lastWriteTime)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = lastWriteTime;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static List<EntrySnapshot> SnapshotEntries(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        return zip.Entries
            .Select(e =>
            {
                using var stream = e.Open();
                return new EntrySnapshot(e.FullName, e.LastWriteTime.DateTime, e.Length, Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToList();
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

    private sealed record EntrySnapshot(string Name, DateTime LastWriteTime, long Length, string Sha256);
}
