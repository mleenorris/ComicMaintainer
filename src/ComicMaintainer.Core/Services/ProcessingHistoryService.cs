using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for managing processing history
/// </summary>
public class ProcessingHistoryService : IProcessingHistoryService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<ProcessingHistoryService> _logger;

    public ProcessingHistoryService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<ProcessingHistoryService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<(IEnumerable<ProcessingHistoryEntry> history, int total)> GetHistoryAsync(
        int limit = 50, 
        int offset = 0, 
        CancellationToken cancellationToken = default)
    {
        // Validate input parameters
        if (limit < 1 || limit > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var total = await dbContext.ProcessingHistory.CountAsync(cancellationToken);

        var entities = await dbContext.ProcessingHistory
            .OrderByDescending(h => h.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var history = entities.Select(e => new ProcessingHistoryEntry
        {
            Id = e.EntryId,
            FilePath = e.FilePath,
            Action = e.Action,
            Timestamp = e.Timestamp,
            Success = e.Success,
            ErrorMessage = e.ErrorMessage,
            BeforeFilename = e.BeforeFilename,
            AfterFilename = e.AfterFilename,
            BeforeTitle = e.BeforeTitle,
            AfterTitle = e.AfterTitle,
            BeforeSeries = e.BeforeSeries,
            AfterSeries = e.AfterSeries,
            BeforeIssue = e.BeforeIssue,
            AfterIssue = e.AfterIssue,
            BeforePublisher = e.BeforePublisher,
            AfterPublisher = e.AfterPublisher,
            BeforeYear = e.BeforeYear,
            AfterYear = e.AfterYear,
            BeforeVolume = e.BeforeVolume,
            AfterVolume = e.AfterVolume
        });

        return (history, total);
    }

    public async Task AddHistoryEntryAsync(
        ProcessingHistoryEntry entry, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entity = new ProcessingHistoryEntity
            {
                EntryId = entry.Id != Guid.Empty ? entry.Id : Guid.NewGuid(),
                FilePath = entry.FilePath,
                Action = entry.Action,
                Timestamp = entry.Timestamp,
                Success = entry.Success,
                ErrorMessage = entry.ErrorMessage,
                BeforeFilename = entry.BeforeFilename,
                AfterFilename = entry.AfterFilename,
                BeforeTitle = entry.BeforeTitle,
                AfterTitle = entry.AfterTitle,
                BeforeSeries = entry.BeforeSeries,
                AfterSeries = entry.AfterSeries,
                BeforeIssue = entry.BeforeIssue,
                AfterIssue = entry.AfterIssue,
                BeforePublisher = entry.BeforePublisher,
                AfterPublisher = entry.AfterPublisher,
                BeforeYear = entry.BeforeYear,
                AfterYear = entry.AfterYear,
                BeforeVolume = entry.BeforeVolume,
                AfterVolume = entry.AfterVolume
            };

            dbContext.ProcessingHistory.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            // Log cancellation but don't treat as error - operation was intentionally cancelled
            _logger.LogWarning(ex, "History entry addition was cancelled for {FilePath}", LoggingHelper.SanitizePathForLog(entry.FilePath));
        }
        catch (Exception ex)
        {
            // Log other errors but don't throw - history logging should not break processing
            _logger.LogError(ex, "Error adding processing history entry for {FilePath}", LoggingHelper.SanitizePathForLog(entry.FilePath));
        }
    }
}
