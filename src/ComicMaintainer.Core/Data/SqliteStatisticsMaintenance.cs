using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Data;

/// <summary>
/// Keeps SQLite's query-planner statistics (<c>sqlite_stat1</c>) current.
/// </summary>
/// <remarks>
/// Without statistics the planner guesses how selective every index is, so it can pick a
/// low-value index (or none) for the library listings. <c>PRAGMA optimize</c> runs
/// <c>ANALYZE</c> only on the tables whose statistics are missing or out of date, and
/// <c>analysis_limit</c> makes that analysis approximate, so a run takes milliseconds even for
/// a large library and is cheap enough to repeat on every scheduled database cleanup.
/// </remarks>
public static class SqliteStatisticsMaintenance
{
    /// <summary>
    /// The statements run by <see cref="OptimizeAsync"/>. The 0x10002 mask asks
    /// <c>optimize</c> to consider every table, not just the ones this connection has queried,
    /// which is what a periodic run from a fresh connection needs.
    /// </summary>
    internal const string OptimizeSql = "PRAGMA analysis_limit=1000; PRAGMA optimize=0x10002;";

    /// <summary>
    /// Refreshes the planner statistics. Returns <c>false</c> without doing anything when the
    /// database is not SQLite (for example the in-memory provider used by tests).
    /// </summary>
    public static async Task<bool> OptimizeAsync(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!dbContext.Database.IsSqlite())
        {
            return false;
        }

        await dbContext.Database.ExecuteSqlRawAsync(OptimizeSql, cancellationToken);
        return true;
    }
}
