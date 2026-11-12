using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Page, cover, progress and session endpoints through the real pipeline: the
/// API-wide <c>no-store</c> header, response compression and routing must leave
/// pages and covers cacheable by the requesting browser only, revalidation must
/// be answered with 304, an archive over the read limits must fail with a clear
/// 422, and a reading session must only be ended by its owner.
/// </summary>
/// <remarks>
/// Authorization is allowed through and the caller is named by an
/// <c>X-Test-User</c> header, so the tests do not depend on how tokens are issued.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ReaderServingIntegrationTests : IAsyncLifetime
{
    private const string UserHeader = "X-Test-User";

    private readonly string _root = Path.Join(AppContext.BaseDirectory, "reader-serving-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private string WatchedDirectory => Path.Join(_root, "watched");

    private string ConfigDirectory => Path.Join(_root, "config");

    private string CoverCacheDirectory => Path.Join(ConfigDirectory, "file-covers");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(WatchedDirectory);
        Directory.CreateDirectory(Path.Join(_root, "duplicates"));
        Directory.CreateDirectory(ConfigDirectory);

        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = ConfigDirectory,
            ["AppSettings:FileCoverCacheDirectory"] = CoverCacheDirectory,
            // The watcher must not rename or rewrite the archives under test.
            ["AppSettings:WatcherEnableRename"] = "false",
            ["AppSettings:WatcherEnableNormalize"] = "false",
            ["CONFIG_DIR"] = ConfigDirectory,
        };

        _factory = new TestWebApplicationFactory(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>());
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task Pages_AreCacheableOnlyByTheBrowserAndRevalidateWith304()
    {
        var comic = WriteComic("Saga 001.cbz", ("p1.jpg", "first page"), ("p2.jpg", "second page"));
        var url = PageUrl(comic, 2);

        using var first = await SendAsync(HttpMethod.Get, url, "alice");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("second page", await first.Content.ReadAsStringAsync());
        AssertPrivateOneHour(first);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.NotNull(first.Content.Headers.LastModified);

        using var revalidated = await SendAsync(HttpMethod.Get, url, "alice", ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        AssertPrivateOneHour(revalidated);
        Assert.Equal(etag, revalidated.Headers.ETag);

        // Another page of the same archive is a different representation.
        using var otherPage = await SendAsync(HttpMethod.Get, PageUrl(comic, 1), "alice", ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.OK, otherPage.StatusCode);
        Assert.Equal("first page", await otherPage.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Covers_AreCacheableOnlyByTheBrowserAndRevalidateFromTheFilesMetadata()
    {
        var comic = WriteComic("Saga 002.cbz", ("p1.jpg", "cover bytes, not a decodable image"));
        var url = $"/api/comicreader/cover?filePath={Uri.EscapeDataString(comic)}";

        using var first = await SendAsync(HttpMethod.Get, url, "alice");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertPrivateOneHour(first);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.True(Directory.Exists(CoverCacheDirectory));

        // A 304 needs neither the archive nor the cached thumbnail: with the cache
        // gone, revalidation still succeeds and does not regenerate it.
        Directory.Delete(CoverCacheDirectory, recursive: true);
        using var revalidated = await SendAsync(HttpMethod.Get, url, "alice", ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        AssertPrivateOneHour(revalidated);
        Assert.False(Directory.Exists(CoverCacheDirectory));
    }

    [Fact]
    public async Task APageThatWouldInflatePastTheLimits_IsRefusedWith422AndTheReason()
    {
        var path = Path.Join(WatchedDirectory, "Bomb 001.cbz");
        File.WriteAllBytes(path, ArchiveEntryReaderTests.BuildZip(
            ("p1.png", new byte[32 * 1024 * 1024], CompressionLevel.SmallestSize)));

        using var response = await SendAsync(HttpMethod.Get, PageUrl(path, 1), "alice");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("p1.png", error);
        Assert.Contains("decompression bomb", error);
        Assert.Null(response.Headers.ETag);
    }

    [Fact]
    public async Task SavingProgress_StoresTheResumePageAndTheUsersProgress()
    {
        var comic = WriteComic("Saga 003.cbz", ("p1.jpg", "1"), ("p2.jpg", "2"), ("p3.jpg", "3"), ("p4.jpg", "4"));
        var query = $"filePath={Uri.EscapeDataString(comic)}";

        using (var saved = await SendAsync(HttpMethod.Post, $"/api/comicreader/progress?{query}&page=3", "alice"))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using var resume = await SendAsync(HttpMethod.Get, $"/api/comicreader/progress?{query}", "alice");
        using (var body = JsonDocument.Parse(await resume.Content.ReadAsStringAsync()))
        {
            Assert.Equal(3, body.RootElement.GetProperty("currentPage").GetInt32());
        }

        using var otherUser = await SendAsync(HttpMethod.Get, $"/api/comicreader/progress?{query}", "bob");
        using (var body = JsonDocument.Parse(await otherUser.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("currentPage").GetInt32());
        }

        var contexts = _factory.Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var progress = await db.ReadingProgresses.AsNoTracking().SingleAsync(p => p.UserId == "alice" && p.ContentId == comic);
        Assert.Equal(3, progress.CurrentPage);
        Assert.Equal(4, progress.TotalPages);
        Assert.Equal(75d, progress.PercentComplete);
    }

    [Fact]
    public async Task AReadingSession_CanOnlyBeEndedByItsOwner()
    {
        using var started = await SendAsync(HttpMethod.Post, "/api/reader/session/start", "alice",
            body: new { contentId = "content-1", startPage = 1 });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Guid sessionId;
        using (var body = JsonDocument.Parse(await started.Content.ReadAsStringAsync()))
        {
            sessionId = body.RootElement.GetProperty("sessionId").GetGuid();
        }

        using var intruder = await SendAsync(HttpMethod.Post, "/api/reader/session/end", "bob",
            body: new { sessionId, endPage = 40 });
        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);

        using var owner = await SendAsync(HttpMethod.Post, "/api/reader/session/end", "alice",
            body: new { sessionId, endPage = 7 });
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        using (var body = JsonDocument.Parse(await owner.Content.ReadAsStringAsync()))
        {
            Assert.Equal(7, body.RootElement.GetProperty("endPage").GetInt32());
        }
    }

    // ---- helpers ----------------------------------------------------------------------

    private static void AssertPrivateOneHour(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.Private, $"Cache-Control: {cacheControl}");
        Assert.False(cacheControl.Public, $"Cache-Control: {cacheControl}");
        Assert.False(cacheControl.NoStore, $"Cache-Control: {cacheControl}");
        Assert.Equal(TimeSpan.FromHours(1), cacheControl.MaxAge);
    }

    private static string PageUrl(string comic, int page) =>
        $"/api/comicreader/page?filePath={Uri.EscapeDataString(comic)}&page={page}";

    private string WriteComic(string name, params (string Name, string Content)[] pages)
    {
        var path = Path.Join(WatchedDirectory, name);
        File.WriteAllBytes(path, ArchiveEntryReaderTests.BuildZip(pages
            .Select(p => (p.Name, Encoding.ASCII.GetBytes(p.Content), CompressionLevel.NoCompression))
            .ToArray()));
        return path;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string user, EntityTagHeaderValue? ifNoneMatch = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(UserHeader, user);
        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Lets every request through authorization as the user named by the
    /// <c>X-Test-User</c> header. The principal is set here, after policy
    /// evaluation, because evaluating a policy that names authentication schemes
    /// replaces <see cref="HttpContext.User"/> with whatever those schemes found.
    /// </summary>
    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (context.Request.Headers.TryGetValue(UserHeader, out var user) && !string.IsNullOrEmpty(user))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Name, user.ToString()) },
                    authenticationType: "Test"));
            }

            return next(context);
        }
    }
}
