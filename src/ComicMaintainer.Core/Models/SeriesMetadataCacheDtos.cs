using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Snapshot of cached external metadata + user aliases for one series key.
/// </summary>
public class SeriesMetadataCacheRecord
{
    [JsonPropertyName("series_id")]
    public string NormalizedKey { get; set; } = string.Empty;

    [JsonPropertyName("canonical_title")]
    public string CanonicalTitle { get; set; } = string.Empty;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("user_aliases")]
    public List<string> UserAliases { get; set; } = new();

    [JsonPropertyName("is_user_canonical")]
    public bool IsUserCanonical { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("last_lookup_utc")]
    public DateTime? LastLookupUtc { get; set; }

    [JsonPropertyName("lookup_status")]
    public string? LookupStatus { get; set; }
}

/// <summary>
/// Result of a manual metadata refresh job request.
/// </summary>
public class SeriesMetadataRefreshResult
{
    public Guid JobId { get; set; }
    public int TotalSeries { get; set; }
}
