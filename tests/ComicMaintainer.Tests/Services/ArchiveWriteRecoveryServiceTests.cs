using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The startup sweep after interrupted archive writes: unfinished temp archives are deleted, a
/// backup is restored only over a missing or unreadable archive and only when the backup itself
/// can be read, and a redundant backup of an intact archive is removed.
/// </summary>
public sealed class ArchiveWriteRecoveryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddMinutes(-10);

    private readonly string _root;
    private readonly string _library;
    private readonly string _temp;
    private readonly AppSettings _settings;

    public ArchiveWriteRecoveryServiceTests()
    {
        _root = Path.Join(Path.GetTempPath(), "archive-recovery-" + Guid.NewGuid().ToString("N"));
        _library = Path.Join(_root, "library");
        _temp = Path.Join(_root, "temp");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_temp);
        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = _temp
        };
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    [Fact]
    public async Task RecoverAsync_DeletesUnfinishedTempArchivesButNotOtherDotFiles()
    {
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var temp = Stale(ArchiveWriteRecoveryService.CreateTempPath(series), "half-written");
        var suwayomiStaging = Stale(Path.Join(series, ".tmp_" + Guid.NewGuid().ToString("N") + ".suwayomi-import"), "staged");
        var userFile = Stale(Path.Join(series, ".tmp_notes.txt"), "mine");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(suwayomiStaging));
        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public async Task RecoverAsync_KeepsATempArchiveWrittenAfterTheSweepStarted()
    {
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var inProgress = ArchiveWriteRecoveryService.CreateTempPath(series);
        await File.WriteAllTextAsync(inProgress, "being written");
        File.SetLastWriteTimeUtc(inProgress, Now.UtcDateTime.AddSeconds(1));

        var result = await CreateService().RecoverAsync();

        Assert.Equal(0, result.TempFilesDeleted);
        Assert.True(File.Exists(inProgress));
    }

    [Fact]
    public async Task RecoverAsync_DeletesTheOldVersionsOrphanedTempArchivesOnly()
    {
        var orphan = Stale(Path.Join(_temp, $".tmp_{Guid.NewGuid()}.cbz"), "orphan");
        var attachment = Stale(Path.Join(_temp, "attachment.epub"), "email");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(attachment));
    }

    [Fact]
    public async Task RecoverAsync_IntactArchive_RemovesTheRedundantBackup()
    {
        var archive = WriteArchive("Series/book.cbz", "new");
        var backup = WriteArchive("Series/book.cbz.backup", "old");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsRemoved);
        Assert.False(File.Exists(backup));
        Assert.Equal("new", PageOf(archive));
    }

    [Fact]
    public async Task RecoverAsync_TruncatedArchive_IsRestoredFromItsBackup()
    {
        // A stop during the old in-place overwrite left a truncated archive and the good copy
        // in '<archive>.backup'.
        var archive = WriteArchive("Series/book.cbz", "new");
        var backup = WriteArchive("Series/book.cbz.backup", "good");
        Truncate(archive);

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsRestored);
        Assert.False(File.Exists(backup));
        Assert.Equal("good", PageOf(archive));
    }

    [Fact]
    public async Task RecoverAsync_MissingArchive_IsRestoredFromItsBackup()
    {
        var backup = WriteArchive("Series/book.cbr.backup", "good");
        var archive = Path.Join(_library, "Series", "book.cbr");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsRestored);
        Assert.False(File.Exists(backup));
        Assert.Equal("good", PageOf(archive));
    }

    [Fact]
    public async Task RecoverAsync_PartialBackup_IsNeverRestoredOverAnIntactArchive()
    {
        // The old restore path deleted an intact original and renamed a half-copied backup
        // into its place when the backup copy failed (for example on a full disk).
        var archive = WriteArchive("Series/book.cbz", "intact");
        var backup = WriteArchive("Series/book.cbz.backup", "partial");
        Truncate(backup);

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsRemoved);
        Assert.Equal(0, result.BackupsRestored);
        Assert.Equal("intact", PageOf(archive));
    }

    [Fact]
    public async Task RecoverAsync_NeitherReadable_KeepsBoth()
    {
        var archive = WriteArchive("Series/book.cbz", "a");
        var backup = WriteArchive("Series/book.cbz.backup", "b");
        Truncate(archive);
        Truncate(backup);
        var archiveBytes = await File.ReadAllBytesAsync(archive);
        var backupBytes = await File.ReadAllBytesAsync(backup);

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsKept);
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(archive));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task RecoverAsync_BackupOfANonComicFile_IsLeftAlone()
    {
        var other = Stale(Path.Join(Directory.CreateDirectory(Path.Join(_library, "Series")).FullName, "notes.txt.backup"), "x");

        var result = await CreateService().RecoverAsync();

        Assert.False(result.DidAnything);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task RecoverAsync_AlsoSweepsTheDuplicateDirectory()
    {
        Directory.CreateDirectory(_settings.DuplicateDirectory);
        var temp = Stale(ArchiveWriteRecoveryService.CreateTempPath(_settings.DuplicateDirectory), "x");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task RecoverAsync_MissingDirectories_DoNothing()
    {
        _settings.WatchedDirectory = Path.Join(_root, "missing");
        _settings.TempFileDirectory = Path.Join(_root, "missing-temp");

        var result = await CreateService().RecoverAsync();

        Assert.False(result.DidAnything);
    }

    [Theory]
    [InlineData(".tmp_0123456789abcdef0123456789abcdef.comicinfo-write", true)]
    [InlineData(".tmp_0123456789abcdef0123456789abcdef.suwayomi-import", false)]
    [InlineData(".tmp_0123456789abcdef.cbz", false)]
    [InlineData("book.cbz", false)]
    public void IsTempPath_RecognisesOnlyArchiveWriteTempFiles(string name, bool expected)
    {
        Assert.Equal(expected, ArchiveWriteRecoveryService.IsTempPath(Path.Join(_library, name)));
        Assert.True(ArchiveWriteRecoveryService.IsTempPath(ArchiveWriteRecoveryService.CreateTempPath(_library)));
    }

    private ArchiveWriteRecoveryService CreateService() =>
        new(new TestOptionsMonitor<AppSettings>(_settings), NullLogger<ArchiveWriteRecoveryService>.Instance, new FakeTimeProvider(Now));

    private static string Stale(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Earlier);
        return path;
    }

    private string WriteArchive(string relativePath, string page)
    {
        var path = Path.Join(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("page001.txt").Open());
            writer.Write(page);
        }

        File.SetLastWriteTimeUtc(path, Earlier);
        return path;
    }

    /// <summary>Cuts off the end of an archive, where its central directory is, as an interrupted copy does.</summary>
    private static void Truncate(string path)
    {
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        File.SetLastWriteTimeUtc(path, Earlier);
    }

    private static string PageOf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("page001.txt")!.Open());
        return reader.ReadToEnd();
    }
}
