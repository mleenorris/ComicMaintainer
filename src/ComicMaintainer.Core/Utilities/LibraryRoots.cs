namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The link-aware checks of <see cref="LibraryPathValidator"/> against one set of allowed
/// directories. Each allowed directory is resolved once, and a directory found not to be a link
/// is not asked about again, so checking many paths, as when one request names thousands of
/// files, costs file system work per distinct directory rather than per path and ancestor.
/// </summary>
/// <remarks>
/// An instance is for one short unit of work on one thread. It does not notice a directory
/// that becomes a link after being checked, so whatever acts on a path later must check it again.
/// </remarks>
public sealed class LibraryRoots
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string?[] _allowedDirectories;
    private readonly string?[] _resolvedDirectories;
    private readonly HashSet<string> _knownNotLinks = new(StringComparer.Ordinal);

    public LibraryRoots(params string?[] allowedDirectories)
    {
        _allowedDirectories = allowedDirectories;
        _resolvedDirectories = new string?[allowedDirectories.Length];
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its physical location and verifies it
    /// is inside one of the allowed directories (themselves resolved).
    /// </summary>
    public bool TryResolveWithin(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var resolved = LibraryPathValidator.ResolveRealPath(Path.GetFullPath(path), _knownNotLinks);

            for (var index = 0; index < _allowedDirectories.Length; index++)
            {
                if (IsWithinAllowedDirectory(resolved, index))
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
    /// the directory holding it must be inside one of the allowed directories.
    /// <paramref name="entryPath"/> is the entry in that resolved directory, so acting on it
    /// affects a link rather than the file the link points to. A path ending in a directory
    /// separator or "." is rejected, since the OS would follow a link it ends in, and so is
    /// any ".." segment: the OS applies it after following links, so normalizing it away
    /// first could name a different entry.
    /// </summary>
    public bool TryResolveEntryWithin(string? path, out string entryPath)
    {
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path) ||
            Path.EndsInDirectorySeparator(path) ||
            Path.GetFileName(path) == "." ||
            path.Split(Separators).Contains(".."))
            return false;

        if (!TryResolveWithin(path, out _))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(name) ||
                !TryResolveWithin(Path.GetDirectoryName(fullPath), out var directory))
                return false;

            entryPath = Path.Join(directory, name);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private bool IsWithinAllowedDirectory(string resolvedPath, int index)
    {
        var directory = _allowedDirectories[index];
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var resolvedDirectory = _resolvedDirectories[index]
            ??= LibraryPathValidator.ResolveRealPath(Path.GetFullPath(directory), _knownNotLinks);

        return LibraryPathValidator.IsSameOrUnder(resolvedPath, resolvedDirectory, StringComparison.Ordinal);
    }
}
