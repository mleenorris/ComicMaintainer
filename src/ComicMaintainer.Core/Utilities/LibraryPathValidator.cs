namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Containment checks for library paths. Every check applies the same rule
/// (<see cref="IsSameOrUnder"/>) to the set of allowed roots its caller passes in.
/// <see cref="TryResolveWithin"/> is link-aware: symlinks and junctions are
/// resolved first — on the path itself, on every ancestor directory and inside
/// link targets — so a link inside an allowed root cannot smuggle in an external target.
/// Callers should use the resolved path it returns for the file system operations
/// that follow the check. <see cref="IsLexicallyWithin"/> only normalizes the path,
/// for callers that track or route paths rather than open them.
/// </summary>
public static class LibraryPathValidator
{
    // Same budget as the Linux kernel (MAXSYMLINKS) and .NET's own link
    // resolution: a path needing more links fails to open (ELOOP) anyway.
    private const int MaxLinksFollowed = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Resolves <paramref name="path"/> to its physical location and verifies it
    /// is inside one of <paramref name="allowedDirectories"/> (themselves resolved).
    /// </summary>
    public static bool TryResolveWithin(string? path, out string resolvedPath, params string?[] allowedDirectories)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var resolved = ResolveRealPath(Path.GetFullPath(path));

            foreach (var allowedDirectory in allowedDirectories)
            {
                if (IsWithin(resolved, allowedDirectory))
                {
                    resolvedPath = resolved;
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Like <see cref="TryResolveWithin"/>, but for an operation on the directory entry
    /// <paramref name="path"/> names, such as deleting it: both what the entry leads to and
    /// the directory holding it must be inside one of <paramref name="allowedDirectories"/>.
    /// <paramref name="entryPath"/> is the entry in that resolved directory, so acting on it
    /// affects a link rather than the file the link points to. A path ending in a directory
    /// separator or "." is rejected, since the OS would follow a link it ends in, and so is
    /// any ".." segment: the OS applies it after following links, so normalizing it away
    /// first could name a different entry.
    /// </summary>
    public static bool TryResolveEntryWithin(string? path, out string entryPath, params string?[] allowedDirectories)
    {
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path) ||
            Path.EndsInDirectorySeparator(path) ||
            Path.GetFileName(path) == "." ||
            path.Split(Separators).Contains(".."))
            return false;

        if (!TryResolveWithin(path, out _, allowedDirectories))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(name) ||
                !TryResolveWithin(Path.GetDirectoryName(fullPath), out var directory, allowedDirectories))
                return false;

