using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Reader.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Data;

/// <summary>
/// Entity Framework Core database context for ComicMaintainer with Identity support
/// </summary>
public class ComicMaintainerDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    public ComicMaintainerDbContext(DbContextOptions<ComicMaintainerDbContext> options)
        : base(options)
    {
    }

    public DbSet<ComicFileEntity> ComicFiles { get; set; } = null!;
    public DbSet<ProcessingHistoryEntity> ProcessingHistory { get; set; } = null!;
#pragma warning disable CS0618 // FileReadStatuses is retained for migration rollback only.
    public DbSet<FileReadStatusEntity> FileReadStatuses { get; set; } = null!;
#pragma warning restore CS0618
    public DbSet<UserFileReadStatusEntity> UserFileReadStatuses { get; set; } = null!;
    public DbSet<ReadingProgressEntity> ReadingProgresses { get; set; } = null!;
    public DbSet<ReaderPreferencesEntity> ReaderPreferences { get; set; } = null!;
    public DbSet<UserPreferencesEntity> UserPreferences { get; set; } = null!;
    public DbSet<ReadingSessionEntity> ReadingSessions { get; set; } = null!;
    public DbSet<SeriesMetadataCacheEntity> SeriesMetadataCache { get; set; } = null!;
    public DbSet<ScheduledJobEntity> ScheduledJobs { get; set; } = null!;
    public DbSet<MetadataAuditFindingEntity> MetadataAuditFindings { get; set; } = null!;
    public DbSet<ProcessingJobEntity> ProcessingJobs { get; set; } = null!;
    public DbSet<EreaderDeviceEntity> EreaderDevices { get; set; } = null!;
    public DbSet<SeriesEmailSubscriptionEntity> SeriesEmailSubscriptions { get; set; } = null!;
    public DbSet<ComicEmailDeliveryEntity> ComicEmailDeliveries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Index ApiKey for fast lookup in AuthService.GetUserByApiKeyAsync.
        // ApiKey is nullable, so this is a sparse index on a low-cardinality
        // column (one entry per user with an API key).
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.HasIndex(e => e.ApiKey);
        });

        // Configure ComicFileEntity
        modelBuilder.Entity<ComicFileEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Directory).IsRequired().HasMaxLength(2048);
            entity.HasIndex(e => e.FilePath).IsUnique();
            entity.HasIndex(e => e.IsProcessed);
            entity.HasIndex(e => e.IsRenamed);
            entity.HasIndex(e => e.IsNormalized);
            entity.HasIndex(e => e.IsDuplicate);
            entity.HasIndex(e => e.IsRead);
            // Hot filter/sort columns identified during EF query profiling:
            // - Directory: file-list queries often filter or group by folder
            // - UpdatedAt / CreatedAt: "recently added/modified" listings and ordering
            entity.HasIndex(e => e.Directory);
            entity.HasIndex(e => e.UpdatedAt);
            entity.HasIndex(e => e.CreatedAt);
            // Composite indexes for fast DB-backed file listing:
            // - (Directory, FileName) covers folder-scoped file pages sorted by name.
            // - FileName alone covers the default global "sort by name" listing.
            entity.HasIndex(e => new { e.Directory, e.FileName });
            entity.HasIndex(e => e.FileName);
            // Used by LibraryScanJobHandler to find files whose series-metadata
            // stamp is lower than the current cache record's MetadataVersion.
            entity.HasIndex(e => e.SeriesMetadataVersion);
            entity.HasIndex(e => new { e.MetadataVersion, e.WrittenMetadataVersion });
            entity.Property(e => e.MetadataSource).HasMaxLength(32).HasDefaultValue("Scanned");
            
            // Configure owned type for metadata
            entity.OwnsOne(e => e.Metadata, metadata =>
            {
                metadata.Property(m => m.Series).HasMaxLength(512);
                metadata.Property(m => m.Title).HasMaxLength(512);
                metadata.Property(m => m.Issue).HasMaxLength(50);
                metadata.Property(m => m.Volume).HasMaxLength(50);
                metadata.Property(m => m.Publisher).HasMaxLength(256);
                metadata.Property(m => m.Summary).HasMaxLength(2048);
                metadata.Property(m => m.Authors).HasConversion(
                    v => string.Join(';', v),
                    v => v.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
                );
                metadata.Property(m => m.Tags).HasConversion(
                    v => string.Join(';', v),
                    v => v.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList()
                );
                metadata.Property(m => m.IsUserEdited);
                metadata.Property(m => m.UserLockedFieldsMask).HasDefaultValue(0L);
            });
        });

        // Configure ProcessingHistoryEntity
        modelBuilder.Entity<ProcessingHistoryEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Success);
            // Used for "show history for a file" lookups and lookup-by-GUID API endpoints.
            entity.HasIndex(e => e.FilePath);
            entity.HasIndex(e => e.EntryId).IsUnique();
            
            // Configure before/after fields
            entity.Property(e => e.BeforeFilename).HasMaxLength(512);
            entity.Property(e => e.AfterFilename).HasMaxLength(512);
            entity.Property(e => e.BeforeTitle).HasMaxLength(512);
            entity.Property(e => e.AfterTitle).HasMaxLength(512);
            entity.Property(e => e.BeforeSeries).HasMaxLength(512);
            entity.Property(e => e.AfterSeries).HasMaxLength(512);
            entity.Property(e => e.BeforeIssue).HasMaxLength(50);
            entity.Property(e => e.AfterIssue).HasMaxLength(50);
            entity.Property(e => e.BeforePublisher).HasMaxLength(256);
            entity.Property(e => e.AfterPublisher).HasMaxLength(256);
            entity.Property(e => e.BeforeVolume).HasMaxLength(50);
            entity.Property(e => e.AfterVolume).HasMaxLength(50);
        });

        // Configure FileReadStatusEntity (legacy; see UserFileReadStatusEntity)
