using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Assembles the overview/home page rows. Continue Reading is derived from the
/// authenticated user's <see cref="ReadingProgressEntity"/> records (per-user);
/// Series Updates and Newly Added Series are bucketed from per-series file
/// CreatedAt timestamps within a fixed recency window.
/// </summary>
public class OverviewService : IOverviewService
{
    /// <summary>Maximum number of cards returned per row.</summary>
    public const int RowCap = 20;

    /// <summary>A series/file is "recent" when added within this many days.</summary>
    public const int RecencyDays = 30;

    private readonly ISeriesLibraryService _library;
    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly ILogger<OverviewService> _logger;

    public OverviewService(
        ISeriesLibraryService library,
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        ILogger<OverviewService> logger)
    {
        _library = library;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<OverviewResult> GetOverviewAsync(string userId, CancellationToken cancellationToken = default)
    {
        var entries = await _library.GetSeriesOverviewEntriesAsync(cancellationToken);
        var cutoff = DateTime.UtcNow.AddDays(-RecencyDays);

        var result = new OverviewResult
        {
            ContinueReading = await BuildContinueReadingAsync(userId, entries, cancellationToken),
            SeriesUpdates = BuildSeriesUpdates(entries, cutoff),
            NewlyAddedSeries = BuildNewlyAdded(entries, cutoff)
        };

        return result;
    }

    private async Task<List<OverviewSeriesCard>> BuildContinueReadingAsync(
        string userId,
        IReadOnlyList<SeriesOverviewEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // In-progress = started (some progress recorded) but not completed.
        var inProgress = await db.ReadingProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userId
                && p.CompletedAt == null
                && p.PercentComplete > 0)
            .ToListAsync(cancellationToken);

        if (inProgress.Count == 0)
        {
            return new List<OverviewSeriesCard>();
        }

        // ContentId follows the reader's convention of using the file path.
        var progressByPath = new Dictionary<string, ReadingProgressEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in inProgress)
        {
            // Keep the most recently read record per file path.
            if (!progressByPath.TryGetValue(p.ContentId, out var existing)
                || p.LastReadAt > existing.LastReadAt)
            {
                progressByPath[p.ContentId] = p;
            }
        }

        var cards = new List<(OverviewSeriesCard Card, DateTime LastReadAt)>();
        foreach (var entry in entries)
        {
            ReadingProgressEntity? best = null;
            foreach (var path in entry.FilePaths)
            {
                if (progressByPath.TryGetValue(path, out var match)
                    && (best is null || match.LastReadAt > best.LastReadAt))
                {
                    best = match;
                }
            }

            if (best is null)
            {
                continue;
            }

            var card = ToCard(entry.Summary);
            card.ResumeFilePath = best.ContentId;
            card.ResumePage = best.CurrentPage;
            card.LastReadUtc = best.LastReadAt;
            cards.Add((card, best.LastReadAt));
        }

        return cards
            .OrderByDescending(c => c.LastReadAt)
            .Take(RowCap)
            .Select(c => c.Card)
            .ToList();
    }

    private static List<OverviewSeriesCard> BuildSeriesUpdates(
        IReadOnlyList<SeriesOverviewEntry> entries,
        DateTime cutoff)
    {
        // A new file was added to an already-existing series: the newest file
        // is within the window, but the series itself first appeared before it
        // (so brand-new series are excluded — they belong in Newly Added).
        return entries
            .Where(e => e.LatestCreatedAt >= cutoff && e.EarliestCreatedAt < cutoff)
            .OrderByDescending(e => e.LatestCreatedAt)
            .Take(RowCap)
            .Select(e => ToCard(e.Summary))
            .ToList();
    }

    private static List<OverviewSeriesCard> BuildNewlyAdded(
        IReadOnlyList<SeriesOverviewEntry> entries,
        DateTime cutoff)
    {
        return entries
            .Where(e => e.EarliestCreatedAt >= cutoff)
            .OrderByDescending(e => e.EarliestCreatedAt)
            .Take(RowCap)
            .Select(e => ToCard(e.Summary))
            .ToList();
    }

    private static OverviewSeriesCard ToCard(SeriesSummaryDto summary) => new()
    {
        Id = summary.Id,
        Title = summary.Title,
        CanonicalTitle = summary.CanonicalTitle,
        Aliases = summary.Aliases,
        MetadataSource = summary.MetadataSource,
        IssueCount = summary.IssueCount,
        TotalSize = summary.TotalSize,
        LatestModified = summary.LatestModified,
        CoverFilePath = summary.CoverFilePath,
        HasExternalImage = summary.HasExternalImage,
        ExternalImageUrl = summary.ExternalImageUrl,
        LookupStatus = summary.LookupStatus,
        LastLookupUtc = summary.LastLookupUtc,
        Synopsis = summary.Synopsis
    };
}
