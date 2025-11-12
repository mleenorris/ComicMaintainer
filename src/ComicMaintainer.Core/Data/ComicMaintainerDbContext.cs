using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Models.Auth;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
