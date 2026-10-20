using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// Database backup management: create, list, download, delete and restore
/// SQLite backups.
/// </summary>
/// <remarks>
/// Administrator-only in full, including the read endpoints: a backup is a
/// complete copy of the database, so it carries password hashes, API keys and
/// SMTP credentials. A restore is staged and applied on the next restart.
/// </remarks>
[ApiController]
[Route("api/backups")]
[Authorize(Policy = AuthorizationPolicies.CanAdminister)]
public class BackupsController : ControllerBase
{
    /// <summary>Largest database file accepted by <see cref="UploadRestore"/> (2 GiB).</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private readonly IDatabaseBackupService _backups;
    private readonly ILogger<BackupsController> _logger;

    public BackupsController(IDatabaseBackupService backups, ILogger<BackupsController> logger)
    {
        _backups = backups;
        _logger = logger;
    }

    /// <summary>Backups (newest first) and whether a restore is pending.</summary>
    [HttpGet]
    public ActionResult<object> List()
    {
        var backups = _backups.ListBackups()
            .Select(b => new
            {
                name = b.Name,
                size_bytes = b.SizeBytes,
                created_utc = b.CreatedUtc
            })
            .ToList();

        return Ok(new
        {
            backups,
            restore_pending = _backups.IsRestorePending
        });
    }

    /// <summary>Creates a backup of the live database now.</summary>
    [HttpPost]
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> Create(CancellationToken cancellationToken)
    {
        try
        {
            var backup = await _backups.CreateBackupAsync(cancellationToken);
            return Ok(new
            {
                name = backup.Name,
                size_bytes = backup.SizeBytes,
                created_utc = backup.CreatedUtc
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Downloads a backup file.</summary>
    [HttpGet("{name}/download")]
    [DisableRequestTimeout]
    public IActionResult Download(string name)
    {
        if (!IsSafeName(name))
        {
            return BadRequest(new { error = "Invalid backup name" });
        }

        var stream = _backups.OpenBackup(name);
        if (stream is null)
        {
            return NotFound(new { error = "Backup not found" });
        }

        Response.Headers.CacheControl = "no-store, private";
        return File(stream, "application/vnd.sqlite3", name);
    }

    /// <summary>Deletes a backup file.</summary>
    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        if (!IsSafeName(name))
        {
            return BadRequest(new { error = "Invalid backup name" });
        }

        return _backups.DeleteBackup(name)
            ? Ok(new { deleted = name })
            : NotFound(new { error = "Backup not found" });
    }

    /// <summary>
    /// Validates the backup, takes a safety backup of the current database and
    /// stages the restore. The application must be restarted to apply it.
    /// </summary>
    [HttpPost("{name}/restore")]
    [RequestTimeout(RequestTimeoutPolicies.LongRunning)]
    public async Task<ActionResult<object>> Restore(string name, CancellationToken cancellationToken)
    {
        if (!IsSafeName(name))
        {
            return BadRequest(new { error = "Invalid backup name" });
        }

        try
        {
            var result = await _backups.StageRestoreAsync(name, cancellationToken);
            _logger.LogWarning("Database restore from {BackupName} staged by {User}",
                LoggingHelper.SanitizeForLog(name), LoggingHelper.SanitizeForLog(User.Identity?.Name));
            return Ok(ToResponse(result));
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Backup not found" });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Uploads a database file (multipart field <c>file</c>), validates it and stages
    /// it as a restore. The application must be restarted to apply it.
    /// </summary>
    [HttpPost("upload-restore")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [DisableRequestTimeout]
    public async Task<ActionResult<object>> UploadRestore(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "File is required" });
        }
        if (file.Length > MaxUploadBytes)
        {
            return BadRequest(new { error = "File too large" });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _backups.StageRestoreFromStreamAsync(stream, cancellationToken);
            _logger.LogWarning("Database restore from an uploaded file staged by {User}",
                LoggingHelper.SanitizeForLog(User.Identity?.Name));
            return Ok(ToResponse(result));
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Discards a staged restore before it is applied.</summary>
    [HttpDelete("restore-pending")]
    public IActionResult CancelPendingRestore()
    {
        return _backups.CancelPendingRestore()
            ? Ok(new { cancelled = true })
            : NotFound(new { error = "No restore is pending" });
    }

    private static object ToResponse(DatabaseRestoreResult result) => new
    {
        source = result.Source,
        safety_backup = new
        {
            name = result.SafetyBackup.Name,
            size_bytes = result.SafetyBackup.SizeBytes,
            created_utc = result.SafetyBackup.CreatedUtc
        },
        restart_required = result.RestartRequired,
        message = "Restore staged. Restart ComicMaintainer to apply it."
    };

    private static bool IsSafeName(string? name) =>
        Core.Services.DatabaseBackupService.IsValidBackupName(name);
}