            entryPath = Path.Join(directory, name);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the already resolved <paramref name="fullPath"/> is the
    /// resolved <paramref name="directory"/> itself or lives beneath it.
    /// </summary>
    /// <remarks>
    /// The paths are compared exactly, on every platform. Resolved paths, and the paths the
    /// OS reports for an opened directory, are spelled as the file system stores them, so a
    /// differently cased request on a case-insensitive volume still matches, while a distinct
    /// "LIBRARY" next to "library" in a case-sensitive directory does not.
    /// </remarks>
    public static bool IsWithin(string fullPath, string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && IsSameOrUnder(fullPath, ResolveRealPath(Path.GetFullPath(directory)), StringComparison.Ordinal);

    /// <summary>
    /// Lexical counterpart of <see cref="TryResolveWithin"/>: normalizes
    /// <paramref name="path"/> and each of <paramref name="allowedDirectories"/> with
    /// <see cref="Path.GetFullPath(string)"/>, which collapses "." and ".." segments,
    /// without resolving links, and returns true when the path is inside one of them.
    /// Blank directories are not configured and contain nothing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, or it or a configured directory cannot be
    /// parsed, so callers can tell an unusable path from one that is merely outside.
    /// </exception>
    public static bool IsLexicallyWithin(string path, params string?[] allowedDirectories)
    {
        var fullPath = Path.GetFullPath(path);

        foreach (var allowedDirectory in allowedDirectories)
        {
            if (!string.IsNullOrWhiteSpace(allowedDirectory)
                && IsSameOrUnder(fullPath, Path.GetFullPath(allowedDirectory)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The containment rule shared by every check: true when the normalized
    /// <paramref name="fullPath"/> is the normalized <paramref name="directory"/>
    /// itself or lives beneath it. Trailing separators are ignored, and a sibling that
    /// merely shares the directory's name as a prefix (<c>/comics2</c> next to
    /// <c>/comics</c>) is outside. A directory made only of separators, such as the
    /// Unix root <c>/</c>, contains nothing: allowing it would make the check
    /// meaningless, so it fails closed. A Windows drive root such as <c>D:\</c> or a
    /// UNC share such as <c>\\server\comics</c> is a usable library root and contains
    /// its paths.
    /// </summary>
    /// <remarks>
    /// These paths are compared with <see cref="PathComparison"/>, since they need not be
    /// spelled as the file system stores them. <see cref="IsWithin"/> applies the same rule
    /// to resolved paths, which it compares exactly.
    /// </remarks>
    public static bool IsSameOrUnder(string fullPath, string directory) =>
        IsSameOrUnder(fullPath, directory, PathComparison);

    private static bool IsSameOrUnder(string fullPath, string directory, StringComparison comparison)
    {
        var root = directory.TrimEnd(Separators);
        if (root.Length == 0)
            return false;

        return string.Equals(fullPath.TrimEnd(Separators), root, comparison)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// How library paths that are not spelled as the file system stores them are compared:
    /// ignoring case on Windows, ordinally elsewhere.
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Returns the physical path a (possibly linked) path ultimately refers to,
    /// resolving each path segment from the root down. A link is replaced by its
    /// target, which is resolved the same way (links in its directories, chained
    /// links, and ".." applied to where the link really leads), as the OS does.
    /// Segments that do not exist, or are not links, are kept verbatim, except that on
    /// Windows and macOS the existing directories are spelled as the file system stores them.
    /// </summary>
    /// <exception cref="IOException">
    /// More than 40 links have to be followed, e.g. because of a link loop.
    /// </exception>
    public static string ResolveRealPath(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        var resolved = Path.GetPathRoot(path)!;
        var pending = new Stack<string>();
        PushSegments(pending, path[resolved.Length..]);
        var linksFollowed = 0;

        while (pending.TryPop(out var segment))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var candidate = Path.Combine(resolved, segment);
            var target = ReadLinkTarget(candidate);
            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            // One shared budget for the whole path, so links that fan out into
            // further links cannot multiply the work.
            if (++linksFollowed > MaxLinksFollowed)
                throw new IOException("Too many levels of symbolic links.");

            // An absolute target restarts at its root; a relative one continues
            // from the link's (already physical) directory.
            if (Path.IsPathRooted(target))
            {
                resolved = Path.GetPathRoot(target)!;
                target = target[resolved.Length..];
            }

            PushSegments(pending, target);
        }

        return ToStoredSpelling(resolved);
    }

    // On Windows and macOS a path may be spelled in any case the volume ignores, so the
    // deepest existing directory is replaced by the path the OS reports for it once opened.
    // That is the spelling on disk, which a containment check can compare exactly.
    private static string ToStoredSpelling(string path)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return path;

        var existing = path;
        while (!Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
            if (existing is null)
                return path;
        }

        var stored = NativeFileSystem.TryReadDirectorySpelling(existing);
        return stored is null ? path : Path.Join(stored, path.AsSpan(existing.Length));
    }

    private static void PushSegments(Stack<string> pending, string relativePath)
    {
        var segments = relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
            pending.Push(segments[i]);
    }

    // The link's own target, not followed any further; null when the entry is
    // not a link. Path.Exists is also true for broken and cyclic links.
    private static string? ReadLinkTarget(string path) =>
        Path.Exists(path) ? new FileInfo(path).LinkTarget : null;
}
