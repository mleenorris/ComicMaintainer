using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Scores how confident we are that an external-metadata candidate matches a
/// user-supplied query. Returns a percentage in the range [0, 100] where 100
/// means an exact normalized match on the canonical title. The score is used
/// in the UI so users can pick the best candidate when the automatic pick
/// was wrong, and it decides which candidate (if any) an automatic lookup
/// accepts (<see cref="SelectAutomaticMatch"/>).
/// </summary>
public static class SeriesMatchScorer
{
    /// <summary>
    /// Lowest score an automatic lookup accepts as a match. Only an exact
    /// normalized match on the canonical title (100) or on an alias (95)
    /// reaches it: a containment match scores below 90 unless the titles are
    /// equal, and a fuzzy match at most 70. Automatic matches are written
    /// into files and cached for the whole folder, so a near miss such as
    /// "Monster" for "Monsters" must stay unmatched for the user to resolve.
    /// </summary>
    public const double AutomaticMatchThreshold = 90d;

    /// <summary>
    /// Deducted from the title score when the query's volume year and the
    /// candidate's start year are one year apart: a volume's first cover date
    /// and the year a library files it under often differ by one.
    /// </summary>
    private const double AdjacentYearPenalty = 5d;

    /// <summary>Score of an exact normalized match on the canonical title.</summary>
    private const double CanonicalExactScore = 100d;

    /// <summary>Score of an exact normalized match on an alias.</summary>
    private const double AliasExactScore = 95d;

    /// <summary>
    /// Deducted when the query names a volume year but the candidate reports
    /// no start year. An exact title then scores 85: shown as a likely pick,
    /// but below <see cref="AutomaticMatchThreshold"/>, because nothing
    /// confirms it is the right one of several same-named volumes.
    /// </summary>
    private const double UnknownYearPenalty = 15d;

    /// <summary>
    /// Deducted when the query's volume year and the candidate's start year
    /// disagree by more than a year: a different volume of the same name.
    /// </summary>
    private const double ConflictingYearPenalty = 40d;

    /// <summary>
    /// Picks the candidate an automatic lookup for <paramref name="query"/>
    /// accepts: the highest-scoring candidate that reaches
    /// <see cref="AutomaticMatchThreshold"/>. On a tie the earlier candidate
    /// wins, so the provider's own relevance order breaks it. Returns null when
    /// no candidate is close enough; a provider's top search hit is never
    /// accepted just for being first. A query that names a volume year
    /// (<c>Green Lantern (2023)</c>) only accepts a candidate whose start year
    /// confirms it (see <see cref="Score(string?, string?, IEnumerable{string}?, int?)"/>).
    /// </summary>
    public static (ExternalSeriesMetadata Match, double Score)? SelectAutomaticMatch(
        IEnumerable<ExternalSeriesMetadata> candidates,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        (ExternalSeriesMetadata Match, double Score)? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var score = Score(query, candidate.CanonicalTitle, candidate.Aliases, candidate.StartYear);
            if (score >= AutomaticMatchThreshold && (best is null || score > best.Value.Score))
            {
                best = (candidate, score);
            }
        }

