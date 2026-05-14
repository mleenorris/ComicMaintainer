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

    [JsonPropertyName("remote_image_url")]
    public string? RemoteImageUrl { get; set; }

    [JsonPropertyName("local_image_file")]
    public string? LocalImageFile { get; set; }

    [JsonPropertyName("image_content_type")]
    public string? ImageContentType { get; set; }

    [JsonPropertyName("image_downloaded_utc")]
    public DateTime? ImageDownloadedUtc { get; set; }

    [JsonPropertyName("image_status")]
    public string? ImageStatus { get; set; }

    /// <summary>True when an image (downloaded or user-uploaded) is available locally.</summary>
    [JsonPropertyName("has_image")]
    public bool HasImage =>
        !string.IsNullOrEmpty(LocalImageFile)
        && (string.Equals(ImageStatus, "downloaded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ImageStatus, "user", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the cached image was uploaded by the user (sticky on refresh).</summary>
    [JsonPropertyName("is_user_image")]
    public bool IsUserImage => string.Equals(ImageStatus, "user", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of a manual metadata refresh job request.
/// </summary>
public class SeriesMetadataRefreshResult
{
    public Guid JobId { get; set; }
    public int TotalSeries { get; set; }
}
