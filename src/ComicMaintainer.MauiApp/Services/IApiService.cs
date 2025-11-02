using ComicMaintainer.Core.Models;

namespace ComicMaintainer.MauiApp.Services;

public interface IApiService
{
    Task<bool> TestConnectionAsync(string serverUrl);
    Task<IEnumerable<ComicFile>> GetFilesAsync(int page = 1, int pageSize = 100, string? filter = null, string? search = null);
    Task<bool> ProcessFileAsync(string filePath);
    Task<string> StartBatchProcessAsync(List<string> files);
    Task<JobStatus?> GetJobStatusAsync(string jobId);
}
