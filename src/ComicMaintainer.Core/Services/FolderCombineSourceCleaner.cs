using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Decides whether a folder-combine source folder may be removed once its
/// tracked files have been moved out, and removes it without ever deleting
/// anything the user may want to keep.
/// </summary>
/// <remarks>
/// <para>Only these entries are disposable: the folder's own
/// <c>ComicInfo.xml</c> and cover image (<c>cover.*</c> or <c>folder.*</c>)
/// directly inside it, operating-system folder metadata (<c>.DS_Store</c>,
/// <c>Thumbs.db</c>, <c>desktop.ini</c>) at any depth, and subfolders that hold
/// nothing else. Every other file (a PDF, an EPUB, a <c>.cb7</c> volume, an
/// untracked archive, a <c>series.json</c>, an artbook's images, a Suwayomi
/// chapter being staged), every link and every configured library folder is a
/// leftover that keeps the folder.</para>
/// <para>Removal deletes the disposable files, then the emptied subfolders and
/// the folder itself non-recursively, so anything that appears in the meantime
/// makes the removal fail and the folder is kept. Links are never followed.</para>
/// </remarks>
internal static class FolderCombineSourceCleaner
{
    private static readonly HashSet<string> OperatingSystemMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini"
    };

    private static readonly HashSet<string> CoverImageBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "folder"
    };

    private static readonly HashSet<string> CoverImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".jxl", ".bmp"
    };

    // Hidden and system entries must be seen too: a staged Suwayomi chapter is
    // a dot-file, and a hidden PDF is still the user's file.
    private static readonly EnumerationOptions ListEverything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    internal const string ProtectedFolderReason =
        "It is a configured library folder (the watched, duplicates or Suwayomi folder) or the destination.";

    internal const string LinkReason = "It is a link; links are never removed.";

    internal const string LeftoverReason = "It still holds files that are not disposable.";

    /// <summary>
    /// Whether <paramref name="fileName"/> is disposable at
    /// <paramref name="depth"/> (0 for a file directly in the source folder).
    /// </summary>
    internal static bool IsDisposableFile(string fileName, int depth)
    {
        if (OperatingSystemMetadataNames.Contains(fileName))
        {
            return true;
        }

        if (depth > 0)
        {
            return false;
        }

        return string.Equals(fileName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase)
            || (CoverImageBaseNames.Contains(Path.GetFileNameWithoutExtension(fileName))
                && CoverImageExtensions.Contains(Path.GetExtension(fileName)));
    }

    /// <summary>
    /// Predicts what is left in <paramref name="directory"/> once
    /// <paramref name="departingFiles"/> have been moved out and the folders in
    /// <paramref name="removedDirectories"/> (nested source folders assessed
    /// first) are gone, and whether the folder will then be removed. Changes
    /// nothing on disk.
    /// </summary>
    public static FolderCombineSourceCleanup Assess(
        string directory,
        IReadOnlySet<string> departingFiles,
        IReadOnlySet<string> removedDirectories,
        IReadOnlyCollection<string> protectedDirectories)
    {
        var cleanup = new FolderCombineSourceCleanup { Directory = directory };
        if (!Directory.Exists(directory))
        {
            // Nothing to remove: reported as removable so the preview does not
            // claim it will be kept.
            cleanup.Removable = true;
            return cleanup;
        }

        if (GetBlockingReason(directory, protectedDirectories) is { } reason)
        {
            cleanup.KeptReason = reason;
            return cleanup;
        }

        var scan = Scan(directory, departingFiles, removedDirectories, protectedDirectories);
        return ToCleanup(directory, scan);
    }

    /// <summary>
    /// Removes <paramref name="directory"/> when nothing but disposable
    /// entries is left in it, and says what was deleted or what kept it. Call
    /// it while holding the library mutation lock so no other service moves a
    /// file into the folder meanwhile.
    /// </summary>
    public static FolderCombineSourceCleanup Remove(string directory, IReadOnlyCollection<string> protectedDirectories)
    {
        var cleanup = new FolderCombineSourceCleanup { Directory = directory };
        if (!Directory.Exists(directory))
        {
            cleanup.Removable = true;
            return cleanup;
        }

        if (GetBlockingReason(directory, protectedDirectories) is { } reason)
        {
            cleanup.KeptReason = reason;
            return cleanup;
        }

        var scan = Scan(
            directory,
            new HashSet<string>(PathComparer),
            new HashSet<string>(PathComparer),
            protectedDirectories);
        cleanup = ToCleanup(directory, scan);
        if (!cleanup.Removable)
        {
            return cleanup;
        }

        try
        {
            foreach (var file in scan.DisposableFiles)
            {
                File.Delete(file);
            }

            // Deepest first, and never recursively: a folder that gained an
            // entry since the scan fails to delete and is kept with it.
            foreach (var subdirectory in scan.EmptySubdirectories)
            {
                Directory.Delete(subdirectory, recursive: false);
            }

            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cleanup.Removable = false;
            cleanup.KeptReason = $"It could not be removed: {ex.Message}";
        }

        return cleanup;
    }

    /// <summary>
    /// Formats the job-result message for a folder that was kept.
    /// </summary>
    public static string DescribeKeptFolder(FolderCombineSourceCleanup cleanup)
    {
        var message = "Source folder kept. " + (cleanup.KeptReason ?? LeftoverReason);
        if (cleanup.LeftoverCount == 0)
        {
            return message;
        }

        var listed = string.Join(", ", cleanup.Leftovers);
        var more = cleanup.LeftoverCount - cleanup.Leftovers.Count;
        return more > 0
            ? $"{message} Remaining: {listed} and {more} more."
            : $"{message} Remaining: {listed}.";
    }

    private static string? GetBlockingReason(string directory, IReadOnlyCollection<string> protectedDirectories)
    {
        if (IsLink(new DirectoryInfo(directory)))
        {
            return LinkReason;
        }

        // A protected folder nested deeper is found by the scan and listed as a
        // leftover, so only the folder itself is checked here.
        return protectedDirectories.Any(protectedDirectory => IsSamePath(protectedDirectory, directory))
            ? ProtectedFolderReason
            : null;
    }

    private sealed class ScanResult
    {
        public List<string> DisposableFiles { get; } = new();

        /// <summary>Subfolders that hold nothing but disposable entries, deepest first.</summary>
        public List<string> EmptySubdirectories { get; } = new();

        public List<string> Leftovers { get; } = new();

        public int LeftoverCount { get; set; }

        public string? KeptReason { get; set; }

        public void AddLeftover(string entry)
        {
            LeftoverCount++;
            if (Leftovers.Count < FolderCombineSourceCleanup.MaxListedEntries)
            {
                Leftovers.Add(entry);
            }
        }
    }

    private static ScanResult Scan(
        string directory,
        IReadOnlySet<string> departingFiles,
        IReadOnlySet<string> removedDirectories,
        IReadOnlyCollection<string> protectedDirectories)
    {
        var result = new ScanResult();
        ScanDirectory(new DirectoryInfo(directory), directory, depth: 0, departingFiles, removedDirectories, protectedDirectories, result);
        return result;
    }

    /// <summary>
    /// Scans one folder level and returns true when nothing in it (or below it)
    /// is a leftover.
    /// </summary>
    private static bool ScanDirectory(
        DirectoryInfo directory,
        string root,
        int depth,
        IReadOnlySet<string> departingFiles,
        IReadOnlySet<string> removedDirectories,
        IReadOnlyCollection<string> protectedDirectories,
        ScanResult result)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos("*", ListEverything).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddLeftover(Relative(root, directory.FullName) + Path.DirectorySeparatorChar);
            result.KeptReason ??= $"Part of it could not be read: {ex.Message}";
            return false;
        }

        var clean = true;
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsLink(entry))
            {
                result.AddLeftover(Relative(root, entry.FullName));
                clean = false;
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                if (removedDirectories.Contains(subdirectory.FullName))
                {
                    continue;
                }

                if (protectedDirectories.Any(protectedDirectory => IsSameOrUnder(protectedDirectory, subdirectory.FullName)))
                {
                    result.AddLeftover(Relative(root, subdirectory.FullName) + Path.DirectorySeparatorChar);
                    clean = false;
                    continue;
                }

                if (ScanDirectory(subdirectory, root, depth + 1, departingFiles, removedDirectories, protectedDirectories, result))
                {
                    result.EmptySubdirectories.Add(subdirectory.FullName);
                }
                else
                {
                    clean = false;
                }

                continue;
            }

            if (departingFiles.Contains(entry.FullName))
            {
                continue;
            }

            if (IsDisposableFile(entry.Name, depth))
            {
                result.DisposableFiles.Add(entry.FullName);
            }
            else
            {
                result.AddLeftover(Relative(root, entry.FullName));
                clean = false;
            }
        }

        return clean;
    }

    private static FolderCombineSourceCleanup ToCleanup(string directory, ScanResult scan)
    {
        var removable = scan.LeftoverCount == 0;
        return new FolderCombineSourceCleanup
        {
            Directory = directory,
            Removable = removable,
            KeptReason = removable ? null : scan.KeptReason ?? LeftoverReason,
            DisposableFiles = scan.DisposableFiles
                .Select(file => Relative(directory, file))
                .Take(FolderCombineSourceCleanup.MaxListedEntries)
                .ToList(),
            DisposableFileCount = scan.DisposableFiles.Count,
            Leftovers = scan.Leftovers,
            LeftoverCount = scan.LeftoverCount
        };
    }

    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Compares library paths the way <see cref="LibraryPathValidator"/> does:
    /// ignoring case on Windows, ordinally elsewhere.
    /// </summary>
    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsSameOrUnder(string path, string directory) =>
        LibraryPathValidator.IsSameOrUnder(Path.GetFullPath(path), Path.GetFullPath(directory));

    private static bool IsSamePath(string path, string other) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            LibraryPathValidator.PathComparison);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}
