using ComicMaintainer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled job that takes a consistent backup of the SQLite database and then
/// prunes old backups down to <c>AppSettings.DatabaseBackupRetention</c>.
/// </summary>
public class DatabaseBackupJobHandler : IScheduledJobHandler
{
    public const string Key = "database-backup";

    private readonly IDatabaseBackupService _backups;
    private readonly ILogger<DatabaseBackupJobHandler> _logger;

    public DatabaseBackupJobHandler(IDatabaseBackupService backups, ILogger<DatabaseBackupJobHandler> logger)
    {
        _backups = backups;
        _logger = logger;
    }

    public string JobKey => Key;
    public string DisplayName => "Database Backup";
    public string Description =>
        "Takes a consistent backup of the application database into the backups directory and prunes old backups beyond the configured retention.";

    // Enabled by default: a daily backup is cheap and protects users, read
    // progress, settings and credentials against corruption or a bad upgrade.
    public ScheduledJobDefaults Defaults => new(Enabled: true, IntervalMinutes: 60 * 24);

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var backup = await _backups.CreateBackupAsync(cancellationToken);
        var pruned = _backups.PruneBackups();
        _logger.LogInformation("Scheduled database backup created: {BackupName}; pruned {Pruned}", backup.Name, pruned);

        var sizeMb = backup.SizeBytes / 1024d / 1024d;
        return pruned > 0
            ? $"Created {backup.Name} ({sizeMb:F1} MB); pruned {pruned} old backup(s)."
            : $"Created {backup.Name} ({sizeMb:F1} MB).";
    }
}
