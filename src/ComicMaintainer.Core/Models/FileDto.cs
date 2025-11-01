using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

/// <summary>
/// Data Transfer Object for file information sent to the frontend.
/// Property names match the JavaScript expectations (snake_case).
/// </summary>
public class FileDto
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modified")]
    public long Modified { get; set; }

    [JsonPropertyName("processed")]
    public bool Processed { get; set; }

    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; set; }

    /// <summary>
    /// Create a FileDto from a ComicFile
    /// </summary>
    public static FileDto FromComicFile(ComicFile file)
    {
        return new FileDto
        {
            RelativePath = file.FilePath,
            Name = file.FileName,
            Size = file.FileSize,
            Modified = file.LastModified.Kind == DateTimeKind.Utc 
                ? new DateTimeOffset(file.LastModified, TimeSpan.Zero).ToUnixTimeSeconds()
                : new DateTimeOffset(file.LastModified).ToUnixTimeSeconds(),
            Processed = file.IsProcessed,
            Duplicate = file.IsDuplicate
        };
    }
}
