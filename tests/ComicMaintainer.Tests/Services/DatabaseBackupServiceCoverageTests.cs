using System.Diagnostics.Metrics;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

public sealed class DatabaseBackupServiceCoverageTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero));

    public DatabaseBackupServiceCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm-backup-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "comicmaintainer.db");
        _settings = new AppSettings { ConfigDirectory = _root, DatabaseBackupRetention = 3 };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateBackupAsync_ThrowsWhenTheDatabaseFileIsMissing()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync());

        Assert.Equal("The database file does not exist.", ex.Message);
        Assert.False(Directory.Exists(service.BackupDirectory));
    }

    [Fact]
    public async Task CreateBackupAsync_RecordsASuccessfulBackupMetric()
    {
        CreateDatabase(_dbPath);
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new BackupMetricRecorder(metrics.Meter);
        var service = CreateService(metrics: metrics);

        await service.CreateBackupAsync();

        Assert.Equal(new[] { "success" }, recorder.Outcomes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateBackupAsync_WhenTheCopyFails_RecordsAFailureAndLeavesNoFiles(bool withMetrics)
    {
        File.WriteAllText(_dbPath, "This file is long enough to look like something, but it is not a SQLite database.");
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new BackupMetricRecorder(metrics.Meter);
        var service = CreateService(metrics: withMetrics ? metrics : null);

        await Assert.ThrowsAsync<SqliteException>(() => service.CreateBackupAsync());

        Assert.Equal(withMetrics ? new[] { "failed" } : Array.Empty<string>(), recorder.Outcomes);
        Assert.True(Directory.Exists(service.BackupDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(service.BackupDirectory));
        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public async Task PruneBackups_SkipsABackupThatCannotBeDeletedAndKeepsGoing()
    {
        CreateDatabase(_dbPath);
        _settings.DatabaseBackupRetention = 1;
        var monitor = new HookedOptionsMonitor(_settings);
        var logger = new CapturingLogger();
        var service = CreateService(monitor, logger);
        var oldest = await service.CreateBackupAsync();
        _time.Advance(TimeSpan.FromDays(1));
        var middle = await service.CreateBackupAsync();
        _time.Advance(TimeSpan.FromDays(1));
        var newest = await service.CreateBackupAsync();
        var oldestPath = Path.Combine(service.BackupDirectory, oldest.Name);

        // PruneBackups reads the settings once for the retention and once to list the
        // backups; the next read happens while deleting. Replacing the oldest backup with
        // a directory at that point simulates a backup that cannot be removed.
        var reads = 0;
        monitor.OnRead = () =>
        {
            if (++reads == 3)
            {
                File.Delete(oldestPath);
                Directory.CreateDirectory(oldestPath);
            }
        };

        var deleted = service.PruneBackups();

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(service.BackupDirectory, middle.Name)));
        Assert.True(File.Exists(Path.Combine(service.BackupDirectory, newest.Name)));
        Assert.True(Directory.Exists(oldestPath));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(oldest.Name, warning.Message);
        Assert.True(warning.Exception is IOException or UnauthorizedAccessException);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Pruned 1 database backup(s)"));
    }

    [Fact]
    public void PruneBackups_WithNothingBeyondTheRetentionDeletesNothing()
    {
        var logger = new CapturingLogger();
        var service = CreateService(logger: logger);

        Assert.Equal(0, service.PruneBackups());
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Pruned"));
    }

    [Fact]
    public async Task ListBackups_UsesTheFileTimestampWhenTheNameHoldsAnImpossibleDate()
    {
        CreateDatabase(_dbPath);
        var service = CreateService();
        var regular = await service.CreateBackupAsync();
        const string oddName = "comicmaintainer-20261399-999999.db";
        var oddPath = Path.Combine(service.BackupDirectory, oddName);
        File.WriteAllText(oddPath, "x");
        var lastWrite = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(oddPath, lastWrite);

        var backups = service.ListBackups();

        Assert.True(DatabaseBackupService.IsValidBackupName(oddName));
        Assert.Equal(new[] { oddName, regular.Name }, backups.Select(b => b.Name).ToArray());
        Assert.Equal(lastWrite, backups[0].CreatedUtc);
        Assert.Equal(1, backups[0].SizeBytes);
    }

    [Fact]
    public async Task NameBasedOperations_WorkWhenTheConfiguredDirectoryEndsWithASeparator()
    {
        CreateDatabase(_dbPath);
        _settings.DatabaseBackupDirectory = Path.Combine(_root, "custom") + Path.DirectorySeparatorChar;
        var service = CreateService();

        var backup = await service.CreateBackupAsync();

        Assert.True(File.Exists(Path.Combine(_root, "custom", backup.Name)));
        using (var stream = service.OpenBackup(backup.Name))
        {
            Assert.NotNull(stream);
        }
        Assert.True(service.DeleteBackup(backup.Name));
        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public async Task NameBasedOperations_AcceptGeneratedNamesWhenTheBackupDirectoryIsTheFileSystemRoot()
    {
        // The root keeps its separator ("/", "C:\"), which used to get every backup name
        // rejected as invalid. Nothing is written to the root: the random suffix names a
        // backup that is absent, which must be reported as missing rather than invalid.
        var fileSystemRoot = Path.GetPathRoot(_root)!;
        _settings.DatabaseBackupDirectory = fileSystemRoot;
        var service = CreateService();
        var name = $"comicmaintainer-20261020-040000-{Guid.NewGuid().ToString("N")[..16]}.db";
        Assert.True(DatabaseBackupService.IsValidBackupName(name));
        Assert.False(File.Exists(Path.Join(fileSystemRoot, name)));

        Assert.Equal(fileSystemRoot, service.BackupDirectory);
        Assert.Null(service.OpenBackup(name));
        Assert.False(service.DeleteBackup(name));
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => service.StageRestoreAsync(name));
        Assert.Equal(name, ex.FileName);
    }

    [Fact]
    public async Task StageRestoreFromStreamAsync_RejectsADatabaseThatFailsTheIntegrityCheck()
    {
        CreateDatabase(_dbPath);
        var upload = Path.Combine(_root, "inconsistent.db");
        CreateDatabase(upload, wal: false);
        // Claim five freelist pages in the header while the database has none.
        using (var stream = new FileStream(upload, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 36;
            stream.Write(new byte[] { 0, 0, 0, 5 });
        }
        var service = CreateService();

        await using var content = File.OpenRead(upload);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.StageRestoreFromStreamAsync(content));

        Assert.Equal("The database failed the SQLite integrity check.", ex.Message);
        Assert.False(service.IsRestorePending);
        Assert.Empty(service.ListBackups());
        Assert.Empty(Directory.GetFiles(_root, ".restore-staging-*"));
    }

    [Fact]
    public void ApplyPendingRestore_SetsAsideAStagedFileThatCannotBeRead()
    {
        CreateDatabase(_dbPath, wal: false);
        var pending = DatabaseBackupService.GetPendingRestorePath(_dbPath);
        File.Copy(_dbPath, pending);
        var logger = new CapturingLogger();

        bool applied;
        // An exclusive lock makes the header unreadable for the restore check.
        using (new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            applied = DatabaseBackupService.ApplyPendingRestore(_dbPath, logger);
        }

        Assert.False(applied);
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(_dbPath + ".restore-failed"));
        Assert.True(File.Exists(_dbPath));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("not a SQLite database"));
    }

    private DatabaseBackupService CreateService(
        IOptionsMonitor<AppSettings>? monitor = null,
        ILogger<DatabaseBackupService>? logger = null,
        ComicMaintainerMetrics? metrics = null) =>
        new(
            _dbPath,
            monitor ?? new TestOptionsMonitor<AppSettings>(_settings),
            logger ?? NullLogger<DatabaseBackupService>.Instance,
            metrics,
            _time);

    private static void CreateDatabase(string path, bool wal = true)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            (wal ? "PRAGMA journal_mode=WAL;" : string.Empty) +
            "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS Marker (Value TEXT NOT NULL);" +
            "INSERT INTO Marker (Value) VALUES ('live');";
        command.ExecuteNonQuery();
    }

    private sealed class HookedOptionsMonitor : IOptionsMonitor<AppSettings>
    {
        private readonly AppSettings _value;

        public HookedOptionsMonitor(AppSettings value) => _value = value;

        public Action? OnRead { get; set; }

        public AppSettings CurrentValue
        {
            get
            {
                OnRead?.Invoke();
                return _value;
            }
        }

        public AppSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<DatabaseBackupService>, ILogger
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class BackupMetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<string> _outcomes = new();

        public BackupMetricRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "comicmaintainer.database.backups")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome")
                    {
                        lock (_outcomes)
                        {
                            _outcomes.Add((string)tag.Value!);
                        }
                    }
                }
            });
            _listener.Start();
        }

        public string[] Outcomes
        {
            get
            {
                lock (_outcomes)
                {
                    return _outcomes.ToArray();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
