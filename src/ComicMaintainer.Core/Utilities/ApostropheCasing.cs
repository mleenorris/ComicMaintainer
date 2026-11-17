using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Repairs the capital a naive title-caser puts after an apostrophe. Such tools treat
/// <c>'</c> and <c>’</c> as the end of a word, so "Trash of the Count’s Family" arrives as
/// "Trash Of The Count’S Family" and "I’ll" as "I’Ll". ComicMaintainer never title-cases a
/// name itself, but it takes series names from folder names and existing tags written by
/// other tools, and would otherwise carry the wrong capital into tags, file names and folders.
/// </summary>
/// <remarks>
/// Only an English contraction ending is lowered: <c>’S</c>, <c>’T</c>, <c>’D</c>, <c>’M</c>,
/// <c>’Ll</c>, <c>’Re</c> and <c>’Ve</c>, at the end of a word that is not written in capitals.
/// Names whose capital after the apostrophe is meant ("O'Brien", "D'Artagnan",
/// "Rock'N'Roll") and names in capitals ("DON'T", "I'LL") are left as they are.
/// </remarks>
public static partial class ApostropheCasing
{
    private static readonly HashSet<string> ContractionEndings = new(StringComparer.Ordinal)
    {
        "S", "T", "D", "M", "Ll", "Re", "Ve"
    };

    /// <summary>
    /// <paramref name="name"/> with each title-cased contraction ending lowered
    /// ("Count’S" becomes "Count’s"); null and names without one are returned unchanged.
    /// </summary>
    public static string? Repair(string? name)
    {
        if (string.IsNullOrEmpty(name) || (name.IndexOf('\'') < 0 && name.IndexOf('’') < 0))
        {
            return name;
        }

        return ContractionPattern().Replace(name, match =>
        {
            var word = match.Groups["word"].Value;
            var ending = match.Groups["ending"].Value;

            // A word in capitals ("DON'T") was written that way on purpose.
            var writtenInCapitals = word.Length > 1 && word.All(letter => !char.IsLower(letter));
            return writtenInCapitals || !ContractionEndings.Contains(ending)
                ? match.Value
                : word + match.Groups["apostrophe"].Value + ending.ToLowerInvariant();
        });
    }

    [GeneratedRegex(@"(?<word>\p{L}+)(?<apostrophe>['’])(?<ending>\p{Lu}\p{Ll}?)(?!\p{L})")]
    private static partial Regex ContractionPattern();
}
