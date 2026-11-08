using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Handles comic file processing operations like formatting filenames
/// and checking normalization. Issue numbers are read from file names by
/// <see cref="IssueNumberParser"/>.
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

    private static readonly Regex UnreplacedPlaceholderPattern =
        new(@"\{[^}]+\}", RegexOptions.Compiled);

    private static readonly Regex ExtraWhitespacePattern =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Limits issue-number padding to 0..<see cref="AppSettings.MaxIssueNumberPadding"/>. Configured values
    /// are not range-checked, and the <c>"D{padding}"</c> format string turns a negative value into literal
    /// text and throws for 1,000,000,000 and above.
    /// </summary>
    public static int ClampIssueNumberPadding(int padding) =>
        Math.Clamp(padding, 0, AppSettings.MaxIssueNumberPadding);

    /// <summary>
    /// Format filename based on template and tags
    /// Converted from Python's format_filename function
    /// </summary>
    /// <param name="template">Filename template with placeholders</param>
    /// <param name="tags">Comic metadata tags</param>
    /// <param name="issueNumber">Issue number</param>
    /// <param name="originalExtension">Original file extension (.cbz or .cbr)</param>
    /// <param name="padding">Issue number padding (default: 4), clamped by <see cref="ClampIssueNumberPadding"/></param>
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
            // ComicInfo numbers always use "." as the decimal separator, so parse and format
            // them culture-invariantly; under e.g. de-DE "5.5" would otherwise be read as 55.
            if (float.TryParse(issueStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var issueFloat))
            {
                var integer = (int)issueFloat;
                var formatString = $"D{ClampIssueNumberPadding(padding)}";
                issueFormatted = integer.ToString(formatString, System.Globalization.CultureInfo.InvariantCulture);
                var integerNoPad = integer.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Check if there's a decimal part
                if (issueStr.Contains('.'))
                {
                    var parts = issueStr.Split('.');
                    var decimalPart = parts[1].TrimEnd('0');
                    if (!string.IsNullOrEmpty(decimalPart))
                    {
                        issueFormatted = $"{issueFormatted}.{decimalPart}";
                        issueNoPad = $"{integerNoPad}.{decimalPart}";
                    }
                    else
                    {
                        issueNoPad = integerNoPad;
                    }
                }
                else
                {
                    issueNoPad = integerNoPad;
                }
            }
            else
            {
                issueFormatted = issueNumber;
                issueNoPad = issueNumber;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
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
                    issueNumber = IssueNumberParser.Parse(Path.GetFileNameWithoutExtension(filepath));
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
