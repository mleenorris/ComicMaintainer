using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads a kind of issue (see <see cref="IssueFormat"/>) from a series or file name, the way
/// publishers and taggers name it: the words before the title (<c>Giant-Size X-Men</c>), after it
/// (<c>X-Men: Red One-Shot</c>), or in brackets in a file name (<c>Batman (One-Shot).cbz</c>). The
/// words elsewhere in a name do not count: <c>Giant Days</c> and <c>The Iron Giant</c> are series of
/// their own. A title names a kind too (see <see cref="IssueKindWord"/>).
/// </summary>
public sealed class IssueKindName
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>"Giant-Size", "Giant Size" or "Giant-Sized" (not "Giant" alone), before or after the title.</summary>
    public static IssueKindName GiantSize { get; } = new(@"giant[\s_-]*sized?", leading: true);

    /// <summary>
    /// "One-Shot", "One Shot" or "Oneshot", after the title or in brackets: publishers put it
    /// last (<c>X-Men: Red One-Shot</c>), and a name starting with it is a series of its own
    /// (<c>One Shot Kill</c>).
    /// </summary>
    public static IssueKindName OneShot { get; } = new(@"one[\s_-]*shot", leading: false);

    // The words starting a name: "Giant-Size X-Men"; null when the kind is not named so.
    private readonly Regex? _leading;

    // The words ending a name: "X-Men Giant-Size", "X-Men: Red One-Shot".
    private readonly Regex _trailing;

    // The words in brackets: "Batman (One-Shot)".
    private readonly Regex _bracketed;

    private IssueKindName(string words, bool leading)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        _leading = leading ? new Regex($@"^\s*(?:{words})\s+(?=\S)", options, MatchTimeout) : null;
        _trailing = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{words})\s*$", options, MatchTimeout);
        _bracketed = new Regex($@"[\(\[]\s*(?:{words})\s*[\)\]]", options, MatchTimeout);
    }

    /// <summary>
    /// True when <paramref name="series"/>, or <paramref name="fileName"/> (its series part, or
    /// the words in brackets), names this kind.
    /// </summary>
    public bool Names(string? series, string? fileName)
        => NamesSeries(series)
           || (!string.IsNullOrWhiteSpace(fileName)
               && (NamesSeries(SeriesFromPath.FromFileName(fileName)) || _bracketed.IsMatch(fileName)));

    /// <summary>
    /// True when <paramref name="series"/> ends with the words before any trailing volume, or,
    /// for a kind named first, starts with them (<c>Giant-Size X-Men (1975)</c>, <c>X-Men: Red One-Shot</c>).
    /// </summary>
    public bool NamesSeries(string? series)
    {
        if (string.IsNullOrWhiteSpace(series))
        {
            return false;
        }

        var title = ComicVolume.WithoutVolume(series.Trim());
        return _leading?.IsMatch(title) == true || _trailing.IsMatch(title);
    }

    /// <summary>
    /// <paramref name="title"/> without the words (<c>Giant-Size X-Men</c> gives <c>X-Men</c>,
    /// <c>X-Men: Red One-Shot</c> gives <c>X-Men: Red</c>), so the filename format's
    /// <c>{series}</c> and <c>{format}</c> do not name the kind twice. Unchanged when it holds
    /// nothing else.
    /// </summary>
    public string Without(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var withoutLeading = _leading?.Replace(title, string.Empty) ?? title;
        var trimmed = _trailing.Replace(withoutLeading, string.Empty).Trim(' ', '-', ':', ',');
        return trimmed.Any(char.IsLetterOrDigit) ? trimmed : title;
    }
}
