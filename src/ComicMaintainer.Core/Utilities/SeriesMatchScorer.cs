namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Scores how confident we are that an external-metadata candidate matches a
/// user-supplied query. Returns a percentage in the range [0, 100] where 100
/// means an exact normalized match on the canonical title. The score is used
/// in the UI so users can pick the best candidate when the automatic pick
/// was wrong.
/// </summary>
public static class SeriesMatchScorer
{
    /// <summary>
    /// Computes a confidence score for <paramref name="canonicalTitle"/> /
    /// <paramref name="aliases"/> against <paramref name="query"/>. The score
    /// uses normalized equality (case- and punctuation-insensitive) for high
    /// confidence and falls back to a Levenshtein-based similarity ratio for
    /// fuzzy matches.
    /// </summary>
    /// <returns>Score in [0, 100]. 0 when either input is empty.</returns>
    public static double Score(string? query, string? canonicalTitle, IEnumerable<string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0d;
        }

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
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var s = ScorePair(queryNorm, Normalize(alias), canonicalMatchBonus: false);
                if (s > best) best = s;
            }
        }

        return Math.Round(Math.Clamp(best, 0d, 100d), 1);
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
            return canonicalMatchBonus ? 100d : 95d;
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
        // Lowercase + strip non-alphanumerics. Mirrors the normalization used
        // by SeriesMetadataCacheService.NormalizeKey (minus the dash join),
        // which is also what the providers' PickBestMatch routines use.
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
