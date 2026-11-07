using System.Text.RegularExpressions;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The series a comic's location implies, for files without a series in their metadata.
/// <para>
/// A library is organized as one folder per series, so the folder holding a file normally
/// names its series. A file directly in a library root (the watched or the duplicates
/// directory) is the exception: the root's name ("watched_dir", "comics") says nothing about
/// the series, and using it gave every loose file the same series ("watched:dir"), renamed them
/// all to "watched_dir - Chapter NNNN" and flagged all but one as duplicates. For such a file
/// the series is read from its own name instead ("Batman 001.cbz" is "Batman").
/// </para>
/// </summary>
public static partial class SeriesFromPath
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// True when <paramref name="filePath"/> sits directly in one of
    /// <paramref name="libraryRoots"/> rather than in a series folder beneath it. A root is
    /// matched both as configured and with its links resolved, so logical and physical paths
    /// of the same file give the same answer.
    /// </summary>
    public static bool IsDirectlyInLibraryRoot(string filePath, params string?[] libraryRoots)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string? parent;
        try
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        foreach (var root in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                var fullRoot = Path.GetFullPath(root);
                if (IsSameDirectory(parent, fullRoot) ||
                    IsSameDirectory(parent, LibraryPathValidator.ResolveRealPath(fullRoot)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // An unusable root contains nothing.
            }
        }

        return false;
    }

    /// <summary>
    /// The series named by the folder holding <paramref name="filePath"/>, normalized the way
    /// folder names are everywhere else (<see cref="ComicFileProcessor.NormalizeSeriesName"/>).
    /// Null for a file directly in one of <paramref name="libraryRoots"/>, whose folder is not a
    /// series folder, and for a folder whose name normalizes to nothing.
    /// </summary>
    public static string? FromFolder(string filePath, params string?[] libraryRoots)
    {
        if (IsDirectlyInLibraryRoot(filePath, libraryRoots))
        {
            return null;
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrEmpty(folderName))
        {
            return null;
        }

        var series = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        return string.IsNullOrWhiteSpace(series) ? null : series;
    }

    /// <summary>
    /// The series a comic's file name starts with: the text before its chapter or issue
    /// number, without bracketed tags such as "(2016)" or "[Digital]", a trailing volume
    /// number or separators. "Batman 001 (2016).cbz", "Batman #12.cbz" and
    /// "Batman - Chapter 0012.cbz" all give "Batman". Null when the name holds no letters
    /// before its number ("001.cbz", "Chapter 12.cbz"), so callers do not invent a series
    /// that every such file would share.
    /// </summary>
    /// <remarks>
    /// The number is found the way <see cref="ComicFileProcessor.ParseChapterNumber"/> finds
    /// the chapter: an explicit chapter or issue keyword first, otherwise the first number
    /// standing on its own (so "20th Century Boys 001" keeps "20th").
    /// </remarks>
    public static string? FromFileName(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var text = BracketedTagPattern().Replace(stem, " ").Replace('_', ' ');
        var number = ChapterKeywordPattern().Match(text);
        if (!number.Success)
        {
            number = StandaloneNumberPattern().Match(text);
        }

        var series = number.Success ? text[..number.Index] : text;
        series = TrailingVolumePattern().Replace(series, string.Empty);
        series = WhitespacePattern().Replace(series, " ").Trim(' ', '-', '.', ',', '#', '~', ':', '+');

        return series.Any(char.IsLetter) ? series : null;
    }

    private static bool IsSameDirectory(string a, string b) =>
        string.Equals(a.TrimEnd(Separators), b.TrimEnd(Separators), LibraryPathValidator.PathComparison);

    // "(2016)", "[Digital]", "{c2c}"
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketedTagPattern();

    // "Ch 12", "Ch. 12", "Chapter 12", "Issue 3", "Ep 4", "Episode 4", "#12", "No. 5"
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:ch(?:apter)?|issue|ep(?:isode)?|no)\.?\s*#?\s*\d|#\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterKeywordPattern();

    // A number that is not part of a word: "001" in "Batman 001" but not "20" in "20th".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d+(?:[.,]\d+)?(?![\p{L}\p{N}])")]
    private static partial Regex StandaloneNumberPattern();

    // A volume token left at the end once the chapter is cut off: "Saga Vol. 2", "Saga v02", "Saga Volume".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:vol(?:ume)?\.?\s*\d*|v\d+)\s*[-.,]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVolumePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