#pragma warning disable CS0618
        modelBuilder.Entity<FileReadStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.CurrentPage).HasDefaultValue(1);
            entity.HasIndex(e => e.FilePath).IsUnique();
            entity.HasIndex(e => e.IsRead);
            entity.HasIndex(e => e.LastReadDate);
        });
#pragma warning restore CS0618

        // Configure UserFileReadStatusEntity
        modelBuilder.Entity<UserFileReadStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.CurrentPage).HasDefaultValue(1);
            entity.HasIndex(e => new { e.UserId, e.FilePath }).IsUnique();
            // Covers the "read"/"unread" library filter, which is always scoped to one user.
            entity.HasIndex(e => new { e.UserId, e.IsRead });
            // Lets a file deletion/rename clean up every user's row in one statement.
            entity.HasIndex(e => e.FilePath);
        });

        // Configure ProcessingJobEntity
        modelBuilder.Entity<ProcessingJobEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobId).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(32);
            entity.Property(e => e.OperationName).IsRequired().HasMaxLength(128);
            entity.Property(e => e.CurrentFile).HasMaxLength(2048);
            entity.HasIndex(e => e.JobId).IsUnique();
            // Startup reconciliation scans for non-terminal jobs; job listing orders by start time.
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.StartTime);
        });

        // Configure ReadingProgressEntity
        modelBuilder.Entity<ReadingProgressEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.ContentId).IsRequired().HasMaxLength(2048);
            entity.HasIndex(e => new { e.UserId, e.ContentId }).IsUnique();
        });

        // Configure ReaderPreferencesEntity
        modelBuilder.Entity<ReaderPreferencesEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.FitPreference).HasMaxLength(50);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        // Configure UserPreferencesEntity
        modelBuilder.Entity<UserPreferencesEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Theme).HasMaxLength(32);
            entity.Property(e => e.ReadingMode).HasMaxLength(32);
            entity.Property(e => e.LibraryViewMode).HasMaxLength(32);
            entity.Property(e => e.FilterMode).HasMaxLength(64);
            entity.Property(e => e.SortMode).HasMaxLength(64);
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        // Configure ReadingSessionEntity
        modelBuilder.Entity<ReadingSessionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.ContentId).IsRequired().HasMaxLength(2048);
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.StartedAt });
        });

        // Configure SeriesMetadataCacheEntity
        modelBuilder.Entity<SeriesMetadataCacheEntity>(entity =>
        {
            entity.HasKey(e => e.NormalizedKey);
            entity.Property(e => e.NormalizedKey).HasMaxLength(512);
            entity.Property(e => e.CanonicalTitle).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Source).HasMaxLength(128);
            entity.Property(e => e.LookupStatus).HasMaxLength(64);
            // Aliases and UserAliases are stored as newline-delimited strings to
            // keep the entity model portable across providers without requiring
            // additional join tables.
            entity.Property(e => e.Aliases).HasConversion(
                v => string.Join('\n', v),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.UserAliases).HasConversion(
                v => string.Join('\n', v),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
            entity.Property(e => e.RemoteImageUrl).HasMaxLength(2048);
            entity.Property(e => e.LocalImageFile).HasMaxLength(512);
            entity.Property(e => e.ImageContentType).HasMaxLength(64);
            entity.Property(e => e.ImageStatus).HasMaxLength(32);
            entity.Property(e => e.PreferredLanguage).HasMaxLength(16);
            entity.Property(e => e.SeriesName).HasMaxLength(512);
            // LocalizedTitlesJson is opaque JSON; no max length so it can
            // accommodate long alias lists from providers like MangaDex.
        });

        // Configure ScheduledJobEntity
        modelBuilder.Entity<ScheduledJobEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobKey).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.JobKey).IsUnique();
            entity.Property(e => e.LastStatus).IsRequired().HasMaxLength(32);
            entity.Property(e => e.LastMessage).HasMaxLength(1024);
            entity.Property(e => e.CronExpression).HasMaxLength(128);
        });

        // Configure MetadataAuditFindingEntity
        modelBuilder.Entity<MetadataAuditFindingEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.FindingType).IsRequired().HasMaxLength(32);
            entity.Property(e => e.ExpectedSeries).HasMaxLength(512);
            entity.Property(e => e.ActualSeries).HasMaxLength(512);
            entity.Property(e => e.ActualIssue).HasMaxLength(50);
            entity.Property(e => e.Details).HasMaxLength(1024);
            entity.HasIndex(e => e.FindingType);
            entity.HasIndex(e => e.FilePath);
        });

        // Configure EreaderDeviceEntity
        modelBuilder.Entity<EreaderDeviceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
            entity.Property(e => e.EmailAddress).IsRequired().HasMaxLength(320);
            entity.Property(e => e.DeliveryFormat).IsRequired().HasMaxLength(16);
            entity.HasIndex(e => e.EmailAddress).IsUnique();
        });

        // Configure SeriesEmailSubscriptionEntity
        modelBuilder.Entity<SeriesEmailSubscriptionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NormalizedSeriesKey).IsRequired().HasMaxLength(512);
            entity.Property(e => e.SeriesTitle).IsRequired().HasMaxLength(512);
            entity.Property(e => e.DeliveryFormat).IsRequired().HasMaxLength(16);
            // One subscription per (series, device) pair so repeated opt-ins are idempotent.
            entity.HasIndex(e => new { e.NormalizedSeriesKey, e.DeviceId }).IsUnique();
            entity.HasOne<EreaderDeviceEntity>()
                .WithMany()
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure ComicEmailDeliveryEntity
        modelBuilder.Entity<ComicEmailDeliveryEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.DeviceName).IsRequired().HasMaxLength(128);
            entity.Property(e => e.DeviceEmail).IsRequired().HasMaxLength(320);
            entity.Property(e => e.DeliveryFormat).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(16);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1024);
            // Auto-send dedupe looks up "was this file already delivered to this device".
            entity.HasIndex(e => new { e.FilePath, e.DeviceId, e.Status });
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}

