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

    /// <summary>
    /// User's preferred language for the displayed series name (one of
    /// <c>en</c>, <c>ja</c>, <c>ko</c>, <c>zh</c>), or null to fall back to
    /// the global default / canonical title.
    /// </summary>
    [JsonPropertyName("preferred_language")]
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// User-pinned localized title that wins over the language-preference
    /// rule but loses to <see cref="IsUserCanonical"/>. When non-null/empty
    /// it is returned verbatim as the resolved series name. Typical use:
    /// the user wants a specific romaji or English alternative even though
    /// their general preferred language would otherwise select a different
    /// entry from <see cref="LocalizedTitles"/>. The value should be one of
    /// the strings present in <see cref="LocalizedTitles"/> or
    /// <see cref="CanonicalTitle"/>; the cache service validates this on
    /// write.
    /// </summary>
    [JsonPropertyName("pinned_localized_title")]
    public string? PinnedLocalizedTitle { get; set; }

    /// <summary>
    /// Provider-supplied titles tagged with their language. Used by the
    /// display-title resolver and surfaced to the UI so users can see which
    /// alternatives exist and in which language.
    /// </summary>
    [JsonPropertyName("localized_titles")]
    public List<LocalizedTitle> LocalizedTitles { get; set; } = new();

    /// <summary>
    /// Monotonic version bumped on every cache mutation that should
    /// invalidate stale per-file metadata stamps. Surfaced so external
    /// callers can verify the version a UI screen was rendered against.
    /// </summary>
    [JsonPropertyName("metadata_version")]
    public int MetadataVersion { get; set; }

    /// <summary>
    /// PR 1 of 3 — resolved series name. The single value the library card
    /// and each file's ComicInfo.xml &lt;Series&gt; element must agree on.
    /// Null only for legacy rows backfilled lazily; consumers should fall
    /// back via <see cref="Services.SeriesDisplayTitleResolver"/>.
    /// </summary>
    [JsonPropertyName("series_name")]
    public string? SeriesName { get; set; }

    /// <summary>How <see cref="SeriesName"/> was determined.</summary>
    [JsonPropertyName("series_name_source")]
    public SeriesNameSource SeriesNameSource { get; set; } = SeriesNameSource.FolderName;

    /// <summary>
    /// Snapshot of the BCP-47 language code that produced
    /// <see cref="SeriesName"/> when the source is
    /// <see cref="SeriesNameSource.LanguageDefault"/>. Diagnostic only.
    /// </summary>
    [JsonPropertyName("series_name_language")]
    public string? SeriesNameLanguage { get; set; }

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
