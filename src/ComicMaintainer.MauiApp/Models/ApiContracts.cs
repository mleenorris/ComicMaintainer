using System.Text.Json.Serialization;

namespace ComicMaintainer.MauiApp.Models;

/// <summary>One page of the library: the response of <c>GET /api/files</c>.</summary>
public sealed class FilesPage
{
    [JsonPropertyName("files")]
    public List<ComicFile> Files { get; set; } = [];

    /// <summary>The page returned, which the server clamps to the pages that exist.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("unmarked_count")]
    public int UnmarkedCount { get; set; }
}

/// <summary>A background job's progress: the response of <c>GET /api/jobs/{id}</c>.</summary>
public sealed class JobStatusInfo
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    /// <summary>One of queued, running, completed, failed, cancelled or interrupted.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("total_items")]
    public int TotalItems { get; set; }

    [JsonPropertyName("processed_items")]
    public int ProcessedItems { get; set; }

    [JsonPropertyName("failed_items")]
    public int FailedItems { get; set; }

    [JsonPropertyName("current_file")]
    public string? CurrentFile { get; set; }
}

/// <summary>The signed-in account: the response of <c>GET /api/auth/user</c>.</summary>
public sealed class AccountInfo
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("canModifyLibrary")]
    public bool CanModifyLibrary { get; set; }
}

/// <summary>The outcome of testing a server address. <see cref="Message"/> is written for the user.</summary>
public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>The body of <c>POST /api/auth/login</c>.</summary>
internal sealed record LoginRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);

/// <summary>The response of <c>POST /api/auth/login</c>.</summary>
internal sealed class LoginResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>The body of the jobs that act on selected files, such as <c>POST /api/jobs/process-selected</c>.</summary>
internal sealed record SelectedFilesRequest([property: JsonPropertyName("files")] IReadOnlyCollection<string> Files);

/// <summary>The response of the endpoints that start a job.</summary>
internal sealed class JobStartedResponse
{
    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }
}

/// <summary>The response of <c>GET /api/version</c>, which needs no account.</summary>
internal sealed class VersionResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}

/// <summary>The response of <c>GET /api/auth/setup-required</c>, which needs no account.</summary>
internal sealed class SetupRequiredResponse
{
    [JsonPropertyName("setupRequired")]
    public bool SetupRequired { get; set; }
}

/// <summary>A credential as the device stores it.</summary>
internal sealed class StoredCredentialDocument
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("server")]
    public string? Server { get; set; }
}