/// <summary>
/// Database entity for comic files
/// </summary>
public class ComicFileEntity
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsProcessed { get; set; }
    public bool IsRenamed { get; set; }
    public bool IsNormalized { get; set; }
    public bool IsDuplicate { get; set; }
    public bool IsRead { get; set; }
    public ComicMetadata? Metadata { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version of the <c>SeriesMetadataCacheEntity</c> record that was used
    /// the last time this file was normalized. The library-scan job
    /// compares this value against the current
    /// <c>SeriesMetadataCacheEntity.MetadataVersion</c> for the file's
    /// matched series; any file whose stamp is lower than the current
    /// record's version is considered stale and will be re-normalized
    /// without needing to re-read every archive on disk. New rows start
    /// at 0 so the first normalize pass always writes a stamp.
    /// </summary>
    public int SeriesMetadataVersion { get; set; }
    public int MetadataVersion { get; set; }
    public int WrittenMetadataVersion { get; set; }
    public DateTime? LastDbEditAt { get; set; }
    public DateTime? LastWriteAt { get; set; }
    public string MetadataSource { get; set; } = "Scanned";
}

/// <summary>
/// Database entity for processing history
/// </summary>
public class ProcessingHistoryEntity
{
    public int Id { get; set; }
    public Guid EntryId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    // Before/After tracking for changes
    public string? BeforeFilename { get; set; }
    public string? AfterFilename { get; set; }
    public string? BeforeTitle { get; set; }
    public string? AfterTitle { get; set; }
    public string? BeforeSeries { get; set; }
    public string? AfterSeries { get; set; }
    public string? BeforeIssue { get; set; }
    public string? AfterIssue { get; set; }
    public string? BeforePublisher { get; set; }
    public string? AfterPublisher { get; set; }
    public int? BeforeYear { get; set; }
    public int? AfterYear { get; set; }
    public string? BeforeVolume { get; set; }
    public string? AfterVolume { get; set; }
}

