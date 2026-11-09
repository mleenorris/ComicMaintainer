using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// The genre and publication-status filters of a series query. Either may be left out, and
/// <see cref="NoData"/> selects the series a provider has not reported that value for.
/// </summary>
public sealed record SeriesFacetFilter(string? Genre = null, string? Status = null)
{
    /// <summary>Filter value that selects series with no value for that facet.</summary>
    public const string NoData = "none";

    /// <summary>The filter for these query values, or null when neither is set.</summary>
    public static SeriesFacetFilter? Create(string? genre, string? status) =>
        string.IsNullOrWhiteSpace(genre) && string.IsNullOrWhiteSpace(status)
            ? null
            : new SeriesFacetFilter(
                string.IsNullOrWhiteSpace(genre) ? null : genre.Trim(),
                string.IsNullOrWhiteSpace(status) ? null : status.Trim());

    /// <summary>True when a series with these values passes every filter that is set.</summary>
    public bool Matches(IReadOnlyCollection<string> genres, string? status) =>
        MatchesGenre(genres) && MatchesStatus(status);

    private bool MatchesGenre(IReadOnlyCollection<string> genres)
    {
        if (Genre is null)
        {
            return true;
        }

        return string.Equals(Genre, NoData, StringComparison.OrdinalIgnoreCase)
            ? genres.Count == 0
            : genres.Contains(Genre, StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesStatus(string? status)
    {
        if (Status is null)
        {
            return true;
        }

        return string.Equals(Status, NoData, StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrEmpty(status)
            : string.Equals(Status, status, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One value of a facet and how many series have it.</summary>
public sealed class SeriesFacetCount
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>The genres and statuses found across the library, for the library's filter menus.</summary>
public sealed class SeriesFacetsResult
{
    /// <summary>Genres in alphabetical order.</summary>
    [JsonPropertyName("genres")]
    public List<SeriesFacetCount> Genres { get; set; } = new();

    /// <summary>Statuses in the order of <see cref="SeriesPublicationStatus.All"/>.</summary>
    [JsonPropertyName("statuses")]
    public List<SeriesFacetCount> Statuses { get; set; } = new();

    /// <summary>Series no provider has reported a genre for yet.</summary>
    [JsonPropertyName("series_without_genres")]
    public int SeriesWithoutGenres { get; set; }

    /// <summary>Series no provider has reported a status for yet.</summary>
    [JsonPropertyName("series_without_status")]
    public int SeriesWithoutStatus { get; set; }
}
