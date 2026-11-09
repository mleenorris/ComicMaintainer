using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Keeps the email delivery history pointing at files after they move. A delivery records the
/// path it was queued for and, for a condensed book, the path of every issue in it; retries,
/// re-sends and the auto-send "already delivered" check read those paths back, so a stale one
/// fails, sends whatever now has that name, or sends an issue a second time.
/// </summary>
public partial class FileStoreService
{
    /// <summary>
    /// Repoints every delivery of the file at <paramref name="oldPath"/>, whatever its status.
    /// </summary>
    private static async Task MoveEmailDeliveriesAsync(
        ComicMaintainerDbContext dbContext,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in await dbContext.ComicEmailDeliveries
                     .Where(d => d.FilePath == oldPath)
                     .ToListAsync(cancellationToken))
        {
            delivery.FilePath = newPath;
        }

        // A member is compared exactly, as FilePath is above, so a search for its serialized
        // form finds every delivery that can change.
        var fragment = CondensedFilePathList.EncodeFragment(oldPath);
        await RepointCondensedMembersAsync(
            dbContext.ComicEmailDeliveries.Where(d => d.CondensedFilePaths != null && d.CondensedFilePaths.Contains(fragment)),
            member => string.Equals(member, oldPath, StringComparison.Ordinal) ? newPath : member,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Repoints every delivery of a file under <paramref name="oldPrefix"/>. The changes stay
    /// tracked by <paramref name="dbContext"/>; the caller saves them as part of the directory
    /// move.
    /// </summary>
    private static async Task MoveEmailDeliveriesUnderAsync(
        ComicMaintainerDbContext dbContext,
        string oldPrefix,
        Func<string, string> rebase,
        IReadOnlyCollection<string> cachedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in await RowsUnderAsync(dbContext.ComicEmailDeliveries, e => e.FilePath, oldPrefix, cachedPaths, cancellationToken))
        {
            delivery.FilePath = rebase(delivery.FilePath);
        }

        // The list is JSON, so a member can only be found in SQL by its serialized form, which
        // is compared exactly. Where the platform ignores case, a member under the directory
        // can be spelled differently, so every condensed delivery is checked instead.
        var candidates = dbContext.ComicEmailDeliveries.Where(d => d.CondensedFilePaths != null);
        if (LibraryPathValidator.PathComparison == StringComparison.Ordinal)
        {
            var fragment = CondensedFilePathList.EncodeFragment(oldPrefix);
            candidates = candidates.Where(d => d.CondensedFilePaths!.Contains(fragment));
        }

        await RepointCondensedMembersAsync(
            candidates,
            member => IsUnderPrefix(member, oldPrefix) ? rebase(member) : member,
            cancellationToken);
    }

    private static async Task RepointCondensedMembersAsync(
        IQueryable<ComicEmailDeliveryEntity> candidates,
        Func<string, string> repoint,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in await candidates.ToListAsync(cancellationToken))
        {
            var members = CondensedFilePathList.Parse(delivery.CondensedFilePaths);
            var repointed = members.Select(repoint).ToList();
            if (!members.SequenceEqual(repointed, StringComparer.Ordinal))
            {
                delivery.CondensedFilePaths = CondensedFilePathList.Serialize(repointed);
            }
        }
    }
}
