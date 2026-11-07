using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Finds folders that hold the same series and combines them: moves the
/// tracked files of the source folders into the destination folder, removes
/// source folders that are left with nothing worth keeping, remembers the
/// source names as aliases of the destination series and queues a
/// normalize-and-rename of the combined folder.
/// </summary>
public interface IFolderCombineService
{
    /// <summary>
    /// The current combinable-folder groups. Cached until the file store or
    /// the series metadata cache changes; callers must not modify the result.
    /// </summary>
    Task<IReadOnlyList<CombinableFolderGroup>> GetCombinableFolderGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many folders could be combined away: every folder of a group
    /// beyond the one that would be kept.
    /// </summary>
    Task<int> GetCombinableFolderCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="request"/> against fresh library data and
    /// plans it without changing anything, including which source folders
    /// will be removed and what would keep the others.
    /// </summary>
    Task<FolderCombinePlanResult> PreviewAsync(FolderCombineRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans <paramref name="request"/> like <see cref="PreviewAsync"/> and
    /// starts a background job that carries it out. Source folders that still
    /// hold anything other than disposable files when the moves are done are
    /// kept and reported in the job's results.
    /// </summary>
    Task<FolderCombineStartResult> StartAsync(FolderCombineRequest request, CancellationToken cancellationToken = default);
}
