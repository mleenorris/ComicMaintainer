using System.IO.Compression;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The startup sweep after interrupted archive writes: unfinished temp archives are deleted, a
/// backup is restored only over a missing or unreadable archive and only when the backup itself
/// can be read, and a backup next to an intact archive is reported but never deleted, since it
/// may be the user's own copy. What cleans up after versions before 3.0 runs until it has
/// finished once.
/// </summary>
public sealed class ArchiveWriteRecoveryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddMinutes(-10);

    private readonly string _root;
    private readonly string _library;
    private readonly string _temp;
    private readonly string _config;
    private readonly AppSettings _settings;
    private readonly RecordingLogger _logger = new();

    public ArchiveWriteRecoveryServiceTests()
    {
        _root = Path.Join(Path.GetTempPath(), "archive-recovery-" + Guid.NewGuid().ToString("N"));
        _library = Path.Join(_root, "library");
        _temp = Path.Join(_root, "temp");
        _config = Path.Join(_root, "config");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_temp);
        Directory.CreateDirectory(_config);
        _settings = new AppSettings
        {
            WatchedDirectory = _library,
            DuplicateDirectory = Path.Join(_root, "duplicates"),
            TempFileDirectory = _temp,
            ConfigDirectory = _config
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
    public async Task RecoverAsync_DeletesTheOldCoverWritersTempArchivesNextToAnArchive()
    {
        // Before 3.0 the series cover writer built '<archive>.cover.tmp' next to the archive.
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var coverTemp = Stale(Path.Join(series, "book.cbz.cover.tmp"), "half-written");
        var unrelated = Stale(Path.Join(series, "notes.cover.tmp"), "mine");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(coverTemp));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task RecoverAsync_BackupNextToAnIntactArchive_IsReportedAndNeverDeleted()
    {
        // This version makes no backups, so such a file may be the user's own copy.
        var archive = WriteArchive("Series/book.cbz", "new");
        var backup = WriteArchive("Series/book.cbz.backup", "old");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsLeftInPlace);
        Assert.True(result.DidAnything);
        Assert.Equal("old", PageOf(backup));
        Assert.Equal("new", PageOf(archive));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("left in place"));
    }

    [Fact]
    public async Task RecoverAsync_BackupNextToAnArchiveTheLimitsRefuse_IsNotRestoredOverIt()
    {
        // Refused by the read limits, but intact: not the interrupted write a backup is for.
        var archive = Path.Join(_library, "Series", "refused.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var refused = ComicMaintainer.Tests.Utilities.ComicArchiveReaderTests.Rar5(dictionaryPower: 13);
        File.WriteAllBytes(archive, refused);
        var backup = WriteArchive("Series/refused.cbz.backup", "old");

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsLeftInPlace);
        Assert.Equal(0, result.BackupsRestored);
        Assert.Equal(refused, File.ReadAllBytes(archive));
        Assert.Equal("old", PageOf(backup));
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

        var backupBytes = await File.ReadAllBytesAsync(backup);

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.BackupsLeftInPlace);
        Assert.Equal(0, result.BackupsRestored);
        Assert.Equal("intact", PageOf(archive));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
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

        Assert.Equal(1, result.BackupsUnreadable);
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(archive));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task RecoverAsync_AfterTheUpgradeCleanupHasRun_LeavesLegacyFilesAlone()
    {
        // From then on anything with these names is the user's own: this version creates none.
        var firstResult = await CreateService().RecoverAsync();
        Assert.True(firstResult.UpgradeCleanupRan);
        Assert.True(File.Exists(MarkerPath));

        var archive = WriteArchive("Series/book.cbz", "damaged");
        var backup = WriteArchive("Series/book.cbz.backup", "mine");
        Truncate(archive);
        var archiveBytes = await File.ReadAllBytesAsync(archive);
        var missingArchiveBackup = WriteArchive("Series/other.cbz.backup", "mine too");
        var coverTemp = Stale(Path.Join(_library, "Series", "book.cbz.cover.tmp"), "mine");
        var legacyTemp = Stale(Path.Join(_temp, $".tmp_{Guid.NewGuid()}.cbz"), "mine");

        var result = await CreateService().RecoverAsync();

        Assert.False(result.UpgradeCleanupRan);
        Assert.False(result.DidAnything);
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(archive));
        Assert.Equal("mine", PageOf(backup));
        Assert.Equal("mine too", PageOf(missingArchiveBackup));
        Assert.False(File.Exists(Path.Join(_library, "Series", "other.cbz")));
        Assert.True(File.Exists(coverTemp));
        Assert.True(File.Exists(legacyTemp));
    }

    [Fact]
    public async Task RecoverAsync_AfterTheUpgradeCleanupHasRun_StillDeletesUnfinishedTempArchives()
    {
        await CreateService().RecoverAsync();
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var temp = Stale(ArchiveWriteRecoveryService.CreateTempPath(series), "half-written");

        var result = await CreateService().RecoverAsync();

        Assert.False(result.UpgradeCleanupRan);
        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task RecoverAsync_LibraryNotMounted_LeavesTheUpgradeCleanupDueForTheNextStartup()
    {
        _settings.WatchedDirectory = Path.Join(_root, "unmounted");

        var result = await CreateService().RecoverAsync();

        Assert.True(result.UpgradeCleanupRan);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task RecoverAsync_CancelledMidSweep_LeavesTheUpgradeCleanupDueForTheNextStartup()
    {
        WriteArchive("Series/book.cbz", "page");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService().RecoverAsync(cancelled.Token));

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task RecoverAsync_DeletingTheMarker_RunsTheUpgradeCleanupAgain()
    {
        await CreateService().RecoverAsync();
        File.Delete(MarkerPath);
        var coverTemp = Stale(Path.Join(Directory.CreateDirectory(Path.Join(_library, "Series")).FullName, "book.cbz.cover.tmp"), "x");

        var result = await CreateService().RecoverAsync();

        Assert.True(result.UpgradeCleanupRan);
        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(coverTemp));
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

    // The marker only saves a later sweep; not being able to write it cannot undo this one.
    [Fact]
    public async Task RecoverAsync_WhenTheUpgradeMarkerCannotBeWritten_StillFinishesAndWarns()
    {
        var temp = Stale(ArchiveWriteRecoveryService.CreateTempPath(_library), "x");
        Directory.CreateDirectory(MarkerPath);

        var result = await CreateService().RecoverAsync();

        Assert.Equal(1, result.TempFilesDeleted);
        Assert.False(File.Exists(temp));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Could not record", StringComparison.Ordinal));
    }

    // A backup written after this sweep started belongs to a write still in progress.
    [Fact]
    public async Task RecoverAsync_LeavesABackupNewerThanTheSweepAlone()
    {
        var archive = Path.Join(_library, "Saga.cbz");
        var backup = archive + ".backup";
        File.WriteAllText(archive, "damaged");
        File.WriteAllText(backup, "not yet a complete backup");
        File.SetLastWriteTimeUtc(backup, Now.UtcDateTime.AddMinutes(1));

        var result = await CreateService().RecoverAsync();

        Assert.False(result.DidAnything);
        Assert.Equal("damaged", File.ReadAllText(archive));
        Assert.True(File.Exists(backup));
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

    private string MarkerPath => Path.Join(_config, ArchiveWriteRecoveryService.UpgradeMarkerFileName);

    private ArchiveWriteRecoveryService CreateService() =>
        new(new TestOptionsMonitor<AppSettings>(_settings), _logger, new FakeTimeProvider(Now));

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

    private sealed class RecordingLogger : ILogger<ArchiveWriteRecoveryService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
