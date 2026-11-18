using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads a kind of issue (see <see cref="IssueFormat"/>) from a comic's <c>&lt;Title&gt;</c>, as
/// <see cref="AnnualIssue.FromTitle"/> does for Annuals. Taggers often leave the original file
/// name, or a short label, in the title (<c>Giant 2025</c>, <c>#Giant 2025 2</c>,
/// <c>Special 1</c>, <c>.Special – One Down</c>). Once the file has been renamed, the title is
/// the only place left that marks the issue as one. The word has to name the issue itself, so
/// a story title that merely contains it ("The Giant", "A Special Day") never counts.
/// </summary>
public sealed class IssueKindWord
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>"Giant", "Giant-Size", "Giant Size" or "Giant-Sized".</summary>
    public static IssueKindWord GiantSize { get; } = new(@"giant(?:[\s_-]*sized?)?");

    /// <summary>"Special".</summary>
    public static IssueKindWord Special { get; } = new("special");

    /// <summary>"One-Shot", "One Shot" or "Oneshot".</summary>
    public static IssueKindWord OneShot { get; } = new(@"one[\s_-]*shot");

    // The word, then the issue number: "Giant 2025", "#Giant 2025 2", "Special_01".
    private readonly Regex _beforeNumber;

    // The word starting a title: "Giant-Size X-Men 1".
    private readonly Regex _leading;

    // The word as the whole label, or before a separator: "Special", "Special – One Down".
    private readonly Regex _label;

    private IssueKindWord(string word)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        _beforeNumber = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{word})[\s_:.#-]*(?<number>\d+(?:\.\d+)?)(?![\p{{N}}])", options, MatchTimeout);
        _leading = new Regex($@"^[\s._#-]*(?:{word})(?![\p{{L}}\p{{N}}])", options, MatchTimeout);
        _label = new Regex($@"^(?:{word})(?![\p{{L}}\p{{N}}])\s*(?:[-–—:]|$)", options, MatchTimeout);
    }

    /// <summary>
    /// True when <paramref name="title"/> names the issue as this kind:
    /// <list type="bullet">
    /// <item>the word right before the issue number (<c>Giant 2025</c> or <c>#Giant 2025 2</c>
    /// for issue 2025, <c>X-Men Special 001</c> for issue 1);</item>
    /// <item>the word starting a title that is otherwise the issue's old file name
    /// (<c>Giant-Size X-Men 1</c> for X-Men #1, see <see cref="FileNameTitle.IsLeftoverFileName"/>);</item>
    /// <item>for an issue with no number, the word as the title's label once the series name is
    /// taken off: alone, or before a separator (<c>All New X-Men .Special – One Down</c>,
    /// <c>Special</c>). "Special Delivery" is a story title, not a label.</item>
    /// </list>
    /// </summary>
    public bool InTitle(string? title, string? series, string? issue)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(issue))
        {
            return _label.IsMatch(FileNameTitle.AfterSeries(title, series));
        }

        if (!TryNumber(issue, out var number))
        {
            return false;
        }

        foreach (Match match in _beforeNumber.Matches(title))
        {
            if (TryNumber(match.Groups["number"].Value, out var named) && named == number)
            {
                return true;
            }
        }

        var leading = _leading.Match(title);
        return leading.Success && FileNameTitle.IsLeftoverFileName(title[leading.Length..], series, issue);
    }

    private static bool TryNumber(string text, out decimal number)
        => decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
}
