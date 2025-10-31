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
    private static readonly Regex ChapterKeywordPattern = 
        new(@"(?i)ch(?:apter)?[-._\s]*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
    
    private static readonly Regex NumberPattern = 
        new(@"(?<![\(\[])[0-9]+(?:\.[0-9]+)?(?![\)\]])", RegexOptions.Compiled);
    
    private static readonly Regex BracketStartPattern = 
        new(@"[\(\[]$", RegexOptions.Compiled);
    
    private static readonly Regex BracketEndPattern = 
        new(@"^[\)\]]", RegexOptions.Compiled);

    /// <summary>
    /// Parse chapter number from filename
    /// Converted from Python's parse_chapter_number function
    /// </summary>
    public static string? ParseChapterNumber(string filename)
    {
        // First try to find chapter keyword
        var match = ChapterKeywordPattern.Match(filename);
        if (match.Success)
        {
            return match.Groups[1].Value;
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
                return m.Value;
            }
        }

        return null;
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
        result = Regex.Replace(result, @"\{[^}]+\}", "");

        // Clean up extra spaces
        result = Regex.Replace(result, @"\s+", " ").Trim();

        // Ensure proper extension
        if (!result.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) && 
            !result.EndsWith(".cbr", StringComparison.OrdinalIgnoreCase))
        {
            result += originalExtension;
        }

        return result;
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
