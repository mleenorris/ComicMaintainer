using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <summary>
/// The server's library API. Every method except <see cref="TestConnectionAsync"/> needs a signed-in session and
/// reports failures with <see cref="ApiException"/>; a credential the server refuses also ends the session.
/// </summary>
public interface IApiService
{
    /// <summary>Checks that <paramref name="serverUrl"/> is a ComicMaintainer server. Needs no account and does not throw for a bad server.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(string serverUrl, CancellationToken cancellationToken = default);

    /// <summary>One page of the library. <paramref name="filter"/> is an API filter such as <c>unmarked</c>, <c>marked</c> or <c>duplicates</c>.</summary>
    Task<FilesPage> GetFilesAsync(int page = 1, int perPage = 100, string? filter = null, string? search = null, CancellationToken cancellationToken = default);

    /// <summary>Processes one file and waits for the result.</summary>
    Task ProcessFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Starts a background job that processes <paramref name="files"/> and returns its id.</summary>
    Task<string> StartBatchProcessAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default);

    /// <summary>Deletes one file from the server.</summary>
    Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Starts a background job that deletes <paramref name="files"/> from the server and returns its id.</summary>
    Task<string> StartBatchDeleteAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default);

    /// <summary>A job's progress, or null when the server does not know the job.</summary>
    Task<JobStatusInfo?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);
}
