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

            // Try to parse as numbers
            bool xIsNumber = int.TryParse(xPart, out int xNum);
            bool yIsNumber = int.TryParse(yPart, out int yNum);

            if (xIsNumber && yIsNumber)
            {
                // Both are numbers, compare numerically
                int numCompare = xNum.CompareTo(yNum);
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
}
