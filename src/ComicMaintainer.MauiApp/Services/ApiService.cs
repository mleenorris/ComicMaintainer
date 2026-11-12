using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using ComicMaintainer.MauiApp.Models;

namespace ComicMaintainer.MauiApp.Services;

/// <inheritdoc cref="IApiService"/>
public sealed class ApiService : IApiService
{
    private readonly ApiTransport _transport;
    private readonly ISessionManager _session;

    public ApiService(ApiTransport transport, ISessionManager session)
    {
        _transport = transport;
        _session = session;
    }

    /// <inheritdoc/>
    public async Task<ConnectionTestResult> TestConnectionAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        if (!ServerAddress.TryParse(serverUrl, out var server, out var error))
        {
            return new ConnectionTestResult(false, error);
        }

        try
        {
            var versionBody = await _transport.SendAsync(server, HttpMethod.Get, "api/version", timeout: ApiTransport.QuickRequestTimeout, cancellationToken: cancellationToken);
            var version = ApiTransport.ParseJson(versionBody, ApiJsonContext.Default.VersionResponse);
            if (string.IsNullOrEmpty(version.Version) || string.IsNullOrEmpty(version.Platform))
            {
                return new ConnectionTestResult(false, "Something answered at this address, but it is not a ComicMaintainer server.");
            }

            var setupBody = await _transport.SendAsync(server, HttpMethod.Get, "api/auth/setup-required", timeout: ApiTransport.QuickRequestTimeout, cancellationToken: cancellationToken);
            return ApiTransport.ParseJson(setupBody, ApiJsonContext.Default.SetupRequiredResponse).SetupRequired
                ? new ConnectionTestResult(true, $"Reached ComicMaintainer {version.Version}. {SessionManager.SetupRequiredMessage}")
                : new ConnectionTestResult(true, $"Reached ComicMaintainer {version.Version}.");
        }
        catch (ApiException exception)
        {
            return new ConnectionTestResult(false, exception.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<FilesPage> GetFilesAsync(int page = 1, int perPage = 100, string? filter = null, string? search = null, CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("api/files?page=")
            .Append(page.ToString(CultureInfo.InvariantCulture))
            .Append("&per_page=")
            .Append(perPage.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query.Append("&filter=").Append(Uri.EscapeDataString(filter));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Append("&search=").Append(Uri.EscapeDataString(search));
        }

        var body = await SendAsync(HttpMethod.Get, query.ToString(), null, cancellationToken);
        return ApiTransport.ParseJson(body, ApiJsonContext.Default.FilesPage);
    }

    /// <inheritdoc/>
    public Task ProcessFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/files/process?filePath=" + Uri.EscapeDataString(filePath), null, cancellationToken);

    /// <inheritdoc/>
    public Task<string> StartBatchProcessAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default) =>
        StartSelectedFilesJobAsync("api/jobs/process-selected", files, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, "api/files/" + EncodePathSegment(filePath), null, cancellationToken);

    /// <inheritdoc/>
    public Task<string> StartBatchDeleteAsync(IReadOnlyCollection<string> files, CancellationToken cancellationToken = default) =>
        StartSelectedFilesJobAsync("api/jobs/delete-selected", files, cancellationToken);

    /// <inheritdoc/>
    public async Task<JobStatusInfo?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await SendAsync(HttpMethod.Get, "api/jobs/" + Uri.EscapeDataString(jobId), null, cancellationToken);
            return ApiTransport.ParseJson(body, ApiJsonContext.Default.JobStatusInfo);
        }
        catch (ApiException exception) when (exception.Kind == ApiErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task<string> StartSelectedFilesJobAsync(string path, IReadOnlyCollection<string> files, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(new SelectedFilesRequest(files), ApiJsonContext.Default.SelectedFilesRequest);
        var body = await SendAsync(HttpMethod.Post, path, content, cancellationToken);
        var jobId = ApiTransport.ParseJson(body, ApiJsonContext.Default.JobStartedResponse).JobId;
        return string.IsNullOrEmpty(jobId)
            ? throw new ApiException(ApiErrorKind.InvalidResponse, "The server accepted the request but did not say which job it started.")
            : jobId;
    }

    private async Task<string> SendAsync(HttpMethod method, string relativeUri, HttpContent? content, CancellationToken cancellationToken)
    {
        var credential = await _session.RequireCredentialAsync();
        try
        {
            return await _transport.SendAsync(credential.ServerUrl, method, relativeUri, content, credential, cancellationToken: cancellationToken);
        }
        catch (ApiException exception) when (exception.Kind == ApiErrorKind.Unauthorized)
        {
            await _session.NotifyUnauthorizedAsync(credential);
            throw;
        }
    }

    // The URL-safe base64 of the UTF-8 path, without padding: the form the endpoints that take a path in the route decode.
    private static string EncodePathSegment(string filePath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(filePath))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
