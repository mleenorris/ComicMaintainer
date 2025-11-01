using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Service for managing processing history
/// </summary>
public class ProcessingHistoryService : IProcessingHistoryService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessingHistoryService> _logger;

    public ProcessingHistoryService(
        IServiceProvider serviceProvider,
        ILogger<ProcessingHistoryService> logger)
    {
        _serviceProvider = serviceProvider;
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

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();

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
                ErrorMessage = e.ErrorMessage
            });

            return (history, total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving processing history");
            return (Enumerable.Empty<ProcessingHistoryEntry>(), 0);
        }
    }

    public async Task AddHistoryEntryAsync(
        ProcessingHistoryEntry entry, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();

            var entity = new ProcessingHistoryEntity
            {
                EntryId = entry.Id != Guid.Empty ? entry.Id : Guid.NewGuid(),
                FilePath = entry.FilePath,
                Action = entry.Action,
                Timestamp = entry.Timestamp,
                Success = entry.Success,
                ErrorMessage = entry.ErrorMessage
            };

            dbContext.ProcessingHistory.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding processing history entry for {FilePath}", entry.FilePath);
        }
    }
}
