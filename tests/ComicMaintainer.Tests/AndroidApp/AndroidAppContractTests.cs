using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.MauiApp.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Tests.AndroidApp;

/// <summary>
/// The Android app's real API and session code against the real server pipeline: routes, parameter names, JSON field
/// names and authentication. Before 3.0.0 the app sent no credentials and most of its requests and JSON no longer
/// matched the server, so it showed "connected" and then failed on every screen.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AndroidAppContractTests : IClassFixture<SessionTokenIntegrationTests.JwtHost>
{
    private const string Server = "http://localhost/";
    private const string Password = SessionTokenIntegrationTests.Password;
    private const int SqliteConstraintError = 19;

    private readonly SessionTokenIntegrationTests.JwtHost _host;

    public AndroidAppContractTests(SessionTokenIntegrationTests.JwtHost host)
    {
        _host = host;
    }

    private string WatchedDirectory =>
        _host.Factory.Services.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue.WatchedDirectory;

    [Fact]
    public async Task SigningIn_StoresTheSessionAndIdentifiesTheAccount()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var app = NewApp();

        await app.Session.SignInAsync(Server, user.UserName!, Password);

        Assert.Equal(CredentialKind.Session, app.Store.Stored!.Kind);
        Assert.Equal(Server, app.Settings.ServerUrl);
        Assert.Equal(user.UserName, app.Session.Account!.Username);
        Assert.True(app.Session.Account.CanModifyLibrary);
        // The password is what must never be kept on the device.
        Assert.DoesNotContain(Password, app.Store.Stored.Serialize(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongPassword_ShowsTheServersMessageAndStoresNothing()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var app = NewApp();

        var failure = await Assert.ThrowsAsync<ApiException>(() => app.Session.SignInAsync(Server, user.UserName!, "not-the-password"));

        Assert.Equal(ApiErrorKind.Unauthorized, failure.Kind);
        Assert.Contains("Invalid", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(app.Store.Stored);
    }

    [Fact]
    public async Task ASessionRestoredAfterARestart_IsConfirmedByTheServer()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var first = NewApp();
        await first.Session.SignInAsync(Server, user.UserName!, Password);

        var restarted = NewApp(first.Store, first.Settings);

        Assert.Equal(SessionRestoreResult.SignedIn, await restarted.Session.RestoreAsync());
        Assert.Equal(user.UserName, restarted.Session.Account!.Username);
    }

    [Fact]
    public async Task SigningOut_EndsTheSessionOnTheServerToo()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var app = NewApp();
        await app.Session.SignInAsync(Server, user.UserName!, Password);
        var token = app.Store.Stored!.Value;
        var ended = CaptureSessionEnd(app.Session);

        await app.Session.SignOutAsync();

        Assert.Null(app.Store.Stored);
        Assert.Null(Assert.Single(ended).Message);
        Assert.Equal(HttpStatusCode.Unauthorized, await GetUserStatusAsync(token));
    }

    [Fact]
    public async Task ASessionRevokedElsewhere_EndsVisiblyOnTheNextRequest()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var app = NewApp();
        await app.Session.SignInAsync(Server, user.UserName!, Password);
        var ended = CaptureSessionEnd(app.Session);

        // Signing out of every session is what a password change or a "sign out everywhere" does to this device.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Content = JsonContent.Create(new { allSessions = true }) })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _host.SignInAsync(user.UserName!, Password));
            using var response = await _host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var failure = await Assert.ThrowsAsync<ApiException>(() => app.Api.GetFilesAsync());

        Assert.Equal(ApiErrorKind.Unauthorized, failure.Kind);
        Assert.Null(app.Store.Stored);
        Assert.Equal(SessionManager.SessionExpiredMessage, Assert.Single(ended).Message);
    }

    [Fact]
    public async Task AnExpiredToken_IsDiscoveredAtStartupAndExplained()
    {
        var (user, _) = await _host.SignInNewUserAsync();
        var app = NewApp();
        app.Settings.ServerUrl = Server;
        app.Store.Stored = new ApiCredential(CredentialKind.Session, WriteExpiredSessionToken(user), Server);
        var ended = CaptureSessionEnd(app.Session);

        Assert.Equal(SessionRestoreResult.SignedOut, await app.Session.RestoreAsync());

        Assert.Null(app.Store.Stored);
        Assert.Equal(SessionManager.SessionExpiredMessage, Assert.Single(ended).Message);
    }

    [Fact]
    public async Task AnApiKey_SignsInWorksAndOutlivesSigningOutOfTheApp()
    {
        var (user, token) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(token);
        var app = NewApp();
        var ended = CaptureSessionEnd(app.Session);

        await app.Session.SignInWithApiKeyAsync(Server, "  " + apiKey + "\n");

        Assert.Equal(CredentialKind.ApiKey, app.Store.Stored!.Kind);
        Assert.Equal(apiKey, app.Store.Stored.Value);
        Assert.Equal(user.UserName, app.Session.Account!.Username);
        await app.Api.GetFilesAsync();

        await app.Session.SignOutAsync();

        Assert.Null(app.Store.Stored);
        Assert.Equal(SessionManager.ApiKeyStillValidNotice, Assert.Single(ended).Message);
        using var check = new HttpRequestMessage(HttpMethod.Get, "/api/auth/user");
        check.Headers.Add("X-API-Key", apiKey);
        using var response = await _host.Client.SendAsync(check);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARevokedApiKey_EndsTheSessionWithItsOwnExplanation()
    {
        var (_, token) = await _host.SignInNewUserAsync();
        var apiKey = await CreateApiKeyAsync(token);
        var app = NewApp();
        await app.Session.SignInWithApiKeyAsync(Server, apiKey);
        var ended = CaptureSessionEnd(app.Session);

        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/api-key"))
        {
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _host.Client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await Assert.ThrowsAsync<ApiException>(() => app.Api.GetFilesAsync());

        Assert.Equal(SessionManager.ApiKeyRejectedMessage, Assert.Single(ended).Message);
    }

    [Fact]
    public async Task AnUnknownApiKey_IsRefusedAndStoresNothing()
    {
        var app = NewApp();

        var failure = await Assert.ThrowsAsync<ApiException>(() => app.Session.SignInWithApiKeyAsync(Server, "cm_not-a-real-key"));

        Assert.Equal(ApiErrorKind.Unauthorized, failure.Kind);
        Assert.Equal(SessionManager.InvalidApiKeyMessage, failure.Message);
        Assert.Null(app.Store.Stored);
    }

    [Fact]
    public async Task TheLibrary_IsListedWithTheServersRealFields()
    {
        var app = await SignedInAppAsync();
        var token = NewToken();
        var path = Path.Join(WatchedDirectory, token, "Series #1.cbz");
        var modified = new DateTime(2024, 3, 2, 1, 0, 0, DateTimeKind.Utc);
        await SeedAsync(new SeededFile(path, IsProcessed: true, Size: 4321, Modified: modified));

        var result = await app.Api.GetFilesAsync(search: token);

        var file = Assert.Single(result.Files);
        Assert.Equal(path, file.FilePath);
        Assert.Equal("Series #1.cbz", file.FileName);
        Assert.Equal(Path.GetDirectoryName(path), file.Directory);
        Assert.Equal(4321, file.FileSize);
        Assert.True(file.IsProcessed);
        Assert.False(file.IsDuplicate);
        Assert.True(file.ModifiedUnixSeconds > 0);
        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(1, result.Page);
    }

    [Fact]
    public async Task ThePageSize_IsHonoured()
    {
        var app = await SignedInAppAsync();
        var token = NewToken();
        await SeedAsync(
            new SeededFile(Path.Join(WatchedDirectory, token, "a.cbz")),
            new SeededFile(Path.Join(WatchedDirectory, token, "b.cbz")),
            new SeededFile(Path.Join(WatchedDirectory, token, "c.cbz")));

        var second = await app.Api.GetFilesAsync(page: 2, perPage: 2, search: token);

        Assert.Equal("c.cbz", Assert.Single(second.Files).FileName);
        Assert.Equal(2, second.Page);
        Assert.Equal(2, second.TotalPages);
        Assert.Equal(3, second.TotalFiles);
    }

    [Theory]
    [InlineData("unmarked", "plain.cbz")]
    [InlineData("marked", "done.cbz")]
    [InlineData("duplicates", "twin.cbz")]
    public async Task TheFiltersTheAppOffers_AreOnesTheServerApplies(string filter, string expected)
    {
        var app = await SignedInAppAsync();
        var token = NewToken();
        await SeedAsync(
            new SeededFile(Path.Join(WatchedDirectory, token, "plain.cbz")),
            new SeededFile(Path.Join(WatchedDirectory, token, "done.cbz"), IsProcessed: true),
            new SeededFile(Path.Join(WatchedDirectory, token, "twin.cbz"), IsDuplicate: true));

        var result = await app.Api.GetFilesAsync(filter: filter, search: token);

        Assert.Equal(expected, Assert.Single(result.Files).FileName);
    }

    [Fact]
    public async Task ProcessingAFile_SendsItsPathTheWayTheServerReadsIt()
    {
        var app = await SignedInAppAsync();
        var path = CreateComicOnDisk(NewToken());

        await app.Api.ProcessFileAsync(path);
    }

    [Fact]
    public async Task ProcessingAFileOutsideTheLibrary_ShowsTheServersReason()
    {
        var app = await SignedInAppAsync();

        var failure = await Assert.ThrowsAsync<ApiException>(() => app.Api.ProcessFileAsync(Path.Join(Path.GetTempPath(), "elsewhere.cbz")));

        Assert.Equal(ApiErrorKind.Rejected, failure.Kind);
        Assert.Equal("Invalid file path", failure.Message);
    }

    [Fact]
    public async Task AReadOnlyAccount_IsToldItMayNotAndStaysSignedIn()
    {
        var app = await SignedInAppAsync("ReadOnly");
        var ended = CaptureSessionEnd(app.Session);

        var failure = await Assert.ThrowsAsync<ApiException>(() => app.Api.ProcessFileAsync(CreateComicOnDisk(NewToken())));

        Assert.Equal(ApiErrorKind.Forbidden, failure.Kind);
        Assert.Equal("Insufficient permissions", failure.Message);
        Assert.False(app.Session.Account!.CanModifyLibrary);
        Assert.NotNull(app.Store.Stored);
        Assert.Empty(ended);
    }

    [Fact]
    public async Task ABatchJob_ReturnsAnIdThatItsProgressCanBeReadBy()
    {
        var app = await SignedInAppAsync();
        var path = CreateComicOnDisk(NewToken());

        var jobId = await app.Api.StartBatchProcessAsync([path]);

        Assert.True(Guid.TryParse(jobId, out var parsed) && parsed != Guid.Empty, jobId);
        var job = await app.Api.GetJobStatusAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(1, job.TotalItems);
        Assert.Contains(job.Status, new[] { "queued", "running", "completed", "failed" });
    }

    [Fact]
    public async Task AJobTheServerDoesNotKnow_HasNoStatus()
    {
        var app = await SignedInAppAsync();

        Assert.Null(await app.Api.GetJobStatusAsync(Guid.NewGuid().ToString()));
    }

    // The path travels in the route as URL-safe base64 of its UTF-8 bytes, which plain ASCII names would not exercise.
    [Fact]
    public async Task DeletingAFile_FindsItEvenWithANonAsciiName()
    {
        var app = await SignedInAppAsync();
        var token = NewToken();
        var path = CreateComicOnDisk(token, "Émile ~ 日本語.cbz");
        await SeedAsync(new SeededFile(path));

        await app.Api.DeleteFileAsync(path);

        Assert.False(File.Exists(path));
        Assert.Empty((await app.Api.GetFilesAsync(search: token)).Files);
    }

    [Fact]
    public async Task DeletingSelectedFiles_StartsAJobThatDeletesThem()
    {
        var app = await SignedInAppAsync();
        var path = CreateComicOnDisk(NewToken());
        await SeedAsync(new SeededFile(path));

        var jobId = await app.Api.StartBatchDeleteAsync([path]);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(File.Exists(path));
        Assert.NotNull(await app.Api.GetJobStatusAsync(jobId));
    }

    [Fact]
    public async Task TheConnectionTest_RecognisesARealServerWithoutAnAccount()
    {
        var app = NewApp();

        var result = await app.Api.TestConnectionAsync(Server);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Reached ComicMaintainer ", result.Message, StringComparison.Ordinal);
    }

    private AndroidApp NewApp(InMemoryCredentialStore? store = null, InMemorySettings? settings = null)
    {
        store ??= new InMemoryCredentialStore();
        settings ??= new InMemorySettings();
        var transport = new ApiTransport(new FactoryHttpClients(_host.Factory));
        var session = new SessionManager(transport, store, settings);
        return new AndroidApp(session, new ApiService(transport, session), store, settings);
    }

    private async Task<AndroidApp> SignedInAppAsync(string role = "User")
    {
        var (user, _) = await _host.SignInNewUserAsync(role);
        var app = NewApp();
        await app.Session.SignInAsync(Server, user.UserName!, Password);
        return app;
    }

    private static List<SessionEndedEventArgs> CaptureSessionEnd(ISessionManager session)
    {
        var ended = new List<SessionEndedEventArgs>();
        session.SessionEnded += (_, e) => ended.Add(e);
        return ended;
    }

    private static string NewToken() => "android" + Guid.NewGuid().ToString("N")[..12];

    private async Task<HttpStatusCode> GetUserStatusAsync(string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await _host.Client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<string> CreateApiKeyAsync(string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/api-key") { Content = JsonContent.Create(new { currentPassword = Password }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("apiKey").GetString()!;
    }

    private string WriteExpiredSessionToken(ApplicationUser user)
    {
        var settings = _host.Factory.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
        using var scope = _host.Factory.Services.CreateScope();
        var stamp = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(user.Id).GetAwaiter().GetResult()!.SecurityStamp;
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(AuthTokens.SecurityStampClaim, AuthTokens.Fingerprint(stamp)),
        ];
        var key = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            settings.Issuer, settings.Audience, claims, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), key));
    }

    private sealed record SeededFile(string Path, bool IsProcessed = false, bool IsDuplicate = false, long Size = 1024, DateTime? Modified = null);

    private async Task SeedAsync(params SeededFile[] files)
    {
        var contexts = _host.Factory.Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        foreach (var file in files)
        {
            // One context per file, because a context whose save failed still tracks the rejected row.
            await using var db = await contexts.CreateDbContextAsync();
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = file.Path,
                FileName = Path.GetFileName(file.Path),
                Directory = Path.GetDirectoryName(file.Path)!,
                FileSize = file.Size,
                LastModified = file.Modified ?? DateTime.UtcNow,
                IsRenamed = file.IsProcessed,
                IsNormalized = file.IsProcessed,
                IsDuplicate = file.IsDuplicate,
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintError })
            {
                // The server's file watcher got there first: it indexes a comic as soon as it appears in the library.
            }
        }
    }

    private string CreateComicOnDisk(string folder, string name = "Comic.cbz")
    {
        var directory = Directory.CreateDirectory(Path.Join(WatchedDirectory, folder)).FullName;
        var path = Path.Join(directory, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var entry = archive.CreateEntry("page001.txt").Open())
        {
            entry.Write("page"u8);
        }

        return path;
    }

    private sealed record AndroidApp(ISessionManager Session, IApiService Api, InMemoryCredentialStore Store, InMemorySettings Settings);
}

