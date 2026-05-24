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
    public DbSet<FileReadStatusEntity> FileReadStatuses { get; set; } = null!;
    public DbSet<ReadingProgressEntity> ReadingProgresses { get; set; } = null!;
    public DbSet<ReaderPreferencesEntity> ReaderPreferences { get; set; } = null!;
    public DbSet<ReadingSessionEntity> ReadingSessions { get; set; } = null!;
    public DbSet<SeriesMetadataCacheEntity> SeriesMetadataCache { get; set; } = null!;
    public DbSet<ScheduledJobEntity> ScheduledJobs { get; set; } = null!;
    public DbSet<MetadataAuditFindingEntity> MetadataAuditFindings { get; set; } = null!;

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
            // Used by LibraryScanJobHandler to find files whose series-metadata
            // stamp is lower than the current cache record's MetadataVersion.
            entity.HasIndex(e => e.SeriesMetadataVersion);
            
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

        // Configure FileReadStatusEntity
        modelBuilder.Entity<FileReadStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.CurrentPage).HasDefaultValue(1);
            entity.HasIndex(e => e.FilePath).IsUnique();
            entity.HasIndex(e => e.IsRead);
            entity.HasIndex(e => e.LastReadDate);
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
            entity.Property(e => e.PinnedLocalizedTitle).HasMaxLength(512);
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
/// Database entity for file read status tracking
/// </summary>
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

    /// <summary>True when the canonical title was overridden by the user.</summary>
    public bool IsUserCanonical { get; set; }

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
    /// User-pinned localized title. When non-null/empty it wins over the
    /// language-preference rule but loses to <see cref="IsUserCanonical"/>.
    /// Persisted as a plain string column rather than a foreign key into
    /// the localized-titles JSON so order-only edits to that list don't
    /// silently invalidate a pin.
    /// </summary>
    public string? PinnedLocalizedTitle { get; set; }

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
}
