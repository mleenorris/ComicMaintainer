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

        // Pull every record that represents real reading activity for the user:
        // either an in-progress issue (some progress, not completed) or a
        // completed issue. Completed records are needed so a series stays in
        // Continue Reading after an issue is finished but more issues remain.
        var activity = await db.ReadingProgresses
            .AsNoTracking()
            .Where(p => p.UserId == userId
                && (p.CompletedAt != null || p.PercentComplete > 0))
            .ToListAsync(cancellationToken);

        if (activity.Count == 0)
        {
            return new List<OverviewSeriesCard>();
        }

        // ContentId follows the reader's convention of using the file path.
        var progressByPath = new Dictionary<string, ReadingProgressEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in activity)
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
            // Issues marked read via the file read flag (outside the reader) count
            // as read even though they have no reading-progress record.
            var readFilePaths = new HashSet<string>(entry.ReadFilePaths, StringComparer.OrdinalIgnoreCase);

            // The most recently read issue (in-progress or completed) anchors the
            // user's place in the series.
            ReadingProgressEntity? anchor = null;
            foreach (var path in entry.FilePaths)
            {
                if (progressByPath.TryGetValue(path, out var match)
                    && (anchor is null || match.LastReadAt > anchor.LastReadAt))
                {
                    anchor = match;
                }
            }

            if (anchor is null)
            {
                continue;
            }

            string resumePath;
            int resumePage;

            if (!IsIssueRead(anchor) && !readFilePaths.Contains(anchor.ContentId))
            {
                // Still in the middle of an issue: resume exactly where we left off.
                resumePath = anchor.ContentId;
                resumePage = anchor.CurrentPage;
            }
            else
            {
                // The most recent issue is finished. Resume at the next issue that
                // has not been completed yet. If none remain, the series is fully
                // caught up and should drop off Continue Reading.
                var next = FindNextUnreadIssue(entry.FilePaths, anchor.ContentId, progressByPath, readFilePaths);
                if (next is null)
                {
                    continue;
                }

                resumePath = next;
                resumePage = progressByPath.TryGetValue(next, out var nextProgress)
                    ? nextProgress.CurrentPage
                    : 0;
            }

            var card = ToCard(entry.Summary);
            card.ResumeFilePath = resumePath;
            card.ResumePage = resumePage;
            card.LastReadUtc = anchor.LastReadAt;
            cards.Add((card, anchor.LastReadAt));
        }

        return cards
            .OrderByDescending(c => c.LastReadAt)
            .Take(RowCap)
            .Select(c => c.Card)
            .ToList();
    }

    /// <summary>
    /// Returns the next issue the user should resume, preferring the first issue
    /// (in series order) after <paramref name="completedPath"/> that is not
    /// completed. If every later issue is finished, falls back to the first
    /// not-completed issue anywhere in the series so that series with earlier
    /// unread issues (for example when issues were read out of order) still stay
    /// in Continue Reading. Returns null only when every issue is completed.
    /// </summary>
    private static string? FindNextUnreadIssue(
        IReadOnlyList<string> orderedPaths,
        string completedPath,
        IReadOnlyDictionary<string, ReadingProgressEntity> progressByPath,
        IReadOnlySet<string> readFilePaths)
    {
        var startIndex = -1;
        for (var i = 0; i < orderedPaths.Count; i++)
        {
            if (string.Equals(orderedPaths[i], completedPath, StringComparison.OrdinalIgnoreCase))
            {
                startIndex = i;
                break;
            }
        }

        for (var i = startIndex + 1; i < orderedPaths.Count; i++)
        {
            var path = orderedPaths[i];
            // Unread (no progress and not marked read) or started-but-not-finished
            // issues qualify.
            if (!IsPathRead(path, progressByPath, readFilePaths))
            {
                return path;
            }
        }

        // No unread issue remains after the completed anchor. Fall back to the
        // earliest not-completed issue in the series so the series is not dropped
        // from Continue Reading while unread issues still exist.
        for (var i = 0; i <= startIndex && i < orderedPaths.Count; i++)
        {
            var path = orderedPaths[i];
            if (!IsPathRead(path, progressByPath, readFilePaths))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether the issue at <paramref name="path"/> should be treated
    /// as read. An issue counts as read when it is marked read via the file read
    /// flag or when its per-user reading progress reports completion.
    /// </summary>
    private static bool IsPathRead(
        string path,
        IReadOnlyDictionary<string, ReadingProgressEntity> progressByPath,
        IReadOnlySet<string> readFilePaths)
    {
        if (readFilePaths.Contains(path))
        {
            return true;
        }

        return progressByPath.TryGetValue(path, out var progress) && IsIssueRead(progress);
    }

    /// <summary>
    /// An issue counts as read once the user has completed it. A record is
    /// treated as read when it has an explicit <see cref="ReadingProgressEntity.CompletedAt"/>
    /// timestamp or its progress has reached the end (<see cref="ReadingProgressEntity.PercentComplete"/>
    /// at 100 or more). The percent check covers issues that reached the final
    /// page but never received an explicit completion mark (for example the last
    /// issue of a series read in webcomic mode), so a fully-read series is not
    /// kept on Continue Reading.
    /// </summary>
    private static bool IsIssueRead(ReadingProgressEntity progress)
        => progress.CompletedAt is not null || progress.PercentComplete >= 100.0;

    private static List<OverviewSeriesCard> BuildSeriesUpdates(
        IReadOnlyList<SeriesOverviewEntry> entries,
        DateTime cutoff)
    {
        // A new file was added to a series that already had at least one earlier
        // issue: the newest file is within the window (LatestCreatedAt >= cutoff)
        // and it was added after the series' first issue (EarliestCreatedAt <
        // LatestCreatedAt). Keying the window off LatestCreatedAt means a series
        // stays here for 30 days from its most recent addition — not from when it
        // first appeared — so adding a new issue always refreshes the 30-day
        // window even for a series that first appeared within the last 30 days.
        // A brand-new series with only its initial issue(s) (no later addition)
        // is excluded here and belongs in Newly Added instead.
        return entries
            .Where(e => e.LatestCreatedAt >= cutoff && e.EarliestCreatedAt < e.LatestCreatedAt)
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
