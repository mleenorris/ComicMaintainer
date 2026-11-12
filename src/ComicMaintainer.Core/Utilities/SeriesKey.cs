using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The one definition of a series key: the case- and punctuation-insensitive
/// form of a series title that identifies a series across the application
/// (series metadata cache records, library grouping, folder combine, and
/// "which files belong to this series").
/// </summary>
/// <remarks>
/// The key is the lowercased title with every run of characters that are not
/// a Unicode letter (<c>\p{L}</c>) or number (<c>\p{N}</c>) replaced by a
/// single dash, and leading/trailing dashes removed: "One_Piece",
/// "one piece" and "ONE PIECE!" all become <c>one-piece</c>. Non-ASCII
/// titles (CJK, accented Latin, Cyrillic) keep their letters, so they produce
/// distinguishable keys instead of collapsing to a bare digit; pure-ASCII
/// titles get the same key as the earlier <c>[^a-z0-9]+</c> sanitizer.
/// </remarks>
public static partial class SeriesKey
{
    /// <summary>Key of a title that has no letters or digits at all.</summary>
    public const string Unknown = "unknown-series";

    /// <summary>
    /// Returns the series key of <paramref name="title"/>, or
    /// <see cref="Unknown"/> when it has no letters or digits.
    /// </summary>
    public static string Normalize(string? title)
        => TryNormalize(title) ?? Unknown;

    /// <summary>
    /// Returns the series key of <paramref name="title"/>, or null when it has
    /// no letters or digits (so callers can skip the title instead of
    /// grouping it under <see cref="Unknown"/>).
    /// </summary>
    public static string? TryNormalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = Sanitizer().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>True when both titles have the same (non-empty) series key.</summary>
    public static bool AreEquivalent(string? left, string? right)
    {
        var leftKey = TryNormalize(left);
        return leftKey is not null && string.Equals(leftKey, TryNormalize(right), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Sanitizer();
}
