using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.ErrorReporting;

namespace ComicMaintainer.Tests.ErrorReporting;

/// <summary>
/// Exercises <see cref="GitHubIssueClient"/> against a stubbed GitHub REST API:
/// request shape, success parsing, and how each failure is classified as
/// transient (retry later) or permanent (misconfiguration).
/// </summary>
public sealed class GitHubIssueClientTests
{
    private static readonly GitHubIssueTarget Target = new("acme", "comics", "ghp_test-token");

    [Fact]
    public async Task CreateIssueAsync_PostsTheIssueWithHeadersAndReturnsItsReference()
    {
        var (client, handler, factory) = CreateClient(_ => Json(
            HttpStatusCode.Created,
            """{ "number": 42, "html_url": "https://github.com/acme/comics/issues/42", "title": "ignored" }"""));

        var issue = await client.CreateIssueAsync(
            Target,
            "Crash in scanner",
            "Body text",
            new[] { "bug", "automated" },
            " octocat ",
            CancellationToken.None);

        Assert.Equal(new GitHubIssueReference(42, "https://github.com/acme/comics/issues/42"), issue);
        Assert.Equal(GitHubIssueClient.HttpClientName, factory.RequestedName);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.test/repos/acme/comics/issues", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer ghp_test-token", request.Authorization);
        Assert.Contains("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Crash in scanner", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Body text", body.RootElement.GetProperty("body").GetString());
        Assert.Equal(
            new[] { "bug", "automated" },
            body.RootElement.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new[] { "octocat" },
            body.RootElement.GetProperty("assignees").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateIssueAsync_OmitsEmptyLabelsAndBlankAssignees(string? assignee)
    {
        var (client, handler, _) = CreateClient(_ => Json(HttpStatusCode.Created, """{ "number": 7 }"""));

        var issue = await client.CreateIssueAsync(Target, "Title", "Body", Array.Empty<string>(), assignee, CancellationToken.None);

        Assert.Equal(7, issue.Number);
        Assert.Null(issue.HtmlUrl);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(
            new[] { "title", "body" },
            body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task CreateIssueAsync_EscapesTheOwnerAndRepositoryInThePath()
    {
        var (client, handler, _) = CreateClient(_ => Json(HttpStatusCode.Created, """{ "number": 1 }"""));

        await client.CreateIssueAsync(
            new GitHubIssueTarget("my org", "a?b#c", "token"),
            "Title",
            "Body",
            Array.Empty<string>(),
            null,
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/my%20org/a%3Fb%23c/issues", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal(string.Empty, request.Uri.Fragment);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "number": 0 }""")]
    [InlineData("""{ "number": -3 }""")]
    [InlineData("null")]
    public async Task CreateIssueAsync_TreatsASuccessWithoutAnIssueNumberAsTransient(string responseBody)
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.Created, responseBody));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal("GitHub accepted the issue but returned no issue number", ex.Message);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{ "number": "forty-two" }""")]
    [InlineData("""{ "number": 4""")]
    public async Task CreateIssueAsync_WrapsAnUnparseableSuccessResponse(string responseBody)
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.Created, responseBody));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal("GitHub returned a response that could not be parsed", ex.Message);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    // Charset names Encoding.GetEncoding does not resolve, which made ReadFromJsonAsync
    // throw. Each test asserts that first (AssertUndecodable), so a runtime that starts
    // accepting one fails loudly instead of silently testing nothing. windows-1252 is not
    // used: SharpCompress registers the code pages for the whole process the first time
    // it reads an archive, so it resolves once any archive test has run.
    public static TheoryData<string> UndecodableCharsets => new() { "utf8", "no-such-charset" };

    private static void AssertUndecodable(string charset)
        => Assert.Throws<ArgumentException>(() => Encoding.GetEncoding(charset));

    [Theory]
    [MemberData(nameof(UndecodableCharsets))]
    public async Task CreateIssueAsync_ReadsTheResponseWhateverTheDeclaredCharset(string charset)
    {
        AssertUndecodable(charset);

        // GitHub always sends UTF-8 JSON, but a proxy can label the body with a charset
        // .NET cannot decode. That used to escape as a raw InvalidOperationException,
        // so an issue GitHub had created went unrecorded.
        var (client, _, _) = CreateClient(_ => Json(
            HttpStatusCode.Created,
            """{ "number": 42, "html_url": "https://github.com/acme/comics/issues/42" }""",
            charset));

        var issue = await CreateIssue(client);

        Assert.Equal(new GitHubIssueReference(42, "https://github.com/acme/comics/issues/42"), issue);
    }

    [Fact]
    public async Task CreateIssueAsync_WrapsAnEmptyResponseAsUnparseable()
    {
        // ReadFromJsonAsync threw JsonException for an empty body; reading the body
        // directly keeps that, so it is still classified as unparseable.
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.Created, ""));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal("GitHub returned a response that could not be parsed", ex.Message);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("iso-8859-1")]
    public async Task CreateIssueAsync_ReadsNonAsciiTextAsUtf8WhateverTheDeclaredCharset(string charset)
    {
        // Decoding with the declared charset would garble the non-ASCII URL (or, for
        // windows-1252 before the code pages are registered, throw).
        const string url = "https://github.com/acme/bandes-dessinées/issues/42";
        var (client, _, _) = CreateClient(_ => Json(
            HttpStatusCode.Created,
            $$"""{ "number": 42, "html_url": "{{url}}" }""",
            charset));

        var issue = await CreateIssue(client);

        Assert.Equal(new GitHubIssueReference(42, url), issue);
    }

    [Theory]
    [MemberData(nameof(UndecodableCharsets))]
    public async Task CreateIssueAsync_WrapsAnUnparseableResponseWhateverTheDeclaredCharset(string charset)
    {
        AssertUndecodable(charset);

        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.Created, "this is not json", charset));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal("GitHub returned a response that could not be parsed", ex.Message);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task AddCommentAsync_PostsTheCommentToTheIssue()
    {
        var (client, handler, factory) = CreateClient(_ => Json(HttpStatusCode.Created, """{ "id": 99 }"""));

        await client.AddCommentAsync(new GitHubIssueTarget("my org", "repo", "comment-token"), 17, "Seen again", CancellationToken.None);

        Assert.Equal(GitHubIssueClient.HttpClientName, factory.RequestedName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/my%20org/repo/issues/17/comments", request.Uri.AbsolutePath);
        Assert.Equal("Bearer comment-token", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Seen again", body.RootElement.GetProperty("body").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task UnavailableOrThrottledResponses_AreTransient(HttpStatusCode status)
    {
        var (client, _, _) = CreateClient(_ => Json(status, """{ "message": "try later" }"""));

        var create = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));
        var comment = await Assert.ThrowsAsync<GitHubIssueTransientException>(
            () => client.AddCommentAsync(Target, 3, "body", CancellationToken.None));

        Assert.Equal($"GitHub returned {(int)status} ({status})", create.Message);
        Assert.Equal(create.Message, comment.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task RejectedRequests_ArePermanentAndNotAssigneeRejections(HttpStatusCode status)
    {
        var (client, _, _) = CreateClient(_ => Json(status, """{ "message": "echo of the secret error text" }"""));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(() => CreateIssue(client));

        Assert.False(ex.IsAssigneeRejection);
        Assert.StartsWith($"GitHub rejected the request with {(int)status} ({status}).", ex.Message);
        // The body echoes request content and must not leak into the exception.
        Assert.DoesNotContain("secret error text", ex.Message);
    }

    [Fact]
    public async Task AddCommentAsync_PropagatesPermanentRejections()
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.NotFound, """{ "message": "Not Found" }"""));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(
            () => client.AddCommentAsync(Target, 3, "body", CancellationToken.None));

        Assert.False(ex.IsAssigneeRejection);
        Assert.Contains("404", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ResponsesWithRetryAfter_AreTransient(HttpStatusCode status)
    {
        var (client, _, _) = CreateClient(_ =>
        {
            var response = Json(status, """{ "message": "secondary rate limit" }""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal($"GitHub returned {(int)status} ({status})", ex.Message);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("12", false)]
    [InlineData("soon", false)]
    public async Task ForbiddenResponses_UseTheRemainingRateLimitHeader(string remaining, bool transient)
    {
        var (client, _, _) = CreateClient(_ =>
        {
            var response = Json(HttpStatusCode.Forbidden, """{ "message": "forbidden" }""");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
            return response;
        });

        var ex = await Record.ExceptionAsync(() => CreateIssue(client));

        if (transient)
        {
            Assert.IsType<GitHubIssueTransientException>(ex);
        }
        else
        {
            var permanent = Assert.IsType<GitHubIssuePermanentException>(ex);
            Assert.False(permanent.IsAssigneeRejection);
        }
    }

    [Fact]
    public async Task TooManyRequestsWithRemainingQuota_IsStillTransient()
    {
        var (client, _, _) = CreateClient(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "100");
            return response;
        });

        await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));
    }

    [Theory]
    [InlineData("assignees")]
    [InlineData("assignee")]
    [InlineData("ASSIGNEES")]
    public async Task UnprocessableAssigneeValidationFailures_AreFlaggedAsAssigneeRejections(string field)
    {
        var (client, _, _) = CreateClient(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            $$"""
            {
              "message": "Validation Failed",
              "errors": [
                { "resource": "Issue", "field": "title", "code": "invalid" },
                { "resource": "Issue", "field": "{{field}}", "code": "invalid", "value": "ghost" }
              ]
            }
            """));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(() => CreateIssue(client, assignee: "ghost"));

        Assert.True(ex.IsAssigneeRejection);
        Assert.Contains("422", ex.Message);
    }

    [Theory]
    [InlineData("""{ "message": "Validation Failed", "errors": [ { "resource": "Issue", "field": "labels", "code": "invalid" } ] }""")]
    [InlineData("""{ "message": "Validation Failed", "errors": [ { "resource": "Issue", "code": "custom" } ] }""")]
    [InlineData("""{ "message": "Validation Failed", "errors": [] }""")]
    [InlineData("""{ "message": "Validation Failed", "errors": null }""")]
    [InlineData("""{ "message": "Validation Failed" }""")]
    [InlineData("null")]
    [InlineData("<html>not json</html>")]
    [InlineData("""{ "errors": "not-an-array" }""")]
    public async Task OtherUnprocessableResponses_AreNotAssigneeRejections(string responseBody)
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.UnprocessableEntity, responseBody));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(() => CreateIssue(client, assignee: "ghost"));

        Assert.False(ex.IsAssigneeRejection);
    }

    [Theory]
    [MemberData(nameof(UndecodableCharsets))]
    public async Task UnprocessableAssigneeValidationFailures_AreFlaggedWhateverTheDeclaredCharset(string charset)
    {
        AssertUndecodable(charset);

        var (client, _, _) = CreateClient(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            """{ "message": "Validation Failed", "errors": [ { "resource": "Issue", "field": "assignees", "code": "invalid" } ] }""",
            charset));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(() => CreateIssue(client, assignee: "octocat"));

        Assert.True(ex.IsAssigneeRejection);
    }

    [Theory]
    [MemberData(nameof(UndecodableCharsets))]
    public async Task UnparseableUnprocessableResponses_AreNotAssigneeRejectionsWhateverTheDeclaredCharset(string charset)
    {
        AssertUndecodable(charset);

        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.UnprocessableEntity, "<html>not json</html>", charset));

        var ex = await Assert.ThrowsAsync<GitHubIssuePermanentException>(() => CreateIssue(client, assignee: "octocat"));

        Assert.False(ex.IsAssigneeRejection);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ResponseBodyThatFailsToDownload_IsTransient(HttpStatusCode status)
    {
        // HttpClient buffers the body before returning, so a connection that
        // drops mid-body surfaces as an HttpRequestException from SendAsync.
        var (client, _, _) = CreateClient(_ => new HttpResponseMessage(status)
        {
            Content = new FailingContent(() => new IOException("connection reset while reading"))
        });

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client, assignee: "ghost"));

        Assert.Equal("Could not reach GitHub", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task NetworkFailures_AreTransient()
    {
        var failure = new HttpRequestException("connection refused");
        var (client, _, _) = CreateClient(_ => throw failure);

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal("Could not reach GitHub", ex.Message);
        Assert.Same(failure, ex.InnerException);
    }

    [Fact]
    public async Task TimeoutsWithoutCallerCancellation_AreTransient()
    {
        var (client, _, _) = CreateClient(_ => throw new TaskCanceledException("timed out"));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(
            () => client.AddCommentAsync(Target, 1, "body", CancellationToken.None));

        Assert.Equal("Request to GitHub timed out", ex.Message);
        Assert.IsAssignableFrom<TaskCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesInsteadOfBeingReportedAsATimeout()
    {
        using var cts = new CancellationTokenSource();
        var (client, handler, _) = CreateClient((request, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.Created, """{ "number": 1 }""");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateIssueAsync(
            Target, "Title", "Body", Array.Empty<string>(), null, cts.Token));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrors_MayHaveCreatedTheIssue(HttpStatusCode status)
    {
        var (client, _, _) = CreateClient(_ => Json(status, "{}"));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.True(ex.MayHaveSucceeded);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task Timeouts_MayHaveCreatedTheIssue()
    {
        var (client, _, _) = CreateClient(_ => throw new TaskCanceledException("timed out"));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.True(ex.MayHaveSucceeded);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, false)]
    [InlineData(HttpRequestError.NameResolutionError, false)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    [InlineData(HttpRequestError.Unknown, true)]
    public async Task NetworkFailures_MayHaveCreatedTheIssueOnlyOnceTheRequestWasSent(HttpRequestError error, bool mayHaveSucceeded)
    {
        var (client, _, _) = CreateClient(_ => throw new HttpRequestException(error, "network failure"));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal(mayHaveSucceeded, ex.MayHaveSucceeded);
    }

    [Theory]
    [InlineData("""{ "html_url": "https://github.com/acme/comics/issues/9" }""")]
    [InlineData("not json")]
    public async Task ASuccessWhoseNumberCannotBeRead_HasCreatedTheIssue(string responseBody)
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.Created, responseBody));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.True(ex.MayHaveSucceeded);
    }

    [Fact]
    public async Task RetryAfterInSeconds_IsCarriedOnTheFailure()
    {
        var (client, _, _) = CreateClient(_ =>
        {
            var response = Json(HttpStatusCode.Forbidden, """{ "message": "You have exceeded a secondary rate limit" }""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.False(ex.MayHaveSucceeded);
    }

    [Fact]
    public async Task RetryAfterAsADate_IsMeasuredFromNow()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero));
        var (client, _, _) = CreateClient(
            (_, _) =>
            {
                var response = Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    new DateTimeOffset(2026, 5, 4, 8, 3, 0, TimeSpan.Zero));
                return response;
            },
            time);

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal(TimeSpan.FromMinutes(3), ex.RetryAfter);
    }

    [Fact]
    public async Task AnExhaustedQuota_WaitsUntilTheRateLimitResets()
    {
        var now = new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var (client, _, _) = CreateClient(
            (_, _) =>
            {
                var response = Json(HttpStatusCode.Forbidden, """{ "message": "API rate limit exceeded" }""");
                response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
                response.Headers.TryAddWithoutValidation(
                    "x-ratelimit-reset",
                    now.AddMinutes(17).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return response;
            },
            time);

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal(TimeSpan.FromMinutes(17), ex.RetryAfter);
    }

    [Fact]
    public async Task ARateLimitWithoutAWaitTime_WaitsAMinute()
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.TooManyRequests, "{}"));

        var ex = await Assert.ThrowsAsync<GitHubIssueTransientException>(() => CreateIssue(client));

        Assert.Equal(TimeSpan.FromMinutes(1), ex.RetryAfter);
        Assert.False(ex.MayHaveSucceeded);
    }

    [Fact]
    public async Task FindIssueByFingerprintAsync_ReadsTheNewestIssuesAndMatchesTheFingerprintBlock()
    {
        var since = new DateTimeOffset(2026, 5, 4, 8, 25, 0, TimeSpan.Zero);
        var body = "Report text\n\n" + ErrorReportIssueFormatter.BuildFingerprintBlock("abc123");
        var (client, handler, _) = CreateClient(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new object[]
        {
            new { number = 12, html_url = "https://github.com/acme/comics/pull/12", body, created_at = "2026-05-04T08:31:00Z", pull_request = new { url = "x" } },
            new { number = 11, html_url = "https://github.com/acme/comics/issues/11", body = "fingerprint: abc1234", created_at = "2026-05-04T08:30:30Z" },
            new { number = 10, html_url = "https://github.com/acme/comics/issues/10", body = body.Replace("\n", "\r\n"), created_at = "2026-05-04T08:30:00Z" },
        })));

        var issue = await client.FindIssueByFingerprintAsync(Target, "abc123", since, CancellationToken.None);

        Assert.Equal(new GitHubIssueReference(10, "https://github.com/acme/comics/issues/10"), issue);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Body);
        Assert.Equal("/repos/acme/comics/issues", request.Uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal("all", query["state"]);
        Assert.Equal("created", query["sort"]);
        Assert.Equal("desc", query["direction"]);
        Assert.Equal("2026-05-04T08:25:00Z", query["since"]);
        Assert.Equal("100", query["per_page"]);
        Assert.Equal("1", query["page"]);
    }

    [Fact]
    public async Task FindIssueByFingerprintAsync_StopsAtTheFirstIssueCreatedBeforeTheWindow()
    {
        var since = new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
        var (client, handler, _) = CreateClient(request =>
        {
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            var issues = Enumerable.Range(0, GitHubIssueClient.LookupPageSize).Select(i => new
            {
                number = 1000 - (page * 100) - i,
                body = "unrelated",
                // The second page reaches issues older than the window.
                created_at = (page == 1 ? since.AddMinutes(30) : since.AddMinutes(-30)).ToString("O"),
            });
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(issues));
        });

        var issue = await client.FindIssueByFingerprintAsync(Target, "abc123", since, CancellationToken.None);

        Assert.Null(issue);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FindIssueByFingerprintAsync_ReadsABoundedNumberOfPages()
    {
        var since = new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
        var (client, handler, _) = CreateClient(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(
            Enumerable.Range(0, GitHubIssueClient.LookupPageSize).Select(i => new
            {
                number = i + 1,
                body = "unrelated",
                created_at = since.AddHours(1).ToString("O"),
            }))));

        Assert.Null(await client.FindIssueByFingerprintAsync(Target, "abc123", since, CancellationToken.None));
        Assert.Equal(GitHubIssueClient.MaxLookupPages, handler.Requests.Count);
    }

    [Fact]
    public async Task FindIssueByFingerprintAsync_ReturnsNullForAnEmptyListing()
    {
        var (client, handler, _) = CreateClient(_ => Json(HttpStatusCode.OK, "[]"));

        Assert.Null(await client.FindIssueByFingerprintAsync(Target, "abc123", DateTimeOffset.UnixEpoch, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FindIssueByFingerprintAsync_ClassifiesFailuresLikeEveryOtherRequest()
    {
        var (client, _, _) = CreateClient(_ => Json(HttpStatusCode.BadGateway, "{}"));

        await Assert.ThrowsAsync<GitHubIssueTransientException>(
            () => client.FindIssueByFingerprintAsync(Target, "abc123", DateTimeOffset.UnixEpoch, CancellationToken.None));
    }

    private static Task<GitHubIssueReference> CreateIssue(GitHubIssueClient client, string? assignee = null)
        => client.CreateIssueAsync(Target, "Title", "Body", new[] { "bug" }, assignee, CancellationToken.None);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>A UTF-8 JSON body whose <c>Content-Type</c> declares <paramref name="charset"/> instead.</summary>
    private static HttpResponseMessage Json(HttpStatusCode status, string body, string charset)
    {
        var response = Json(status, body);
        response.Content.Headers.ContentType!.CharSet = charset;
        return response;
    }

    private static (GitHubIssueClient Client, StubHandler Handler, StubHttpClientFactory Factory) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        => CreateClient((request, _) => respond(request));

    private static (GitHubIssueClient Client, StubHandler Handler, StubHttpClientFactory Factory) CreateClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond,
        TimeProvider? timeProvider = null)
    {
        var handler = new StubHandler(respond);
        var factory = new StubHttpClientFactory(handler);
        return (new GitHubIssueClient(factory, timeProvider), handler, factory);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        IReadOnlyList<string> Accept,
        string? ApiVersion,
        string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(CancellationToken.None);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.Select(a => a.MediaType ?? string.Empty).ToList(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? versions.Single() : null,
                body));
            return _respond(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.github.test/")
            };
        }
    }

    /// <summary>Response content whose body fails to load with the supplied exception.</summary>
    private sealed class FailingContent : HttpContent
    {
        private readonly Func<Exception> _failure;

        public FailingContent(Func<Exception> failure)
        {
            _failure = failure;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(_failure());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
