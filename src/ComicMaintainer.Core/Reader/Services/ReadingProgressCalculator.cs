using ComicMaintainer.Core.Reader.Models;

namespace ComicMaintainer.Core.Reader.Services;

/// <summary>
/// Pure calculation helpers for reading progress state.
/// Has no dependencies so it can be tested without any infrastructure.
/// </summary>
public static class ReadingProgressCalculator
{
    /// <summary>
    /// Computes the percentage of content read, clamped to the range 0–100.
    /// Returns 0 when <paramref name="totalPages"/> is zero or negative.
    /// </summary>
    /// <param name="currentPage">1-based current page.</param>
    /// <param name="totalPages">Total number of pages.</param>
    public static double CalculatePercentComplete(int currentPage, int totalPages)
    {
        if (totalPages <= 0) return 0;
        var pct = (double)currentPage / totalPages * 100.0;
        return Math.Clamp(pct, 0.0, 100.0);
    }

    /// <summary>
    /// Returns <c>true</c> when the reader has reached or passed the last page
    /// (i.e., <paramref name="currentPage"/> &gt;= <paramref name="totalPages"/>).
    /// </summary>
    public static bool IsCompleted(int currentPage, int totalPages)
    {
        return totalPages > 0 && currentPage >= totalPages;
    }

    /// <summary>
    /// Applies new progress values to an existing <see cref="ReadingProgress"/>,
    /// updating <see cref="ReadingProgress.PercentComplete"/>,
    /// <see cref="ReadingProgress.LastReadAt"/>, and optionally
    /// <see cref="ReadingProgress.CompletedAt"/> when the content is finished.
    /// </summary>
    public static void ApplyProgress(ReadingProgress progress, int newPage, DateTime utcNow)
    {
        progress.CurrentPage = newPage;
        progress.PercentComplete = CalculatePercentComplete(newPage, progress.TotalPages);
        progress.LastReadAt = utcNow;

        if (IsCompleted(newPage, progress.TotalPages) && progress.CompletedAt is null)
        {
            progress.CompletedAt = utcNow;
        }
    }
}
