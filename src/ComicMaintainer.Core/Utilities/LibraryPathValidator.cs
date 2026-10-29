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

    // Case-insensitive on Windows and case-sensitive elsewhere.
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

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
    /// Returns true when the already resolved <paramref name="fullPath"/> is the
    /// resolved <paramref name="directory"/> itself or lives beneath it.
    /// </summary>
    public static bool IsWithin(string fullPath, string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && IsSameOrUnder(fullPath, ResolveRealPath(Path.GetFullPath(directory)));

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
    /// itself or lives beneath it. Trailing separators are ignored, a sibling that
    /// merely shares the directory's name as a prefix (<c>/comics2</c> next to
    /// <c>/comics</c>) is outside, and case is ignored only on Windows. A file
    /// system root (the Unix <c>/</c>, a Windows drive root such as <c>C:\</c>, or a
    /// UNC share root) contains nothing: allowing it would make the check
    /// meaningless, so it fails closed.
    /// </summary>
    public static bool IsSameOrUnder(string fullPath, string directory)
    {
        var root = directory.TrimEnd(Separators);
        if (root.Length == 0 || IsFileSystemRoot(directory, root))
            return false;

        return string.Equals(fullPath.TrimEnd(Separators), root, PathComparison)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsFileSystemRoot(string directory, string trimmedDirectory)
    {
        var pathRoot = Path.GetPathRoot(directory);
        return !string.IsNullOrEmpty(pathRoot)
            && string.Equals(trimmedDirectory, pathRoot.TrimEnd(Separators), PathComparison);
    }

    /// <summary>
    /// Returns the physical path a (possibly linked) path ultimately refers to,
    /// resolving each path segment from the root down. A link is replaced by its
    /// target, which is resolved the same way (links in its directories, chained
    /// links, and ".." applied to where the link really leads), as the OS does.
    /// Segments that do not exist, or are not links, are kept verbatim.
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

        return resolved;
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