/// <summary>
/// Database entity for file read status tracking.
/// </summary>
/// <remarks>
/// Superseded by <see cref="UserFileReadStatusEntity"/>, which scopes the same state to a
/// user. Retained so the AddPerUserFileReadStatus migration is reversible and so an
/// operator can roll back to a previous release without losing read state; it is no longer
/// read or written by the application and can be dropped in a future major version.
/// </remarks>
[Obsolete("Use UserFileReadStatusEntity. Retained only for migration rollback; scheduled for removal in v3.0.")]
public class FileReadStatusEntity
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public int CurrentPage { get; set; } = 1; // Track current page for resuming reading
    public DateTime? LastReadDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for per-user file read status and resume position.
/// </summary>
/// <remarks>
/// Read state used to live on <c>ComicFileEntity.IsRead</c> and in
/// <see cref="FileReadStatusEntity"/>, both of which were global: in a multi-user
/// deployment one user marking an issue read flipped it for everyone, and the global state
/// could disagree with the per-user <see cref="ReadingProgressEntity"/> shown by the reader.
/// This entity is now the single source of truth for "has this user read this file", and is
/// kept consistent with <see cref="ReadingProgressEntity"/> by
/// <c>IFileStoreService.MarkFileReadAsync</c>.
/// </remarks>
public class UserFileReadStatusEntity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public int CurrentPage { get; set; } = 1;
    public DateTime? LastReadDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for batch processing jobs.
/// </summary>
/// <remarks>
/// Jobs are held in memory while they run, but that state used to be lost on restart: the UI
/// would keep polling a job id the server no longer knew about and simply hang on a stale
/// progress bar. Persisting jobs lets startup reconciliation mark anything left non-terminal
/// as <see cref="JobStatus.Interrupted"/>, so the UI gets a definite answer.
///
/// Progress is written on state transitions and otherwise throttled, so this table is not a
/// per-file write log; the durable per-file record remains
/// <see cref="ProcessingHistoryEntity"/>.
/// </remarks>
public class ProcessingJobEntity
{
    public int Id { get; set; }
    public Guid JobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;

