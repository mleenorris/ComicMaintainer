using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting.Interfaces;
using ComicMaintainer.Core.ErrorReporting.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.ErrorReporting.Services;

/// <summary>
/// Opt-in transport that files the issue directly through the GitHub REST API.
/// </summary>
/// <remarks>
/// <para>
/// Requires a fine-grained personal access token holding nothing beyond
/// <c>issues: write</c> on the configured repository. The token is read from
/// settings on every call so revoking it takes effect immediately, and it is
/// never written to a log or returned by the settings API.
/// </para>
/// <para>
/// Before opening an issue the transport searches for an open issue already
/// carrying this fingerprint's marker and comments on it instead. That keeps
/// deduplication working across instances, which the local database cannot do:
/// ten users hitting the same defect should produce one issue with ten
/// confirmations, not ten issues.
/// </para>
/// </remarks>
public sealed class GitHubIssueErrorReportTransport : IErrorReportTransport
{
    /// <summary>Named <c>HttpClient</c> registered for this transport.</summary>
    public const string HttpClientName = "github-issues";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<GitHubIssueErrorReportTransport> _logger;

    public GitHubIssueErrorReportTransport(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        ILogger<GitHubIssueErrorReportTransport> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Mode => "automatic";

    /// <inheritdoc />
    public bool TransmitsAutomatically => true;

    /// <inheritdoc />
    public async Task<ErrorReportTransportResult> SendAsync(
        ErrorReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var settings = _settings.CurrentValue;
        var token = settings.GitHubToken?.Trim();

        if (string.IsNullOrEmpty(token))
        {
            return new ErrorReportTransportResult(
                false, null, null,
                "Automatic reporting is selected but no GitHub token is configured.");
        }

        var repository = ConsentUrlErrorReportTransport.NormalizeRepository(settings.GitHubRepository);

        try
        {
            using var client = CreateClient(token);

            var existing = await FindExistingIssueAsync(client, repository, report.Fingerprint, cancellationToken);
            if (existing is { } issueNumber)
            {
                await CommentOnIssueAsync(client, repository, issueNumber, report, cancellationToken);
                return new ErrorReportTransportResult(
                    true,
                    issueNumber,
                    $"https://github.com/{repository}/issues/{issueNumber}",
                    "Added a recurrence comment to the existing issue.");
            }

            return await CreateIssueAsync(client, repository, report, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Never surface the token or the response body: the former is a
            // secret and the latter can echo the request back.
            _logger.LogWarning("Automatic error reporting failed: {Reason}", ex.GetType().Name);
            return new ErrorReportTransportResult(
                false, null, null, "Could not reach GitHub; the report was kept locally.");
        }
    }

    private HttpClient CreateClient(string token)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Finds an open issue whose body carries this fingerprint's marker.
    /// </summary>
    /// <remarks>
    /// The search is scoped to the repository, the <c>auto-reported</c> label
    /// and open state, and the marker is verified in the returned body rather
    /// than trusted from the search index, which is eventually consistent and
    /// tokenises punctuation unpredictably.
    /// </remarks>
    private static async Task<int?> FindExistingIssueAsync(
        HttpClient client,
        string repository,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(
            $"repo:{repository} is:issue is:open label:auto-reported \"{fingerprint}\"");

        using var response = await client.GetAsync(
            $"search/issues?q={query}&per_page=20", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken);
        var marker = ErrorReportIssueBuilder.Marker(fingerprint);

        return payload?.Items?
            .FirstOrDefault(item => item.Body?.Contains(marker, StringComparison.Ordinal) == true)?
            .Number;
    }

    private static async Task CommentOnIssueAsync(
        HttpClient client,
        string repository,
        int issueNumber,
        ErrorReport report,
        CancellationToken cancellationToken)
    {
        var body = $"Seen again on another instance: {report.OccurrenceCount} occurrence(s) " +
                   $"on version `{report.AppVersion}`, most recently {report.LastSeenUtc:u}.";

        using var response = await client.PostAsJsonAsync(
            $"repos/{repository}/issues/{issueNumber}/comments",
            new { body },
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private static async Task<ErrorReportTransportResult> CreateIssueAsync(
        HttpClient client,
        string repository,
        ErrorReport report,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var assignees = string.IsNullOrWhiteSpace(settings.GitHubIssueAssignee)
            ? Array.Empty<string>()
            : new[] { settings.GitHubIssueAssignee.Trim() };

        using var response = await client.PostAsJsonAsync(
            $"repos/{repository}/issues",
            new
            {
                title = ErrorReportIssueBuilder.BuildTitle(report),
                body = ErrorReportIssueBuilder.BuildBody(report),
                labels = ErrorReportIssueBuilder.BuildLabels(report),
                assignees,
            },
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ErrorReportTransportResult(
                false, null, null,
                $"GitHub rejected the report ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<IssueResponse>(JsonOptions, cancellationToken);

        return new ErrorReportTransportResult(
            true,
            created?.Number,
            created?.HtmlUrl,
            "Filed automatically.");
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("items")]
        public List<IssueResponse>? Items { get; set; }
    }

    private sealed class IssueResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