/// <summary>Hands the app clients that talk to the in-memory server and, like the real client, do not follow redirects.</summary>
internal sealed class FactoryHttpClients(WebApplicationFactory<Program> factory) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
}

/// <summary>A server nobody has signed in to yet, whose clients all share one rate-limit partition.</summary>
[Trait("Category", "Integration")]
public sealed class AndroidAppFreshServerTests : IAsyncLifetime
{
    private const string Server = "http://localhost/";

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "AndroidFresh-" + Guid.NewGuid().ToString("N"));
    private TestWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        var watched = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
        var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        var config = Directory.CreateDirectory(Path.Join(_root, "config")).FullName;
        _factory = new TestWebApplicationFactory(builder =>
        {
            builder.UseSetting("AppSettings:WatchedDirectory", watched);
            builder.UseSetting("AppSettings:DuplicateDirectory", duplicates);
            builder.UseSetting("AppSettings:ConfigDirectory", config);
            builder.UseSetting("CONFIG_DIR", config);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_root);
    }

    private SessionManager NewSession(out ApiService api)
    {
        var transport = new ApiTransport(new FactoryHttpClients(_factory));
        var session = new SessionManager(transport, new InMemoryCredentialStore(), new InMemorySettings());
        api = new ApiService(transport, session);
        return session;
    }

    [Fact]
    public async Task AServerWithoutAccounts_SaysToSetItUpInsteadOfBlamingThePassword()
    {
        var session = NewSession(out var api);

        var failure = await Assert.ThrowsAsync<ApiException>(() => session.SignInAsync(Server, "admin", "whatever-it-is"));

        Assert.Equal(ApiErrorKind.SetupRequired, failure.Kind);
        Assert.Equal(SessionManager.SetupRequiredMessage, failure.Message);
        var connection = await api.TestConnectionAsync(Server);
        Assert.True(connection.Success);
        Assert.Contains(SessionManager.SetupRequiredMessage, connection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignInRateLimit_TellsTheUserHowLongToWait()
    {
        var session = NewSession(out _);
        ApiException? limited = null;

        for (var attempt = 0; attempt < 20 && limited is null; attempt++)
        {
            var failure = await Assert.ThrowsAsync<ApiException>(() => session.SignInAsync(Server, "admin", "wrong-password"));
            if (failure.Kind == ApiErrorKind.RateLimited)
            {
                limited = failure;
            }
        }

        Assert.NotNull(limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Matches(@"try again in \d+ (second|minute)", limited.Message);
    }
}
