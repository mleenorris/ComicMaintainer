namespace ComicMaintainer.Core.Models;

/// <summary>
/// The set of tracked files a bulk read/unread change applies to; see
/// <c>IFileStoreService.MarkScopeReadAsync</c>.
/// </summary>
/// <remarks>
/// The scope is resolved on the server against the <c>ComicFiles</c> table, so a whole-library
/// change never needs the client to download and post back every path.
/// </remarks>
public sealed record FileReadScope
{
    private FileReadScope(FileReadScopeKind kind, string? folderKey, IReadOnlyCollection<string>? filePaths)
    {
        Kind = kind;
        FolderKey = folderKey;
        FilePaths = filePaths;
    }

    public FileReadScopeKind Kind { get; }

    /// <summary>For <see cref="FileReadScopeKind.Folder"/>: the folder key (relative to the watched directory).</summary>
    public string? FolderKey { get; }

    /// <summary>For <see cref="FileReadScopeKind.Files"/>: the file paths (for example every issue of a series).</summary>
    public IReadOnlyCollection<string>? FilePaths { get; }

    /// <summary>Every tracked file.</summary>
    public static FileReadScope Library { get; } = new(FileReadScopeKind.Library, null, null);

    /// <summary>The tracked files directly inside one folder (the same set the folder's file list shows).</summary>
    public static FileReadScope Folder(string folderKey) =>
        new(FileReadScopeKind.Folder, folderKey ?? string.Empty, null);

    /// <summary>The tracked files among <paramref name="filePaths"/>; untracked paths are ignored.</summary>
    public static FileReadScope Files(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        return new(FileReadScopeKind.Files, null, filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList());
    }
}

/// <summary>What a <see cref="FileReadScope"/> selects.</summary>
public enum FileReadScopeKind
{
    Library,
    Folder,
    Files
}
