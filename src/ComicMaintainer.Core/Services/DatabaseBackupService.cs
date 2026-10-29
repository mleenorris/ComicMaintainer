using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Telemetry;
using ComicMaintainer.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IDatabaseBackupService"/>. Backups are taken with SQLite's
/// <c>VACUUM INTO</c>, which produces a transactionally consistent, compacted copy
/// while the application keeps reading and writing the live (WAL-mode) database.
/// </summary>
/// <remarks>
/// A restore is never applied to the running process — every pooled connection,
/// cache and background service would be holding the old database. Instead the
/// validated file is staged next to the live database as
/// <c>{db}.restore-pending</c> and <see cref="ApplyPendingRestore"/> swaps it in
/// on the next start, before migrations run.
/// </remarks>
public sealed partial class DatabaseBackupService : IDatabaseBackupService
{
    public const string FilePrefix = "comicmaintainer-";
    public const string FileExtension = ".db";
    public const string PendingRestoreSuffix = ".restore-pending";
    private const string SafetyBackupSuffix = "prerestore";

    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private readonly string _databasePath;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly ComicMaintainerMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseBackupService(
        string databasePath,
        IOptionsMonitor<AppSettings> settings,
        ILogger<DatabaseBackupService> logger,
        ComicMaintainerMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _settings = settings;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // comicmaintainer-yyyyMMdd-HHmmss.db, optionally followed by up to two short
    // lowercase suffixes (collision counter, "prerestore"). No dots, slashes or
    // other separators can appear, so a matching name is always a plain file name.
    [GeneratedRegex(@"^comicmaintainer-(\d{8}-\d{6})(?:-[a-z0-9]{1,16}){0,2}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupNamePattern();

    /// <summary>Path of the staged restore file for <paramref name="databasePath"/>.</summary>
    public static string GetPendingRestorePath(string databasePath) => databasePath + PendingRestoreSuffix;

    public string BackupDirectory
    {
        get
        {
            var settings = _settings.CurrentValue;
            var configured = settings.DatabaseBackupDirectory;
            var dir = string.IsNullOrWhiteSpace(configured)
                ? Path.Join(settings.ConfigDirectory, "backups")
                : configured;
            return Path.GetFullPath(dir);
        }
    }

    public bool IsRestorePending => File.Exists(GetPendingRestorePath(_databasePath));

    /// <summary>True when <paramref name="name"/> has the shape of a generated backup name.</summary>
    public static bool IsValidBackupName(string? name) =>
        !string.IsNullOrEmpty(name) && BackupNamePattern().IsMatch(name);

    public async Task<DatabaseBackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CreateBackupCoreAsync(suffix: null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<DatabaseBackupInfo> ListBackups()
    {
        var dir = BackupDirectory;
        if (!Directory.Exists(dir))
        {
            return Array.Empty<DatabaseBackupInfo>();
        }

        return Directory.EnumerateFiles(dir, FilePrefix + "*" + FileExtension, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(IsValidBackupName)
            .Select(name => ToInfo(new FileInfo(Path.Join(dir, name!))))
            .OrderByDescending(b => b.CreatedUtc)
            .ThenByDescending(b => b.Name, StringComparer.Ordinal)
            .ToList();
    }

    public bool DeleteBackup(string name)
    {
        var path = ResolveBackupPath(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        _logger.LogInformation("Deleted database backup {BackupName}", LoggingHelper.SanitizeForLog(name));
        return true;
    }

    public Stream? OpenBackup(string name)
    {
        var path = ResolveBackupPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public int PruneBackups()
    {
        var retention = _settings.CurrentValue.DatabaseBackupRetention;
        if (retention < 1)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var backup in ListBackups().Skip(retention))
        {
            try
            {
                File.Delete(Path.Join(BackupDirectory, backup.Name));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to prune database backup {BackupName}", backup.Name);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Pruned {Count} database backup(s), keeping the newest {Retention}", deleted, retention);
        }
        return deleted;
    }

    public async Task<DatabaseRestoreResult> StageRestoreAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = ResolveBackupPath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Backup not found.", name);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var staging = NewStagingPath();
            try
            {
                File.Copy(path, staging, overwrite: true);
                var result = await StageCoreAsync(staging, name, cancellationToken);
                _logger.LogWarning(
                    "Database restore from backup {BackupName} staged; it will be applied on the next restart",
                    LoggingHelper.SanitizeForLog(name));
                return result;
            }
            finally
            {
                DeleteQuietly(staging);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DatabaseRestoreResult> StageRestoreFromStreamAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var staging = NewStagingPath();
            try
            {
                await using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await content.CopyToAsync(target, cancellationToken);
                }

                var result = await StageCoreAsync(staging, "upload", cancellationToken);
                _logger.LogWarning("Database restore from an uploaded file staged; it will be applied on the next restart");
                return result;
            }
            finally
            {
                DeleteQuietly(staging);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool CancelPendingRestore()
    {
        var pending = GetPendingRestorePath(_databasePath);
        if (!File.Exists(pending))
        {
            return false;
        }

        File.Delete(pending);
        _logger.LogInformation("Pending database restore cancelled");
        return true;
    }

    /// <summary>
    /// Validates that <paramref name="path"/> is a healthy ComicMaintainer SQLite
    /// database: the SQLite header, <c>PRAGMA integrity_check</c> returning
    /// <c>ok</c>, and the EF Core migrations table being present. The file is
    /// switched to rollback-journal mode so it is self-contained (no -wal/-shm).
    /// Returns null when valid, otherwise the reason it was rejected.
    /// </summary>
    internal static async Task<string?> ValidateAndNormalizeAsync(string path, CancellationToken cancellationToken)
    {
        if (!HasSqliteHeader(path))
        {
            return "The file is not a SQLite database.";
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };

        try
        {
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);

            await using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = (await integrity.ExecuteScalarAsync(cancellationToken))?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return "The database failed the SQLite integrity check.";
                }
            }

            await using (var tables = connection.CreateCommand())
            {
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';";
                var count = Convert.ToInt64(await tables.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                if (count == 0)
                {
                    return "The file is a SQLite database but not a ComicMaintainer database.";
                }
            }

            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                await journal.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqliteException)
        {
            return "The file could not be opened as a SQLite database.";
        }

        return null;
    }

    /// <summary>
    /// Swaps a staged restore into place. Must run before anything opens the
    /// database (i.e. before migrations). A staged file that no longer looks like
    /// a SQLite database is set aside as <c>.restore-failed</c> and the current
    /// database is kept.
    /// </summary>
    /// <returns>True when a restore was applied.</returns>
    public static bool ApplyPendingRestore(string databasePath, ILogger logger)
    {
        databasePath = Path.GetFullPath(databasePath);
        var pending = GetPendingRestorePath(databasePath);
        if (!File.Exists(pending))
        {
            return false;
        }

        if (!HasSqliteHeader(pending))
        {
            var failed = databasePath + ".restore-failed";
            File.Move(pending, failed, overwrite: true);
            logger.LogError("Staged database restore is not a SQLite database; it was moved aside and the current database was kept");
            return false;
        }

        SqliteConnection.ClearAllPools();

        // The old database's WAL/SHM belong to the file being replaced; leaving them
        // would make SQLite replay old pages on top of the restored database. The
        // safety backup taken when the restore was staged already holds their contents.
        var sidecarPaths = new[] { "-wal", "-shm", "-journal" }
            .Select(sidecar => databasePath + sidecar)
            .Where(File.Exists);
        foreach (var sidecarPath in sidecarPaths)
        {
            File.Delete(sidecarPath);
        }

        File.Move(pending, databasePath, overwrite: true);
        logger.LogWarning("Applied staged database restore to {DatabaseFile}", Path.GetFileName(databasePath));
        return true;
    }

    private async Task<DatabaseRestoreResult> StageCoreAsync(string staging, string source, CancellationToken cancellationToken)
    {
        var error = await ValidateAndNormalizeAsync(staging, cancellationToken);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        var safety = await CreateBackupCoreAsync(SafetyBackupSuffix, cancellationToken);

        File.Move(staging, GetPendingRestorePath(_databasePath), overwrite: true);
        return new DatabaseRestoreResult(source, safety);
    }

    private async Task<DatabaseBackupInfo> CreateBackupCoreAsync(string? suffix, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            throw new InvalidOperationException("The database file does not exist.");
        }

        var dir = BackupDirectory;
        Directory.CreateDirectory(dir);

        var finalPath = NextBackupPath(dir, suffix);
        // Written under a non-matching name first so a half-written file is never
        // listed, downloaded or restored.
        var partialPath = finalPath + ".partial";
        DeleteQuietly(partialPath);

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 60
            };

            await using (var connection = new SqliteConnection(builder.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", partialPath);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            File.Move(partialPath, finalPath);
            var info = ToInfo(new FileInfo(finalPath));
            _metrics?.RecordDatabaseBackup(success: true);
            _logger.LogInformation("Created database backup {BackupName} ({SizeBytes} bytes)", info.Name, info.SizeBytes);
            return info;
        }
        catch
        {
            _metrics?.RecordDatabaseBackup(success: false);
            DeleteQuietly(partialPath);
            throw;
        }
    }

    private string NextBackupPath(string dir, string? suffix)
    {
        var stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = FilePrefix + stamp + (suffix is null ? string.Empty : "-" + suffix);
        var candidate = Path.Join(dir, baseName + FileExtension);
        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Join(dir, $"{baseName}-{n}{FileExtension}");
        }
        return candidate;
    }

    private string NewStagingPath()
    {
        var dir = Path.GetDirectoryName(_databasePath)!;
        Directory.CreateDirectory(dir);
        return Path.Join(dir, $".restore-staging-{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// Maps a client-supplied backup name to a path, rejecting anything that is not
    /// a generated backup name or that does not resolve directly inside the backup directory.
    /// </summary>
    private string ResolveBackupPath(string name)
    {
        if (!IsValidBackupName(name))
        {
            throw new ArgumentException("Invalid backup name.", nameof(name));
        }

        var dir = Path.GetFullPath(BackupDirectory);
        var full = Path.GetFullPath(Path.Join(dir, name));
        var dirWithSeparator = Path.EndsInDirectorySeparator(dir) ? dir : dir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(dirWithSeparator, StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid backup name.", nameof(name));
        }
        return full;
    }

    private static DatabaseBackupInfo ToInfo(FileInfo file)
    {
        var match = BackupNamePattern().Match(file.Name);
        var created = match.Success && DateTime.TryParseExact(
                match.Groups[1].Value,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            ? parsed
            : file.LastWriteTimeUtc;
        return new DatabaseBackupInfo(file.Name, file.Length, created);
    }

    private static bool HasSqliteHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                && header.SequenceEqual(SqliteHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }
}