        return best;
    }

    /// <summary>
    /// Computes a confidence score for <paramref name="canonicalTitle"/> /
    /// <paramref name="aliases"/> against <paramref name="query"/>. The score
    /// uses normalized equality (case- and punctuation-insensitive) for high
    /// confidence and falls back to a Levenshtein-based similarity ratio for
    /// fuzzy matches.
    /// </summary>
    /// <remarks>
    /// When <paramref name="query"/> ends in a volume year
    /// (<c>Green Lantern (2023)</c>, see <see cref="SeriesTitleYear"/>), the
    /// title without the year is scored too and adjusted by how the year
    /// compares with <paramref name="startYear"/>: the same year keeps the
    /// score, an adjacent year loses <see cref="AdjacentYearPenalty"/>, an
    /// unknown start year <see cref="UnknownYearPenalty"/> and any other year
    /// <see cref="ConflictingYearPenalty"/>. The better of that and the score of
    /// the whole query (for a candidate whose own title carries the year) is
    /// returned. A query without a year ignores <paramref name="startYear"/>.
    /// </remarks>
    /// <returns>Score in [0, 100]. 0 when either input is empty.</returns>
    public static double Score(string? query, string? canonicalTitle, IEnumerable<string>? aliases, int? startYear = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0d;
        }

        var aliasList = aliases?.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList();
        var whole = ScoreTitles(query, canonicalTitle, aliasList);
        var best = whole;

        var (title, year) = SeriesTitleYear.Parse(query);
        if (year is { } queryYear)
        {
            // The whole query only counts when a title carries the year itself
            // ("Batman (1940)" as an alias); otherwise "green lantern 2023"
            // containing "green lantern" would hide a conflicting start year.
            var adjusted = ScoreTitles(title, canonicalTitle, aliasList) - YearPenalty(queryYear, startYear);
            best = whole >= AliasExactScore ? Math.Max(whole, adjusted) : adjusted;
        }

        return Math.Round(Math.Clamp(best, 0d, 100d), 1);
    }

    private static double YearPenalty(int queryYear, int? startYear) => startYear switch
    {
        null => UnknownYearPenalty,
        { } year when year == queryYear => 0d,
        { } year when Math.Abs(year - queryYear) == 1 => AdjacentYearPenalty,
        _ => ConflictingYearPenalty,
    };

    private static double ScoreTitles(string query, string? canonicalTitle, IReadOnlyList<string>? aliases)
    {
        var queryNorm = Normalize(query);
        if (queryNorm.Length == 0)
        {
            return 0d;
        }

        var best = 0d;
        if (!string.IsNullOrWhiteSpace(canonicalTitle))
        {
            best = Math.Max(best, ScorePair(queryNorm, Normalize(canonicalTitle), canonicalMatchBonus: true));
        }

        if (aliases != null)
        {
            foreach (var alias in aliases)
            {
                best = Math.Max(best, ScorePair(queryNorm, Normalize(alias), canonicalMatchBonus: false));
            }
        }

        return best;
    }

    private static double ScorePair(string queryNorm, string candidateNorm, bool canonicalMatchBonus)
    {
        if (candidateNorm.Length == 0)
        {
            return 0d;
        }

        if (queryNorm == candidateNorm)
        {
            // Exact normalized match. Canonical exact matches score the full
            // 100, alias exact matches are slightly demoted so that a
            // canonical exact match is always preferred when both exist.
            return canonicalMatchBonus ? CanonicalExactScore : AliasExactScore;
        }

        // Substring containment is a strong signal that the candidate covers
        // the query (or vice versa).
        if (candidateNorm.Contains(queryNorm) || queryNorm.Contains(candidateNorm))
        {
            var ratio = (double)Math.Min(queryNorm.Length, candidateNorm.Length)
                      / Math.Max(queryNorm.Length, candidateNorm.Length);
            // 70-90 band for substring matches, scaled by the shorter-vs-longer ratio.
            return 70d + (ratio * 20d);
        }

        // Fall back to Levenshtein similarity for fuzzy matches.
        var distance = Levenshtein(queryNorm, candidateNorm);
        var maxLen = Math.Max(queryNorm.Length, candidateNorm.Length);
        var similarity = 1d - ((double)distance / maxLen);
        // Compress the fuzzy band into [0, 70] so it never beats a substring match.
        return Math.Max(0d, similarity) * 70d;
    }

    private static string Normalize(string value)
    {
        // Lowercase + strip non-alphanumerics: the series key of SeriesKey
        // without the dashes, so word breaks do not count as differences.
        var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static int Levenshtein(string a, string b)
    {
        // Standard two-row Levenshtein, O(n*m) time, O(min(n,m)) extra space.
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Ensure b is the shorter of the two so the rolling row stays small.
        if (b.Length > a.Length)
        {
            (a, b) = (b, a);
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
