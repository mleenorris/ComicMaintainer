using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.Models;

public class FolderSummaryDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }

    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }

    [JsonPropertyName("last_modified")]
    public long LastModified { get; set; }

    [JsonPropertyName("unmarked_count")]
    public int UnmarkedCount { get; set; }

    [JsonPropertyName("duplicate_count")]
    public int DuplicateCount { get; set; }
}

public class FolderSummariesResult
{
    public List<FolderSummaryDto> Folders { get; set; } = new();
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalFolders { get; set; }
}

/// <summary>
/// Result for paged file list queries served directly from the database.
/// </summary>
public class PagedFilesResult
{
    public List<FileDto> Files { get; set; } = new();
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int TotalFiles { get; set; }
}
