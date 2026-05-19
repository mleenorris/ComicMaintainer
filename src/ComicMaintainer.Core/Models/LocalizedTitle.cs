using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// A single series title or alias tagged with its language. The language code is
/// expected to be an ISO 639-1 (or BCP-47) tag such as <c>en</c>, <c>ja</c>,
/// <c>ja-Latn</c>, <c>ko</c>, <c>ko-Latn</c>, or <c>zh</c>. A <c>null</c>
/// language means the title's language is unknown (e.g. an AniList "synonym"
/// or a ComicVine alias not explicitly tagged).
/// </summary>
public class LocalizedTitle
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    public LocalizedTitle() { }

    public LocalizedTitle(string title, string? language = null)
    {
        Title = title;
        Language = language;
    }
}
