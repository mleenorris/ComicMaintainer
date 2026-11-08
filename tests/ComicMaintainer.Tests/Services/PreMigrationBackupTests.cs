using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Startup backs the database up before it applies an upgrade's migrations, which the previous version cannot undo.
/// Each test uses a real SQLite database brought up to a given migration.
/// </summary>
public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "premigrate-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly AppSettings _settings;
    private readonly DatabaseBackupService _backups;

    public PreMigrationBackupTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Join(_root, "comicmaintainer.db");
        _settings = new AppSettings { ConfigDirectory = _root, DatabaseBackupRetention = 7 };
        _backups = new DatabaseBackupService(
            _databasePath,
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<DatabaseBackupService>.Instance,
            timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task PendingMigrations_BackUpTheDatabaseAsTheVersionBeforeTheUpgradeLeftIt()
    {
        await using var db = CreateContext();
        var migrations = db.Database.GetMigrations().ToList();
        var previous = migrations[^2];
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var logger = new Mock<ILogger>();

        var backup = await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, _backups, logger.Object);

        Assert.NotNull(backup);
        Assert.Equal("comicmaintainer-20261107-030000-premigrate.db", backup.Name);
        Assert.True(DatabaseBackupService.IsPreMigrationBackup(backup.Name));
        var backupPath = Path.Join(_backups.BackupDirectory, backup.Name);
        Assert.Equal(previous, LastAppliedMigration(backupPath));
        // The log says where the backup is.
        logger.Verify(
            log => log.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(backupPath, StringComparison.Ordinal)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UpToDateDatabase_IsNotBackedUp()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var backup = await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, _backups, NullLogger.Instance);

        Assert.Null(backup);
        Assert.Empty(_backups.ListBackups());
    }

    [Fact]
    public async Task NewDatabase_IsNotBackedUp()
    {
        await using var db = CreateContext();

        var backup = await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, _backups, NullLogger.Instance);

        Assert.Null(backup);
        Assert.False(Directory.Exists(_backups.BackupDirectory));
    }

    [Fact]
    public async Task DatabaseWithoutAppliedMigrations_IsNotBackedUp()
    {
        await using var db = CreateContext();
        // Opening the connection creates an empty database file, as SQLite does on first use.
        await db.Database.OpenConnectionAsync();
        await db.Database.CloseConnectionAsync();
        Assert.True(File.Exists(_databasePath));

        var backup = await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, _backups, NullLogger.Instance);

        Assert.Null(backup);
        Assert.Empty(_backups.ListBackups());
    }

    [Fact]
    public async Task FailedBackup_StopsStartupWithoutMigrating()
    {
        await using var db = CreateContext();
        var migrations = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        // A file where the backup directory should be makes creating it fail.
        _settings.DatabaseBackupDirectory = Path.Join(_root, "not-a-directory");
        await File.WriteAllTextAsync(_settings.DatabaseBackupDirectory, "file");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, _backups, NullLogger.Instance));

        Assert.Contains(_settings.DatabaseBackupDirectory, error.Message);
        Assert.Single(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task WithoutABackupService_NothingIsBackedUp()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().ToList()[^2]);

        var backup = await PreMigrationBackup.CreateIfMigrationsPendingAsync(db.Database, _databasePath, backups: null, NullLogger.Instance);

        Assert.Null(backup);
    }

    private ComicMaintainerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString())
            .Options);

    private static string LastAppliedMigration(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }
}
