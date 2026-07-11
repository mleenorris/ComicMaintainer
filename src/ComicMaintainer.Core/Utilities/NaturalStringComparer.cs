using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Comparer that sorts strings using natural/numeric sorting.
/// For example: "page1.jpg", "page2.jpg", "page10.jpg" instead of "page1.jpg", "page10.jpg", "page2.jpg"
/// </summary>
public class NaturalStringComparer : IComparer<string?>
{
    private static readonly Regex NumberRegex = new Regex(@"(\d+)", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        var xParts = NumberRegex.Split(x);
        var yParts = NumberRegex.Split(y);

        for (int i = 0; i < Math.Min(xParts.Length, yParts.Length); i++)
        {
            var xPart = xParts[i];
            var yPart = yParts[i];

            // A "numeric" segment is a non-empty run of digits produced by the
            // split above. Compare such runs by numeric value without parsing
            // into a fixed-width integer so arbitrarily long digit runs (e.g.
            // timestamped page names or very large identifiers that overflow
            // Int32/Int64) still sort by value instead of silently falling
            // back to lexicographic order.
            bool xIsNumber = IsNumericSegment(xPart);
            bool yIsNumber = IsNumericSegment(yPart);

            if (xIsNumber && yIsNumber)
            {
                int numCompare = CompareNumericSegments(xPart, yPart);
                if (numCompare != 0) return numCompare;
            }
            else
            {
                // At least one is not a number, compare as strings (case-insensitive)
                int strCompare = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                if (strCompare != 0) return strCompare;
            }
        }

        // If all parts are equal, compare lengths
        return xParts.Length.CompareTo(yParts.Length);
    }

    private static bool IsNumericSegment(string part)
    {
        if (string.IsNullOrEmpty(part)) return false;
        foreach (var c in part)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// Compares two digit-only strings by numeric value with no size limit.
    /// Leading zeros are ignored so "007" and "7" are equal, matching the
    /// previous integer-based behaviour while remaining correct for values
    /// that exceed <see cref="int.MaxValue"/> / <see cref="long.MaxValue"/>.
    /// </summary>
    private static int CompareNumericSegments(string x, string y)
    {
        var xTrimmed = x.TrimStart('0');
        var yTrimmed = y.TrimStart('0');

        // Fewer significant digits => smaller magnitude.
        if (xTrimmed.Length != yTrimmed.Length)
        {
            return xTrimmed.Length < yTrimmed.Length ? -1 : 1;
        }

        // Same number of significant digits: lexicographic order matches
        // numeric order for equal-length digit strings.
        return string.CompareOrdinal(xTrimmed, yTrimmed);
    }
}
