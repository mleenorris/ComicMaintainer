using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

public class SeriesIssueDto
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("issue")]
    public string? Issue { get; set; }

    [JsonPropertyName("volume")]
    public string? Volume { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modified")]
    public long Modified { get; set; }

    [JsonPropertyName("processed")]
    public bool Processed { get; set; }

    [JsonPropertyName("renamed")]
    public bool Renamed { get; set; }

    [JsonPropertyName("normalized")]
    public bool Normalized { get; set; }

    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; set; }

    [JsonPropertyName("read")]
    public bool Read { get; set; }
}

public class SeriesLibraryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("canonical_title")]
    public string CanonicalTitle { get; set; } = string.Empty;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("metadata_source")]
    public string? MetadataSource { get; set; }

    [JsonPropertyName("issue_count")]
    public int IssueCount { get; set; }

    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }

    [JsonPropertyName("latest_modified")]
    public long LatestModified { get; set; }

    [JsonPropertyName("cover_file_path")]
    public string CoverFilePath { get; set; } = string.Empty;

    [JsonPropertyName("issues")]
    public List<SeriesIssueDto> Issues { get; set; } = new();
}

public class SeriesLibraryResult
{
    public List<SeriesLibraryDto> Series { get; set; } = new();
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalSeries { get; set; }
}
