using System.Text.Json.Serialization;

namespace ComicMaintainer.MauiApp.Models;

/// <summary>One comic of the server's library, as <c>GET /api/files</c> reports it.</summary>
public sealed class ComicFile
{
    /// <summary>
    /// The file's full path on the server, which is its identity: it is sent back unchanged to the endpoints that act
    /// on a file. The server's JSON name is kept for compatibility, despite what it says.
    /// </summary>
    [JsonPropertyName("relative_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long FileSize { get; set; }

    /// <summary>When the file last changed, in seconds since the Unix epoch.</summary>
    [JsonPropertyName("modified")]
    public long ModifiedUnixSeconds { get; set; }

    [JsonPropertyName("processed")]
    public bool IsProcessed { get; set; }

    [JsonPropertyName("duplicate")]
    public bool IsDuplicate { get; set; }

    /// <summary>The folder holding the file on the server, which may run another operating system than this device.</summary>
    [JsonIgnore]
    public string Directory
    {
        get
        {
            var separator = FilePath.LastIndexOfAny(['/', '\\']);
            return separator > 0 ? FilePath[..separator] : string.Empty;
        }
    }

    /// <summary>When the file last changed, in this device's time zone.</summary>
    [JsonIgnore]
    public DateTime LastModified => DateTimeOffset.FromUnixTimeSeconds(ModifiedUnixSeconds).LocalDateTime;
}
