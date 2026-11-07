using System.Globalization;
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

    public GitHubIssuePermanentException(string message, bool isAssigneeRejection) : base(message)
    {
        IsAssigneeRejection = isAssigneeRejection;
    }

    /// <summary>
    /// True when GitHub rejected the request specifically because the requested
    /// assignee could not be assigned. Only then is retrying the same request
    /// unassigned worthwhile; every other rejection would fail identically.
    /// </summary>
    public bool IsAssigneeRejection { get; }
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

    public GitHubIssueTransientException(
        string message,
        Exception? inner,
        bool mayHaveSucceeded,
        TimeSpan? retryAfter)
        : base(message, inner)
    {
        MayHaveSucceeded = mayHaveSucceeded;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// True when GitHub may have carried out the request even though no usable
    /// answer came back: the request timed out, the connection broke after it
    /// was sent, or GitHub answered with a server error. Repeating a request
    /// that creates something can then create it twice.
    /// </summary>
    public bool MayHaveSucceeded { get; }

    /// <summary>
    /// How long GitHub asked the caller to wait (Retry-After, or until the
    /// rate limit resets) before sending another request; null when it did
    /// not say.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
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

    /// <summary>
    /// Finds an issue (open or closed) created since <paramref name="createdSince"/>
    /// whose body carries the fingerprint block for <paramref name="fingerprint"/>
    /// (<see cref="ErrorReportIssueFormatter.BuildFingerprintBlock"/>), so a
    /// creation whose outcome is unknown is not repeated. Null when there is none.
    /// </summary>
    Task<GitHubIssueReference?> FindIssueByFingerprintAsync(
        GitHubIssueTarget target,
        string fingerprint,
        DateTimeOffset createdSince,
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

    /// <summary>Issues requested per page when looking an issue up by fingerprint.</summary>
    internal const int LookupPageSize = 100;

    /// <summary>
    /// Most pages read when looking an issue up by fingerprint. Pages are
    /// newest first and the lookup stops at the first issue created before the
    /// window, so this only matters in a very busy repository.
    /// </summary>
    internal const int MaxLookupPages = 10;

    /// <summary>
    /// How long to stay away after a rate-limit response that gives no time:
    /// GitHub asks for at least a minute before retrying.
    /// </summary>
    internal static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;

    public GitHubIssueClient(IHttpClientFactory httpClientFactory, TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        using var response = await SendAsync(
            target,
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}/issues",
            payload,
            cancellationToken);

        // From here on GitHub has created the issue; only its number is
        // missing, so posting it again would file a duplicate.
        try
        {
            var created = await ReadJsonAsync<IssueResponse>(response.Content, cancellationToken);
            if (created is null || created.Number <= 0)
            {
                throw new GitHubIssueTransientException(
                    "GitHub accepted the issue but returned no issue number",
                    null,
                    mayHaveSucceeded: true,
                    retryAfter: null);
            }

            return new GitHubIssueReference(created.Number, created.HtmlUrl);
        }
        catch (JsonException ex)
        {
            throw new GitHubIssueTransientException(
                "GitHub returned a response that could not be parsed",
                ex,
                mayHaveSucceeded: true,
                retryAfter: null);
        }
    }

    public async Task<GitHubIssueReference?> FindIssueByFingerprintAsync(
        GitHubIssueTarget target,
        string fingerprint,
        DateTimeOffset createdSince,
        CancellationToken cancellationToken)
    {
        // The issues listing is read rather than the search API: search is
        // eventually consistent, so an issue created moments ago by a request
        // that timed out may not be found yet.
        var marker = $"fingerprint: {fingerprint}";
        var since = Uri.EscapeDataString(createdSince.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        var basePath = $"repos/{Uri.EscapeDataString(target.Owner)}/{Uri.EscapeDataString(target.Repo)}/issues";

        for (var page = 1; page <= MaxLookupPages; page++)
        {
            using var response = await SendAsync(
                target,
                HttpMethod.Get,
                $"{basePath}?state=all&sort=created&direction=desc&since={since}&per_page={LookupPageSize}&page={page}",
                payload: null,
                cancellationToken);

            List<IssueListItem>? issues;
            try
            {
                issues = await ReadJsonAsync<List<IssueListItem>>(response.Content, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new GitHubIssueTransientException("GitHub returned a response that could not be parsed", ex);
            }

            if (issues is null || issues.Count == 0)
            {
                return null;
            }

            var match = issues.FirstOrDefault(issue =>
                issue.PullRequest is null &&
                issue.Number > 0 &&
                ContainsMarker(issue.Body, marker));
            if (match is not null)
            {
                return new GitHubIssueReference(match.Number, match.HtmlUrl);
            }

            // Newest first: once a page reaches issues created before the
            // window (listed because they were updated since), no later page
            // can hold the one being looked for.
            if (issues.Count < LookupPageSize || issues.Any(issue => issue.CreatedAt < createdSince))
            {
                return null;
            }
        }

        return null;
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
        object? payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, path)
        {
            Content = payload is null ? null : JsonContent.Create(payload, options: JsonOptions)
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
            // Only a failure to open the connection proves the request never
            // reached GitHub; one that broke later may have been carried out.
            var neverSent = ex.HttpRequestError is HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError;
            throw new GitHubIssueTransientException(
                "Could not reach GitHub",
                ex,
                mayHaveSucceeded: !neverSent,
                retryAfter: null);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubIssueTransientException(
                "Request to GitHub timed out",
                ex,
                mayHaveSucceeded: true,
                retryAfter: null);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        var isRateLimited = IsRateLimited(response) || status == HttpStatusCode.TooManyRequests;
        var retryAfter = GetRetryAfter(response, isRateLimited);
        var isAssigneeRejection = status == HttpStatusCode.UnprocessableEntity
            && await IsAssigneeValidationFailureAsync(response, cancellationToken);
        response.Dispose();

        // A rate-limited or unavailable GitHub is a "come back later"; anything
        // else is a misconfiguration that retrying would only turn into an
        // endless loop of rejected requests. Secondary rate limits arrive as a
        // 403 rather than a 429, so the rate-limit headers decide rather than
        // the status code alone — a permissions 403 stays permanent.
        if (isRateLimited
            || status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            // A rate-limited request was refused outright; a server error or
            // timeout may have happened after the request was carried out.
            throw new GitHubIssueTransientException(
                $"GitHub returned {(int)status} ({status})",
                null,
                mayHaveSucceeded: !isRateLimited,
                retryAfter);
        }

        // The response body is intentionally not included: it echoes request
        // content, which is the very error text we are trying not to duplicate
        // into local logs in unredacted form.
        throw new GitHubIssuePermanentException(
            $"GitHub rejected the request with {(int)status} ({status}). Check the repository name and that the token grants issues:write.",
            isAssigneeRejection);
    }

    /// <summary>
    /// Recognizes a rate-limited response, including the secondary rate limit
    /// that GitHub reports as <c>403</c> rather than <c>429</c>.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), out var value))
        {
            return value <= 0;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="body"/> contains <paramref name="marker"/> as a
    /// whole value, so one fingerprint never matches another it is a prefix of.
    /// </summary>
    private static bool ContainsMarker(string? body, string marker)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        for (var index = body.IndexOf(marker, StringComparison.Ordinal);
             index >= 0;
             index = body.IndexOf(marker, index + 1, StringComparison.Ordinal))
        {
            var end = index + marker.Length;
            if (end == body.Length || !char.IsLetterOrDigit(body[end]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How long GitHub asked us to stay away: the <c>Retry-After</c> header,
    /// else for an exhausted quota the time until <c>x-ratelimit-reset</c>,
    /// else a minute for any other rate-limit response, as GitHub's guidance on
    /// secondary rate limits asks. Null for a response that names no wait and
    /// is not rate limited.
    /// </summary>
    private TimeSpan? GetRetryAfter(HttpResponseMessage response, bool isRateLimited)
    {
        var now = _timeProvider.GetUtcNow();
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is TimeSpan delta)
            {
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            if (retryAfter.Date is DateTimeOffset date)
            {
                return date > now ? date - now : TimeSpan.Zero;
            }
        }

        if (!isRateLimited)
        {
            return null;
        }

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues)
            && int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
            && remaining <= 0
            && response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
            return reset > now ? reset - now : TimeSpan.Zero;
        }

        return DefaultRateLimitWait;
    }

    /// <summary>
    /// Detects the specific validation failure that says the assignee could not
    /// be assigned. Only the <c>field</c>/<c>resource</c> markers are inspected;
    /// the rest of the body (which echoes the reported error text) is discarded.
    /// </summary>
    private static async Task<bool> IsAssigneeValidationFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadJsonAsync<ValidationErrorResponse>(response.Content, cancellationToken);
            return body?.Errors?.Any(e =>
                string.Equals(e.Field, "assignees", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Field, "assignee", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a response body as UTF-8 JSON whatever charset it declares. GitHub's
    /// REST API responses are UTF-8 JSON, so an incorrect charset declaration is
    /// ignored to keep <c>ReadFromJsonAsync</c> from throwing while it resolves the
    /// encoding: a name .NET cannot resolve (<c>utf8</c>, or <c>windows-1252</c>
    /// before anything has registered the code pages) throws
    /// <see cref="InvalidOperationException"/>, which would escape the
    /// transient/permanent classification, and any other non-UTF-8 charset
    /// garbles non-ASCII text. A body that cannot be parsed, including an empty
    /// one, still fails with <see cref="JsonException"/>, as it did with
    /// <c>ReadFromJsonAsync</c>.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private sealed record IssueResponse
    {
        public int Number { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }

    private sealed record IssueListItem
    {
        public int Number { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        public string? Body { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>Present only on pull requests, which the issues listing includes.</summary>
        [JsonPropertyName("pull_request")]
        public JsonElement? PullRequest { get; init; }
    }

    private sealed record ValidationErrorResponse
    {
        public IReadOnlyList<ValidationError>? Errors { get; init; }
    }

    private sealed record ValidationError
    {
        public string? Resource { get; init; }

        public string? Field { get; init; }

        public string? Code { get; init; }
    }
}
