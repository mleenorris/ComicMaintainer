using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero));
    private readonly DatabaseBackupService _service;

    public DatabaseBackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "comicmaintainer.db");
        CreateDatabase(_dbPath, "live");
        _settings = new AppSettings { ConfigDirectory = _root, DatabaseBackupRetention = 3 };
        _service = new DatabaseBackupService(
            _dbPath,
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<DatabaseBackupService>.Instance,
            timeProvider: _time);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void CreateDatabase(string path, string marker, bool wal = true)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            (wal ? "PRAGMA journal_mode=WAL;" : string.Empty) +
            "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS Marker (Value TEXT NOT NULL);" +
            "DELETE FROM Marker;" +
            "INSERT INTO Marker (Value) VALUES ($marker);";
        command.Parameters.AddWithValue("$marker", marker);
        command.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Marker LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task CreateBackupAsync_WritesConsistentCopyIntoDefaultBackupDirectory()
    {
        var backup = await _service.CreateBackupAsync();

        Assert.Equal("comicmaintainer-20261020-040000.db", backup.Name);
        Assert.Equal(Path.Combine(_root, "backups"), _service.BackupDirectory);
        var path = Path.Combine(_service.BackupDirectory, backup.Name);
        Assert.True(File.Exists(path));
        Assert.Equal(new FileInfo(path).Length, backup.SizeBytes);
        Assert.Equal("live", ReadMarker(path));
        Assert.Empty(Directory.GetFiles(_service.BackupDirectory, "*.partial"));
    }

    [Fact]
    public async Task CreateBackupAsync_UsesConfiguredDirectoryAndAvoidsNameCollisions()
    {
        _settings.DatabaseBackupDirectory = Path.Combine(_root, "custom");

        var first = await _service.CreateBackupAsync();
        var second = await _service.CreateBackupAsync();

        Assert.Equal(Path.Combine(_root, "custom"), _service.BackupDirectory);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal("comicmaintainer-20261020-040000-2.db", second.Name);
        Assert.Equal(2, _service.ListBackups().Count);
    }

    [Fact]
    public async Task ListBackups_ReturnsNewestFirstAndIgnoresForeignFiles()
    {
        await _service.CreateBackupAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await _service.CreateBackupAsync();
        File.WriteAllText(Path.Combine(_service.BackupDirectory, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_service.BackupDirectory, "comicmaintainer-bad.db"), "x");

        var list = _service.ListBackups();

        Assert.Equal(2, list.Count);
        Assert.Equal("comicmaintainer-20261020-050000.db", list[0].Name);
        Assert.Equal(new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc), list[0].CreatedUtc);
    }

    [Fact]
    public void ListBackups_ReturnsEmptyWhenDirectoryMissing()
    {
        Assert.Empty(_service.ListBackups());
    }

    [Fact]
    public async Task PruneBackups_KeepsNewestRetentionCount()
    {
        for (var i = 0; i < 5; i++)
        {
            await _service.CreateBackupAsync();
            _time.Advance(TimeSpan.FromDays(1));
        }

        var deleted = _service.PruneBackups();

        Assert.Equal(2, deleted);
        var remaining = _service.ListBackups().Select(b => b.Name).ToList();
        Assert.Equal(new[]
        {
            "comicmaintainer-20261024-040000.db",
            "comicmaintainer-20261023-040000.db",
            "comicmaintainer-20261022-040000.db"
        }, remaining);
    }

    [Fact]
    public async Task PruneBackups_DisabledWhenRetentionBelowOne()
    {
        _settings.DatabaseBackupRetention = 0;
        await _service.CreateBackupAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.CreateBackupAsync();

        Assert.Equal(0, _service.PruneBackups());
        Assert.Equal(2, _service.ListBackups().Count);
    }

    [Fact]
    public async Task DeleteAndOpen_WorkForExistingBackups()
    {
        var backup = await _service.CreateBackupAsync();

        using (var stream = _service.OpenBackup(backup.Name))
        {
            Assert.NotNull(stream);
            var header = new byte[16];
            stream!.ReadExactly(header);
            Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(header));
        }

        Assert.True(_service.DeleteBackup(backup.Name));
        Assert.False(_service.DeleteBackup(backup.Name));
        Assert.Null(_service.OpenBackup(backup.Name));
    }

    [Theory]
    [InlineData("../comicmaintainer.db")]
    [InlineData("..\\comicmaintainer.db")]
    [InlineData("/etc/passwd")]
    [InlineData("comicmaintainer-20261020-040000.db/../../x.db")]
    [InlineData("comicmaintainer-20261020-040000.DB")]
    [InlineData("comicmaintainer.db")]
    [InlineData("")]
    public async Task NameBasedOperations_RejectPathTraversalAndForeignNames(string name)
    {
        Assert.False(DatabaseBackupService.IsValidBackupName(name));
        Assert.Throws<ArgumentException>(() => _service.DeleteBackup(name));
        Assert.Throws<ArgumentException>(() => _service.OpenBackup(name));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.StageRestoreAsync(name));
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task StageRestoreAsync_TakesSafetyBackupAndStagesPendingFile()
    {
        var backup = await _service.CreateBackupAsync();
        CreateDatabase(_dbPath, "changed-after-backup");
        _time.Advance(TimeSpan.FromMinutes(5));

        var result = await _service.StageRestoreAsync(backup.Name);

        Assert.True(result.RestartRequired);
        Assert.Equal(backup.Name, result.Source);
        Assert.Equal("comicmaintainer-20261020-040500-prerestore.db", result.SafetyBackup.Name);
        Assert.Equal("changed-after-backup", ReadMarker(Path.Combine(_service.BackupDirectory, result.SafetyBackup.Name)));
        Assert.True(_service.IsRestorePending);

        var pending = DatabaseBackupService.GetPendingRestorePath(_dbPath);
        Assert.Equal("live", ReadMarker(pending));
        // Live database untouched until restart.
        Assert.Equal("changed-after-backup", ReadMarker(_dbPath));
        // No staging leftovers next to the database.
        Assert.Empty(Directory.GetFiles(_root, ".restore-staging-*"));
    }

    [Fact]
    public async Task StageRestoreAsync_MissingBackupThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.StageRestoreAsync("comicmaintainer-20200101-000000.db"));
        Assert.False(_service.IsRestorePending);
    }

    [Fact]
    public async Task StageRestoreFromStreamAsync_RejectsNonSqliteContent()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("definitely not a database"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _service.StageRestoreFromStreamAsync(content));

        Assert.Contains("not a SQLite database", ex.Message);
        Assert.False(_service.IsRestorePending);
        Assert.Empty(_service.ListBackups()); // no safety backup for a rejected file
    }

    [Fact]
    public async Task StageRestoreFromStreamAsync_RejectsCorruptSqliteFile()
    {
        var bytes = Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[4096]).ToArray();
        using var content = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.StageRestoreFromStreamAsync(content));
        Assert.False(_service.IsRestorePending);
    }

    [Fact]
    public async Task StageRestoreFromStreamAsync_RejectsSqliteDatabaseFromAnotherApplication()
    {
        var foreign = Path.Combine(_root, "foreign.db");
        using (var connection = new SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Other (Id INTEGER);";
            command.ExecuteNonQuery();
        }

        await using var content = File.OpenRead(foreign);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _service.StageRestoreFromStreamAsync(content));

        Assert.Contains("not a ComicMaintainer database", ex.Message);
    }

    [Fact]
    public async Task StageRestoreFromStreamAsync_StagesValidUpload()
    {
        var upload = Path.Combine(_root, "upload-source.db");
        CreateDatabase(upload, "uploaded", wal: false);

        await using (var content = File.OpenRead(upload))
        {
            var result = await _service.StageRestoreFromStreamAsync(content);
            Assert.Equal("upload", result.Source);
        }

        Assert.Equal("uploaded", ReadMarker(DatabaseBackupService.GetPendingRestorePath(_dbPath)));
        Assert.Single(_service.ListBackups());
    }

    [Fact]
    public async Task ApplyPendingRestore_SwapsInStagedDatabaseAndRemovesOldSidecars()
    {
        var backup = await _service.CreateBackupAsync();
        CreateDatabase(_dbPath, "changed");
        await _service.StageRestoreAsync(backup.Name);
        File.WriteAllText(_dbPath + "-wal", "stale");
        File.WriteAllText(_dbPath + "-shm", "stale");

        var applied = DatabaseBackupService.ApplyPendingRestore(_dbPath, NullLogger.Instance);

        Assert.True(applied);
        Assert.False(_service.IsRestorePending);
        Assert.False(File.Exists(_dbPath + "-wal"));
        Assert.False(File.Exists(_dbPath + "-shm"));
        Assert.Equal("live", ReadMarker(_dbPath));
    }

    [Fact]
    public void ApplyPendingRestore_NoPendingFileIsNoOp()
    {
        Assert.False(DatabaseBackupService.ApplyPendingRestore(_dbPath, NullLogger.Instance));
        Assert.Equal("live", ReadMarker(_dbPath));
    }

    [Fact]
    public void ApplyPendingRestore_SetsAsideInvalidPendingFile()
    {
        File.WriteAllText(DatabaseBackupService.GetPendingRestorePath(_dbPath), "garbage");

        Assert.False(DatabaseBackupService.ApplyPendingRestore(_dbPath, NullLogger.Instance));

        Assert.True(File.Exists(_dbPath + ".restore-failed"));
        Assert.False(File.Exists(DatabaseBackupService.GetPendingRestorePath(_dbPath)));
        Assert.Equal("live", ReadMarker(_dbPath));
    }

    [Fact]
    public async Task CancelPendingRestore_RemovesStagedFile()
    {
        var backup = await _service.CreateBackupAsync();
        await _service.StageRestoreAsync(backup.Name);

        Assert.True(_service.CancelPendingRestore());
        Assert.False(_service.IsRestorePending);
        Assert.False(_service.CancelPendingRestore());
    }

    [Fact]
    public async Task JobHandler_CreatesBackupAndPrunes()
    {
        var handler = new DatabaseBackupJobHandler(_service, NullLogger<DatabaseBackupJobHandler>.Instance);
        Assert.Equal("database-backup", handler.JobKey);
        Assert.True(handler.Defaults.Enabled);
        Assert.Equal(1440, handler.Defaults.IntervalMinutes);

        for (var i = 0; i < 4; i++)
        {
            await handler.ExecuteAsync(null, CancellationToken.None);
            _time.Advance(TimeSpan.FromDays(1));
        }

        var summary = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Contains("comicmaintainer-20261024-040000.db", summary);
        Assert.Contains("pruned 1", summary);
        Assert.Equal(3, _service.ListBackups().Count);
    }
}
