namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Link-aware containment checks for library paths. Symlinks and junctions are
/// resolved first — on the path itself, on every ancestor directory and inside
/// link targets — so a link inside an allowed root cannot smuggle in an external target.
/// Callers should use the resolved path returned by <see cref="TryResolveWithin"/>
/// for the file system operations that follow the check.
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
    /// Returns true when the already resolved <paramref name="fullPath"/> is the
    /// resolved <paramref name="directory"/> itself or lives beneath it.
    /// </summary>
    public static bool IsWithin(string fullPath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var root = ResolveRealPath(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length == 0)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root, comparison)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison);
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
