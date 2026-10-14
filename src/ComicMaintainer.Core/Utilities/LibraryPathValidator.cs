namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Link-aware containment checks for library paths. Symlinks and junctions are
/// resolved first — both on the path itself and on every ancestor directory —
/// so a link inside an allowed root cannot smuggle in an external target.
/// Callers should use the resolved path returned by <see cref="TryResolveWithin"/>
/// for the file system operations that follow the check.
/// </summary>
public static class LibraryPathValidator
{
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
    /// resolving each path segment from the root down. Segments that do not
    /// exist, or are not links, are kept verbatim.
    /// </summary>
    public static string ResolveRealPath(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            // Root of the volume: nothing above it can be a link.
            return ResolveLinkTarget(fullPath) ?? fullPath;
        }

        var candidate = Path.Combine(ResolveRealPath(parent), Path.GetFileName(fullPath));
        return ResolveLinkTarget(candidate) ?? candidate;
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.Exists(path)
                    ? File.ResolveLinkTarget(path, returnFinalTarget: true)
                    : null;

            return target is null ? null : Path.GetFullPath(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unresolvable (broken or cyclic link, or no permission): treat the
            // path as-is; the containment check still has to pass.
            return null;
        }
    }
}
