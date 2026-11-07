namespace ComicMaintainer.Core.Models;

/// <summary>
/// One folder of a <see cref="CombinableFolderGroup"/>: a directory holding
/// tracked files of the group's series.
/// </summary>
public sealed class CombinableFolder
{
    public string Directory { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public DateTime NewestFileAddedAt { get; set; }
    public DateTime OldestFileAddedAt { get; set; }
    public DateTime NewestFileModifiedAt { get; set; }
    public List<string> SampleFileNames { get; set; } = new();

    /// <summary>Every tracked file of the folder; these are what a combine moves.</summary>
    public List<string> FilePaths { get; set; } = new();
}

/// <summary>
/// Folders that look like they hold the same series (and volume), so the user
/// can combine them into one.
/// </summary>
public sealed class CombinableFolderGroup
{
    /// <summary>
    /// Prefix of synthetic group keys (<c>series:&lt;seriesId&gt;</c>) built
    /// from the series library's grouping. A combine request may use one even
    /// when the group is not in the current combinable-folder list.
    /// </summary>
    public const string SeriesGroupKeyPrefix = "series:";

    public string GroupKey { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public string? Volume { get; set; }
    public int TotalFileCount { get; set; }
    public string SuggestedDestinationDirectory { get; set; } = string.Empty;
    public string SuggestionReason { get; set; } = string.Empty;
    public List<CombinableFolder> Folders { get; set; } = new();
}

/// <summary>
/// What to combine: the folder to keep and the folders to move out of. When
/// <see cref="GroupKey"/> is empty the group is inferred from the folders;
/// when <see cref="SourceDirectories"/> is empty every other folder of the
/// group is a source.
/// </summary>
public sealed class FolderCombineRequest
{
    public string? GroupKey { get; set; }
    public string DestinationDirectory { get; set; } = string.Empty;
    public List<string>? SourceDirectories { get; set; }
}

/// <summary>One planned file move of a folder combine.</summary>
public sealed class FolderCombineMove
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>True when the name was taken in the destination, so the file gets a numbered name.</summary>
    public bool HadConflict { get; set; }

    /// <summary>True when the file is already at its destination path and is left alone.</summary>
    public bool Skipped { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// What happens to a source folder once its tracked files have been moved
/// out. The folder is removed only when everything left in it is known to be
/// disposable (the folder's own <c>ComicInfo.xml</c> and cover image,
/// operating-system folder metadata, empty subfolders); anything else is
/// listed in <see cref="Leftovers"/> and the folder is kept.
/// </summary>
public sealed class FolderCombineSourceCleanup
{
    /// <summary>At most this many entries are listed in <see cref="RemovableFiles"/> and <see cref="Leftovers"/>.</summary>
    public const int MaxListedEntries = 25;

    public string Directory { get; set; } = string.Empty;

    /// <summary>True when the folder will be (or was) deleted.</summary>
    public bool Removed { get; set; }

    /// <summary>Why the folder is kept; null when it is removed.</summary>
    public string? KeptReason { get; set; }

    /// <summary>Disposable files deleted with the folder, relative to it (first <see cref="MaxListedEntries"/>).</summary>
    public List<string> RemovableFiles { get; set; } = new();

    public int RemovableFileCount { get; set; }

    /// <summary>Files that keep the folder, relative to it (first <see cref="MaxListedEntries"/>).</summary>
    public List<string> Leftovers { get; set; } = new();

    public int LeftoverCount { get; set; }
}

/// <summary>The planned moves and source-folder cleanup of a folder combine.</summary>
public sealed class FolderCombinePlan
{
    public string Destination { get; set; } = string.Empty;
    public List<string> SourceDirectories { get; set; } = new();
    public List<FolderCombineMove> Moves { get; set; } = new();

    /// <summary>
    /// Per source folder, what will be left once the planned moves succeed
    /// and whether the folder will be removed. A move that fails at run time
    /// leaves its file behind, which then keeps the folder.
    /// </summary>
    public List<FolderCombineSourceCleanup> SourceCleanup { get; set; } = new();
}

/// <summary>A built plan, or why the request cannot be planned.</summary>
public sealed record FolderCombinePlanResult(string? Error, FolderCombinePlan? Plan)
{
    public static FolderCombinePlanResult Failed(string error) => new(error, null);

    public static FolderCombinePlanResult Planned(FolderCombinePlan plan) => new(null, plan);
}

/// <summary>
/// The outcome of starting a folder combine: an error, or the plan and the id
/// of the background job doing the moves. <see cref="JobId"/> is null when
/// every move was skipped, so there was nothing to run.
/// </summary>
public sealed record FolderCombineStartResult(string? Error, FolderCombinePlan? Plan, Guid? JobId)
{
    public static FolderCombineStartResult Failed(string error) => new(error, null, null);
}
