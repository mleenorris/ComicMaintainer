using System.Net.Http.Json;
using System.Text.Json;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.MauiApp.Services;

public class ApiService : IApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settingsService;
    private readonly JsonSerializerOptions _jsonOptions;

    public ApiService(IHttpClientFactory httpClientFactory, ISettingsService settingsService)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        var serverUrl = _settingsService.ServerUrl;
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            client.BaseAddress = new Uri(serverUrl);
        }
        return client;
    }

    public async Task<bool> TestConnectionAsync(string serverUrl)
    {
        try
        {
            using var testClient = _httpClientFactory.CreateClient();
            testClient.BaseAddress = new Uri(serverUrl);
            testClient.Timeout = TimeSpan.FromSeconds(5);

            var response = await testClient.GetAsync("/api/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IEnumerable<ComicFile>> GetFilesAsync(int page = 1, int pageSize = 100, string? filter = null, string? search = null)
    {
        using var client = CreateClient();
        
        var queryParams = new List<string>
        {
            $"page={page}",
            $"page_size={pageSize}"
        };

        if (!string.IsNullOrWhiteSpace(filter))
            queryParams.Add($"filter={filter}");

        if (!string.IsNullOrWhiteSpace(search))
            queryParams.Add($"search={Uri.EscapeDataString(search)}");

        var query = string.Join("&", queryParams);
        var response = await client.GetAsync($"/api/files?{query}");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<FilesResponse>(_jsonOptions);
        return result?.Files ?? Enumerable.Empty<ComicFile>();
    }

    public async Task<bool> ProcessFileAsync(string filePath)
    {
        using var client = CreateClient();
        
        var content = JsonContent.Create(new { file_path = filePath });
        var response = await client.PostAsync("/api/files/process", content);
        return response.IsSuccessStatusCode;
    }

    public async Task<string> StartBatchProcessAsync(List<string> files)
    {
        using var client = CreateClient();
        
        var content = JsonContent.Create(new { files });
        var response = await client.PostAsync("/api/jobs/process-selected", content);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JobResponse>(_jsonOptions);
        return result?.JobId ?? throw new Exception("Failed to start batch process");
    }

    public async Task<JobStatus?> GetJobStatusAsync(string jobId)
    {
        using var client = CreateClient();
        
        var response = await client.GetAsync($"/api/jobs/{jobId}");
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<JobStatus>(_jsonOptions);
    }

    private class FilesResponse
    {
        public List<ComicFile> Files { get; set; } = new();
        public int TotalFiles { get; set; }
        public int TotalPages { get; set; }
        public int CurrentPage { get; set; }
    }

    private class JobResponse
    {
        public string JobId { get; set; } = string.Empty;
        public int TotalItems { get; set; }
    }
}