    /// <summary>JSON array of the file paths in the batch.</summary>
    public string FilesJson { get; set; } = "[]";

    /// <summary>JSON object mapping file path to error message.</summary>
    public string ErrorsJson { get; set; } = "{}";

    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FailedFiles { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? CurrentFile { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for per-user durable reading progress (reader foundation).
/// </summary>
public class ReadingProgressEntity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public double PercentComplete { get; set; }
    public DateTime LastReadAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int LastReaderMode { get; set; }
    public int ReadingDirection { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for per-user reader preferences (reader foundation).
/// </summary>
public class ReaderPreferencesEntity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int DefaultReaderMode { get; set; }
    public int ReadingDirection { get; set; }
    public bool TapZonesEnabled { get; set; } = true;
    public bool AutoHideChrome { get; set; } = true;
    public string FitPreference { get; set; } = "width";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for per-user web UI preferences (theme, pagination, library view).
/// All preference columns are nullable so an unset preference falls back to the
/// application-level default rather than being pinned to a stored default value.
/// </summary>
public class UserPreferencesEntity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? Theme { get; set; }
    public int? PerPage { get; set; }
    public string? ReadingMode { get; set; }
    public string? LibraryViewMode { get; set; }
    public string? FilterMode { get; set; }
    public string? SortMode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Database entity for individual reading sessions (reader foundation).
/// </summary>
public class ReadingSessionEntity
{
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int StartPage { get; set; }
    public int? EndPage { get; set; }
    public bool Incognito { get; set; }
}

/// <summary>
/// Database entity for cached external series metadata + user-managed aliases.
/// Keyed on the normalized series key (lowercase alphanumerics joined by '-').
/// </summary>
public class SeriesMetadataCacheEntity
{
    /// <summary>Normalized series key (lowercase, non-alphanumerics replaced).</summary>
    public string NormalizedKey { get; set; } = string.Empty;

    /// <summary>Canonical title (provider-supplied unless the user overrides it).</summary>
    public string CanonicalTitle { get; set; } = string.Empty;

    /// <summary>Aliases reported by the external provider.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Aliases manually added by the user.</summary>
    public List<string> UserAliases { get; set; } = new();

    /// <summary>
    /// The single authoritative series name override (the "pinned" name).
    /// When non-null/empty the user has explicitly chosen this exact name
    /// (from the alias list or a custom alias they added); it is displayed
    /// in the library, written into ComicInfo.xml's <c>&lt;Series&gt;</c>, and
    /// stays sticky across refreshes / language-preference changes until the
    /// user picks a different name or reverts to automatic. When null/empty
    /// the displayed name is computed automatically from the preferred
    /// language (per-series, then global default) over the localized titles,
    /// falling back to <see cref="CanonicalTitle"/>.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>Provider source name (e.g. ComicVine, MangaDex), null when unknown.</summary>
    public string? Source { get; set; }

    /// <summary>Last successful or attempted external lookup time, null if never.</summary>
    public DateTime? LastLookupUtc { get; set; }

    /// <summary>Status of the last lookup: success, not_found, error, manual, pending.</summary>
    public string? LookupStatus { get; set; }

    /// <summary>
    /// URL of the series image from the external provider (or "user-upload"
    /// for user-supplied images). Used to detect when the local cached image
    /// needs re-downloading on the next refresh.
    /// </summary>
    public string? RemoteImageUrl { get; set; }

    /// <summary>
    /// Filename (relative to <c>AppSettings.SeriesImageCacheDirectory</c>) of
    /// the locally cached series image, or null when no image is cached.
    /// Never contains path separators.
    /// </summary>
    public string? LocalImageFile { get; set; }

    /// <summary>MIME type of the cached image (image/jpeg, image/png, image/webp).</summary>
    public string? ImageContentType { get; set; }

    /// <summary>UTC timestamp the image was last downloaded or uploaded.</summary>
    public DateTime? ImageDownloadedUtc { get; set; }

    /// <summary>
    /// State of the cached series image:
    /// <list type="bullet">
    /// <item><c>none</c> — no image available;</item>
    /// <item><c>downloaded</c> — cached from external provider;</item>
    /// <item><c>user</c> — uploaded by the user (sticky; not overwritten by refresh);</item>
    /// <item><c>failed</c> — last attempt failed and may be retried;</item>
    /// </list>
    /// </summary>
    public string? ImageStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User's preferred language for the displayed series name (one of
    /// <c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>), or null to fall back to
    /// the global default / canonical title.
    /// </summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// JSON-encoded list of <see cref="Models.LocalizedTitle"/> entries
    /// captured from the last successful external lookup. Used by the
    /// display-title resolver to pick a title matching
    /// <see cref="PreferredLanguage"/>. Stored as JSON to keep the schema
    /// stable; null/empty when no language information is available.
    /// </summary>
    public string? LocalizedTitlesJson { get; set; }

    /// <summary>
    /// Monotonic version counter bumped every time a mutation occurs that
    /// could affect how files belonging to this series should be
    /// normalized — canonical title edits, alias changes, language
    /// preference changes, localized-title list growth, manual matches,
    /// etc. The library-scan job compares this against each tracked
    /// file's <c>SeriesMetadataVersion</c> stamp and re-normalizes any
    /// file whose stamp is lower than the current value. Existing rows
    /// default to 1 (anything stamped 0 is therefore considered stale on
    /// first scan).
    /// </summary>
    public int MetadataVersion { get; set; } = 1;

    /// <summary>
    /// Plain-text synopsis / summary captured from the last successful external
    /// metadata lookup, displayed at the top of the series page. Null when the
    /// provider returned no description or the series is unmatched.
    /// </summary>
    public string? Synopsis { get; set; }
}

/// <summary>
/// A saved ereader email address (e.g. a Kindle "send to" address) that comics
/// can be delivered to.
/// </summary>
public class EreaderDeviceEntity
{
    public int Id { get; set; }

    /// <summary>Friendly device name shown in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Destination email address for this device.</summary>
    public string EmailAddress { get; set; } = string.Empty;

    /// <summary>
    /// Default delivery format for this device: <c>original</c> (send the CBZ/CBR
    /// as-is) or <c>epub</c> (convert before sending).
    /// </summary>
    public string DeliveryFormat { get; set; } = "original";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Marks a series for automatic delivery: every newly processed issue of the
/// series is queued for email to the referenced device.
/// </summary>
public class SeriesEmailSubscriptionEntity
{
    public int Id { get; set; }

    /// <summary>Normalized series key (same normalization used by the series cache).</summary>
    public string NormalizedSeriesKey { get; set; } = string.Empty;

    /// <summary>Display title captured when the subscription was created.</summary>
    public string SeriesTitle { get; set; } = string.Empty;

    public int DeviceId { get; set; }

    /// <summary>
    /// Delivery format override for this series: <c>original</c>, <c>epub</c>, or
    /// <c>device</c> to inherit the device default.
    /// </summary>
    public string DeliveryFormat { get; set; } = "device";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentAt { get; set; }
}

/// <summary>
/// Durable log of a comic emailed (or attempted) to a device. Doubles as the
/// dedupe record that stops automatic delivery from re-sending an issue that has
/// already been delivered to the same device.
/// </summary>
public class ComicEmailDeliveryEntity
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceEmail { get; set; } = string.Empty;

    /// <summary><c>original</c> or <c>epub</c>.</summary>
    public string DeliveryFormat { get; set; } = "original";

    /// <summary><c>pending</c>, <c>sent</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary><c>manual</c> for user-initiated sends, <c>auto</c> for series subscriptions.</summary>
    public string Source { get; set; } = "manual";

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}
