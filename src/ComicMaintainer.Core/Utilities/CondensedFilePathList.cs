using System.Text.Json;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// The JSON member list of a condensed email delivery
/// (<c>ComicEmailDeliveryEntity.CondensedFilePaths</c>): every issue in the book, in reading
/// order.
/// </summary>
internal static class CondensedFilePathList
{
    public static string Serialize(IReadOnlyList<string> paths) => JsonSerializer.Serialize(paths);

    /// <summary>
    /// The member paths, or an empty list for a missing or unreadable value: a delivery whose
    /// member list cannot be read is treated as a single-issue delivery.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// <paramref name="text"/> as it appears inside a serialized list, so a list holding a path
    /// that contains <paramref name="text"/> can be found with a substring search. The
    /// serializer escapes more than a plain JSON string needs (an apostrophe, a non-ASCII
    /// letter), so this cannot simply be the text itself.
    /// </summary>
    public static string EncodeFragment(string text)
    {
        var quoted = JsonSerializer.Serialize(text);
        return quoted[1..^1];
    }
}
