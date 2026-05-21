using System.Text.RegularExpressions;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Handles comic file processing operations like parsing chapter numbers,
/// formatting filenames, and checking normalization.
/// Converted from Python's process_file.py
/// </summary>
public class ComicFileProcessor
{
    // Windows-invalid filename characters are added explicitly so generated names remain portable
    // even when the service is running on a platform with a more permissive filesystem.
    private static readonly char[] CrossPlatformInvalidFileNameChars =
        Path.GetInvalidFileNameChars()
            .Concat("<>:\"/\\|?*".ToCharArray())
            .Distinct()
            .ToArray();

    private static readonly Regex ChapterKeywordPattern = 
        new(@"(?i)ch(?:apter)?[-._\s]*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
    
    private static readonly Regex NumberPattern = 
        new(@"(?<![\(\[])[0-9]+(?:\.[0-9]+)?(?![\)\]])", RegexOptions.Compiled);
    
    private static readonly Regex BracketStartPattern = 
        new(@"[\(\[]$", RegexOptions.Compiled);
    
    private static readonly Regex BracketEndPattern = 
        new(@"^[\)\]]", RegexOptions.Compiled);

    private static readonly Regex UnreplacedPlaceholderPattern =
        new(@"\{[^}]+\}", RegexOptions.Compiled);

    private static readonly Regex ExtraWhitespacePattern =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Parse chapter number from filename. Leading zeros are stripped from
    /// the integer part while any decimal portion is preserved
    /// (e.g. "0004" → "4", "0004.5" → "4.5").
    /// Converted from Python's parse_chapter_number function.
    /// </summary>
    public static string? ParseChapterNumber(string filename)
    {
        // First try to find chapter keyword
        var match = ChapterKeywordPattern.Match(filename);
        if (match.Success)
        {
            return StripLeadingZeros(match.Groups[1].Value);
        }

        // Look for numbers not in brackets
        var matches = NumberPattern.Matches(filename);
        foreach (Match m in matches)
        {
            var start = m.Index;
            var end = m.Index + m.Length;
            var before = filename[..start];
            var after = filename[end..];

            if (!BracketStartPattern.IsMatch(before) && !BracketEndPattern.IsMatch(after))
            {
                return StripLeadingZeros(m.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Strip leading zeros from the integer part of a numeric chapter/issue
    /// string while preserving the decimal portion. Returns "0" for an
    /// all-zero integer part and passes non-numeric values through unchanged.
    /// </summary>
    internal static string StripLeadingZeros(string number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return number;
        }

        var dotIndex = number.IndexOf('.');
        var integerPart = dotIndex >= 0 ? number[..dotIndex] : number;
        var fractionalPart = dotIndex >= 0 ? number[dotIndex..] : string.Empty;

        var trimmed = integerPart.TrimStart('0');
        if (trimmed.Length == 0)
        {
            trimmed = "0";
        }

        return trimmed + fractionalPart;
    }

    /// <summary>
    /// Parse a chapter number from a filename only when an explicit
    /// "Ch"/"Chapter" keyword is present. This is a stricter variant of
    /// <see cref="ParseChapterNumber"/> that avoids false-positives on
    /// volume-only filenames where the only numeric token is a volume
    /// number, year, or part of a title.
    /// </summary>
    public static string? ParseChapterKeyword(string filename)
    {
        var match = ChapterKeywordPattern.Match(filename);
        return match.Success ? StripLeadingZeros(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Returns true when two chapter-number strings represent the same
    /// numeric value (e.g. "1", "0001" and "0001.0" are all equivalent).
    /// Falls back to ordinal string comparison for non-numeric values.
    /// </summary>
    public static bool ChapterNumbersEquivalent(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (decimal.TryParse(a, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
        {
            return da == db;
        }

        return false;
    }

    /// <summary>
    /// Format filename based on template and tags
    /// Converted from Python's format_filename function
    /// </summary>
    /// <param name="template">Filename template with placeholders</param>
    /// <param name="tags">Comic metadata tags</param>
    /// <param name="issueNumber">Issue number</param>
    /// <param name="originalExtension">Original file extension (.cbz or .cbr)</param>
    /// <param name="padding">Issue number padding (default: 4)</param>
    public static string FormatFilename(
        string template, 
        ComicInfo tags, 
        string issueNumber, 
        string originalExtension = ".cbz",
        int padding = 4)
    {
        // Parse issue number into integer and decimal parts
        string issueFormatted;
        string issueNoPad;

        try
        {
            var issueStr = issueNumber;
            if (float.TryParse(issueStr, out var issueFloat))
            {
                var integer = (int)issueFloat;
                var formatString = $"D{padding}";
                issueFormatted = integer.ToString(formatString);

                // Check if there's a decimal part
                if (issueStr.Contains('.'))
                {
                    var parts = issueStr.Split('.');
                    var decimalPart = parts[1].TrimEnd('0');
                    if (!string.IsNullOrEmpty(decimalPart))
                    {
                        issueFormatted = $"{integer.ToString(formatString)}.{decimalPart}";
                        issueNoPad = $"{integer}.{decimalPart}";
                    }
                    else
                    {
                        issueNoPad = integer.ToString();
                    }
                }
                else
                {
                    issueNoPad = integer.ToString();
                }
            }
            else
            {
                issueFormatted = issueNumber;
                issueNoPad = issueNumber;
            }
        }
        catch
        {
            issueFormatted = issueNumber;
            issueNoPad = issueNumber;
        }

        // Build replacement dictionary
        var replacements = new Dictionary<string, string>
        {
            ["series"] = tags.Series ?? "",
            ["issue"] = issueFormatted,
            ["issue_no_pad"] = issueNoPad,
            ["title"] = tags.Title ?? "",
            ["volume"] = tags.Volume ?? "",
            ["year"] = tags.Year?.ToString() ?? "",
            ["publisher"] = tags.Publisher ?? ""
        };

        // Replace placeholders
        var result = template;
        foreach (var (key, value) in replacements)
        {
            result = result.Replace($"{{{key}}}", value);
        }

        // Clean up any remaining unreplaced placeholders
        result = UnreplacedPlaceholderPattern.Replace(result, "");

        // Clean up extra spaces
        result = ExtraWhitespacePattern.Replace(result, " ").Trim();

        // Ensure proper extension
        if (!result.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) && 
            !result.EndsWith(".cbr", StringComparison.OrdinalIgnoreCase))
        {
            result += originalExtension;
        }

        return SanitizeFileName(result);
    }

    public static string SanitizeFileName(string fileName)
    {
        var sanitized = fileName;
        foreach (var invalidChar in CrossPlatformInvalidFileNameChars)
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        return sanitized;
    }

    /// <summary>
    /// Check if file is already normalized
    /// Converted from Python's is_file_already_normalized function
    /// </summary>
    public static bool IsFileAlreadyNormalized(
        string filepath,
        string? filenameTemplate,
        bool fixTitle = true,
        bool fixSeries = true,
        bool fixFilename = true,
        string? comicFolder = null,
        int issuePadding = 4)
    {
        try
        {
            using var ca = new ComicArchive(filepath);
            var tags = ca.ReadTags("cr");
            
            if (tags == null)
            {
                return false;
            }

            // Check title normalization if requested
            if (fixTitle)
            {
                string? issueNumber = null;
                
                if (!string.IsNullOrEmpty(tags.Number))
                {
                    issueNumber = tags.Number;
                }

                if (string.IsNullOrEmpty(issueNumber))
                {
                    issueNumber = ParseChapterNumber(Path.GetFileNameWithoutExtension(filepath));
                }

                if (!string.IsNullOrEmpty(issueNumber))
                {
                    var expectedTitle = $"Chapter {issueNumber}";
                    if (tags.Title != expectedTitle)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            // Check series normalization if requested
            if (fixSeries)
            {
                comicFolder ??= Path.GetDirectoryName(filepath);
                if (comicFolder == null)
                {
                    return false;
                }

                var seriesName = Path.GetFileName(comicFolder);
                var seriesNameCompare = NormalizeSeriesName(seriesName, forComparison: true);

                if (!string.IsNullOrEmpty(tags.Series))
                {
                    var tagsSeriesCompare = NormalizeSeriesName(tags.Series, forComparison: true);
                    if (tagsSeriesCompare != seriesNameCompare)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            // Check filename normalization if requested
            if (fixFilename)
            {
                if (string.IsNullOrEmpty(tags.Number))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(filenameTemplate))
                {
                    return false;
                }

                var originalExt = Path.GetExtension(filepath).ToLowerInvariant();
                var expectedFilename = FormatFilename(filenameTemplate, tags, tags.Number, originalExt, issuePadding);
                var currentFilename = Path.GetFileName(filepath);

                if (currentFilename != expectedFilename)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Expected exceptions when file is not accessible or doesn't exist
            System.Diagnostics.Debug.WriteLine($"Cannot check normalization for {filepath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Normalize series name from folder name
    /// Converted from Python's series normalization logic
    /// </summary>
    /// <param name="folderName">The folder name to normalize</param>
    /// <param name="forComparison">If true, also applies comparison-specific transformations</param>
    public static string NormalizeSeriesName(string folderName, bool forComparison = false)
    {
        var seriesName = folderName.Replace('_', ':');
        
        if (forComparison)
        {
            seriesName = seriesName.Replace("'", "\u0027");
            seriesName = Regex.Replace(seriesName, @"\(\*\)|\[\*\]", "");
            seriesName = seriesName.Trim();
        }
        
        return seriesName;
    }
}
