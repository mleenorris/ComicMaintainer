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

    [JsonPropertyName("series_name")]
    public string? SeriesName { get; set; }

    /// <summary>
    /// True when <see cref="SeriesName"/> is an explicit user selection
    /// (sticky). Derived; surfaced so the UI can show whether the displayed
    /// name is automatic or user-chosen.
    /// </summary>
    [JsonPropertyName("is_user_selected_name")]
    public bool IsUserSelectedName => !string.IsNullOrWhiteSpace(SeriesName);

    /// <summary>
    /// The fully-resolved series name actually displayed in the library and
    /// written into ComicInfo.xml's <c>&lt;Series&gt;</c>. Computed by the
    /// cache service via <see cref="Services.SeriesDisplayTitleResolver"/> so
    /// the website and file metadata always agree.
    /// </summary>
    [JsonPropertyName("resolved_series_name")]
    public string ResolvedSeriesName { get; set; } = string.Empty;

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

    /// <summary>
    /// User's preferred language for the displayed series name (one of
    /// <c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>), or null to fall back to
    /// the global default / canonical title.
    /// </summary>
    [JsonPropertyName("preferred_language")]
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Provider-supplied titles tagged with their language. Used by the
    /// display-title resolver and surfaced to the UI so users can see which
    /// alternatives exist and in which language.
    /// </summary>
    [JsonPropertyName("localized_titles")]
    public List<LocalizedTitle> LocalizedTitles { get; set; } = new();

    /// <summary>
    /// Plain-text synopsis / summary captured from the last successful external
    /// lookup, shown at the top of the series page. Null when unavailable.
    /// </summary>
    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; set; }

    /// <summary>
    /// Monotonic version bumped on every cache mutation that should
    /// invalidate stale per-file metadata stamps. Surfaced so external
    /// callers can verify the version a UI screen was rendered against.
    /// </summary>
    [JsonPropertyName("metadata_version")]
    public int MetadataVersion { get; set; }

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
