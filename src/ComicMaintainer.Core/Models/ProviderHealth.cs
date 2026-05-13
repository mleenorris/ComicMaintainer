using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Snapshot of an external metadata provider's configuration + recent runtime
/// status. Surfaced via <c>GET /api/metadata/providers</c> so the UI can show a
/// per-provider health indicator.
/// </summary>
public class ProviderHealth
{
    /// <summary>Provider display name, e.g. "ComicVine", "MangaDex".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>True when the provider is enabled in app settings.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>True when all required configuration (API keys / URLs) is set.</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    /// <summary>
    /// True when the most recent reachability probe (or actual lookup) succeeded
    /// against the provider's HTTP endpoint. Null when never probed.
    /// </summary>
    [JsonPropertyName("reachable")]
    public bool? Reachable { get; set; }

    /// <summary>Short human-readable status string for display in the UI.</summary>
    [JsonPropertyName("status_message")]
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Last error encountered (most recent failure), or null.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    /// <summary>UTC timestamp of the most recent successful lookup/search.</summary>
    [JsonPropertyName("last_success_utc")]
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>UTC timestamp of the most recent failed lookup/search.</summary>
    [JsonPropertyName("last_failure_utc")]
    public DateTime? LastFailureUtc { get; set; }

    /// <summary>Total successful calls observed since process start.</summary>
    [JsonPropertyName("success_count")]
    public long SuccessCount { get; set; }

    /// <summary>Total failed calls observed since process start.</summary>
    [JsonPropertyName("failure_count")]
    public long FailureCount { get; set; }
}
