using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComicMaintainer.Core.ErrorReporting;

/// <summary>Identity of an issue on GitHub.</summary>
public sealed record GitHubIssueReference(int Number, string? HtmlUrl);

/// <summary>
/// Raised when GitHub rejects a request in a way that retrying cannot fix
/// (bad token, missing repository, insufficient scope).
/// </summary>
public sealed class GitHubIssuePermanentException : Exception
{
    public GitHubIssuePermanentException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when GitHub is momentarily unavailable or rate-limiting. The caller
/// is expected to back off and retry.
/// </summary>
public sealed class GitHubIssueTransientException : Exception
{
    public GitHubIssueTransientException(string message) : base(message)
    {
    }

    public GitHubIssueTransientException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Minimal GitHub issues API surface needed to report field errors.</summary>
public interface IGitHubIssueClient
{
    Task<GitHubIssueReference> CreateIssueAsync(
        GitHubIssueTarget target,
        string title,
        string body,
        IReadOnlyCollection<string> labels,
        string? assignee,
        CancellationToken cancellationToken);

    Task AddCommentAsync(
        GitHubIssueTarget target,
        int issueNumber,
        string body,
        CancellationToken cancellationToken);
}

/// <summary>Repository and credentials an issue is filed against.</summary>
public sealed record GitHubIssueTarget(string Owner, string Repo, string Token);

/// <summary>
/// <see cref="IGitHubIssueClient"/> over the GitHub REST API.
/// </summary>
/// <remarks>
/// Written by hand against <c>POST /repos/{owner}/{repo}/issues</c> rather than
/// taking a dependency on Octokit: two endpoints do not justify pulling a large
/// client library (and its transitive surface) into a process whose whole job
/// is managing comic files.
/// </remarks>
public sealed class GitHubIssueClient : IGitHubIssueClient
{
    /// <summary>Named <see cref="HttpClient"/> registered for this client.</summary>
    public const string HttpClientName = "GitHubIssueReporting";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubIssueClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<GitHubIssueReference> CreateIssueAsync(
        GitHubIssueTarget target,
        string title,
        string body,
        IReadOnlyCollection<string> labels,
        string? assignee,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["body"] = body
        };

        if (labels.Count > 0)
        {
            payload["labels"] = labels;
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            payload["assignees"] = new[] { assignee.Trim() };
        }

        var response = await SendAsync(
            target,
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}/issues",
            payload,
            cancellationToken);

        try
        {
            var created = await response.Content.ReadFromJsonAsync<IssueResponse>(JsonOptions, cancellationToken);
            if (created is null || created.Number <= 0)
            {
                throw new GitHubIssueTransientException("GitHub accepted the issue but returned no issue number");
            }

            return new GitHubIssueReference(created.Number, created.HtmlUrl);
        }
        catch (JsonException ex)
        {
            throw new GitHubIssueTransientException("GitHub returned a response that could not be parsed", ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task AddCommentAsync(
        GitHubIssueTarget target,
        int issueNumber,
        string body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            target,
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}/issues/{issueNumber}/comments",
            new Dictionary<string, object?> { ["body"] = body },
            cancellationToken);

        response.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        GitHubIssueTarget target,
        HttpMethod method,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        // The token is attached per-request rather than baked into the named
        // client so a rotated token takes effect immediately and no credential
        // is retained on a pooled, long-lived object.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubIssueTransientException("Could not reach GitHub", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubIssueTransientException("Request to GitHub timed out", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();

        // A rate-limited or unavailable GitHub is a "come back later"; anything
        // else is a misconfiguration that retrying would only turn into an
        // endless loop of rejected requests.
        if (status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            throw new GitHubIssueTransientException($"GitHub returned {(int)status} ({status})");
        }

        // The response body is intentionally not included: it echoes request
        // content, which is the very error text we are trying not to duplicate
        // into local logs in unredacted form.
        throw new GitHubIssuePermanentException(
            $"GitHub rejected the request with {(int)status} ({status}). Check the repository name and that the token grants issues:write.");
    }

    private sealed record IssueResponse
    {
        public int Number { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }
}
