using System.Collections.Concurrent;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// EF-backed implementation of <see cref="IScheduledJobService"/>. Persists
/// each registered <see cref="IScheduledJobHandler"/>'s state in the
/// <c>ScheduledJobs</c> table and merges in handler-supplied display metadata
/// when listing.
/// </summary>
public class ScheduledJobService : IScheduledJobService
{
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IReadOnlyDictionary<string, IScheduledJobHandler> _handlers;
    private readonly ILogger<ScheduledJobService> _logger;

    public ScheduledJobService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IEnumerable<IScheduledJobHandler> handlers,
        ILogger<ScheduledJobService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;

        // Index handlers by JobKey, detecting duplicates eagerly so a misconfiguration surfaces at startup.
        var dict = new ConcurrentDictionary<string, IScheduledJobHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (!dict.TryAdd(handler.JobKey, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate IScheduledJobHandler.JobKey registered: '{handler.JobKey}'");
            }
        }
        _handlers = dict;
    }

    /// <summary>
    /// Handlers exposed to the hosted service so it can iterate and dispatch
    /// runs. Returned as an unsorted snapshot.
    /// </summary>
    public IEnumerable<IScheduledJobHandler> Handlers => _handlers.Values;

    public IScheduledJobHandler? GetHandler(string jobKey) =>
        _handlers.TryGetValue(jobKey, out var h) ? h : null;

    public event EventHandler<ScheduledJobChangedEventArgs>? JobChanged;

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ScheduledJobs
            .Select(j => j.JobKey)
            .ToListAsync(cancellationToken);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var handler in _handlers.Values)
        {
            if (existingSet.Contains(handler.JobKey))
            {
                continue;
            }

            var defaults = handler.Defaults;
            db.ScheduledJobs.Add(new ScheduledJobEntity
            {
                JobKey = handler.JobKey,
                Enabled = defaults.Enabled,
                IntervalMinutes = Math.Max(1, defaults.IntervalMinutes),
                OptionsJson = defaults.OptionsJson,
                LastStatus = ScheduledJobStatus.NeverRun.ToString(),
                NextRunUtc = defaults.Enabled
                    ? DateTime.UtcNow.AddMinutes(Math.Max(1, defaults.IntervalMinutes))
                    : null,
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded {Count} scheduled job definition(s)", added);
        }
    }

    public async Task<IReadOnlyList<ScheduledJobView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ScheduledJobs.AsNoTracking().ToListAsync(cancellationToken);
        var views = new List<ScheduledJobView>(rows.Count);
        foreach (var row in rows)
        {
            if (!_handlers.TryGetValue(row.JobKey, out var handler))
            {
                // Orphaned row (handler removed in a later version). Surface it but greyed-out.
                views.Add(MapToView(row, displayName: row.JobKey, description: "(handler not registered)"));
                continue;
            }
            views.Add(MapToView(row, handler.DisplayName, handler.Description));
        }
        return views.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ScheduledJobView?> GetAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(jobKey, out var handler))
        {
            return null;
        }
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ScheduledJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobKey == jobKey, cancellationToken);
        if (row is null)
        {
            return null;
        }
        return MapToView(row, handler.DisplayName, handler.Description);
    }

    public async Task<ScheduledJobView?> UpdateAsync(
        string jobKey,
        bool enabled,
        int intervalMinutes,
        string? optionsJson,
        CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(jobKey, out var handler))
        {
            return null;
        }
        if (intervalMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes), "Interval must be positive.");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.JobKey == jobKey, cancellationToken);
        if (row is null)
        {
            return null;
        }

        row.Enabled = enabled;
        row.IntervalMinutes = intervalMinutes;
        row.OptionsJson = optionsJson;
        row.UpdatedAt = DateTime.UtcNow;
        // Recompute next run from "now" so a freshly-changed schedule waits a full interval.
        row.NextRunUtc = enabled ? DateTime.UtcNow.AddMinutes(intervalMinutes) : null;

        await db.SaveChangesAsync(cancellationToken);

        JobChanged?.Invoke(this, new ScheduledJobChangedEventArgs(jobKey));
        return MapToView(row, handler.DisplayName, handler.Description);
    }

    public async Task MarkRunningAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.JobKey == jobKey, cancellationToken);
        if (row is null)
        {
            return;
        }
        row.LastStatus = ScheduledJobStatus.Running.ToString();
        row.LastMessage = null;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordCompletionAsync(
        string jobKey,
        ScheduledJobStatus status,
        string? message,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.JobKey == jobKey, cancellationToken);
        if (row is null)
        {
            return;
        }
        var now = DateTime.UtcNow;
        row.LastRunUtc = now;
        row.LastDurationMs = durationMs;
        row.LastStatus = status.ToString();
        row.LastMessage = Truncate(message, 1024);
        row.UpdatedAt = now;
        if (row.Enabled && row.IntervalMinutes > 0)
        {
            row.NextRunUtc = now.AddMinutes(row.IntervalMinutes);
        }
        else
        {
            row.NextRunUtc = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ScheduledJobView MapToView(ScheduledJobEntity row, string displayName, string description)
    {
        Enum.TryParse<ScheduledJobStatus>(row.LastStatus, ignoreCase: true, out var status);
        return new ScheduledJobView
        {
            JobKey = row.JobKey,
            DisplayName = displayName,
            Description = description,
            Enabled = row.Enabled,
            IntervalMinutes = row.IntervalMinutes,
            LastRunUtc = row.LastRunUtc,
            NextRunUtc = row.NextRunUtc,
            LastDurationMs = row.LastDurationMs,
            LastStatus = status,
            LastMessage = row.LastMessage,
            OptionsJson = row.OptionsJson,
        };
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
}
