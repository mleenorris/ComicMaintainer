using System.Security.Claims;
using System.Text.Encodings.Web;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// The X-API-Key handler on a real SQLite users table: which requests it claims, who a key signs in as, and that it
/// records a key's use at most once per <see cref="ApiKeyAuthenticationHandler.LastUsedResolution"/> without ever
/// refusing a request because the bookkeeping failed.
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests : IAsyncLifetime
{
    private const string Key = "cmk_valid-key";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IAuthService> _authService = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManager = new(
        Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

    // Shared by every request of a test, as the app's cache and session validator are.
    private readonly ManualClock _clock = new();
    private readonly MemoryCache _cache;
    private readonly Mock<IAuthSessionValidator> _sessions = new();
    private readonly Dictionary<string, long> _accountVersions = new(StringComparer.Ordinal);
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public ApiKeyAuthenticationHandlerTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _sessions
            .Setup(s => s.GetAccountVersion(It.IsAny<string>()))
            .Returns((string userId) => _accountVersions.GetValueOrDefault(userId));
        _sessions
            .Setup(s => s.Invalidate(It.IsAny<string>()))
            .Callback((string userId) => _accountVersions[userId] = _accountVersions.GetValueOrDefault(userId) + 1);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton(_ => _authService.Object);
        services.AddSingleton(_ => _userManager.Object);
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        _cache.Dispose();
    }

    [Fact]
    public async Task Authenticate_WithoutTheHeader_LeavesTheRequestToTheOtherSchemes()
    {
        var result = await AuthenticateAsync(CreateContext());

        Assert.True(result.None);
        _authService.Verify(a => a.GetUserByApiKeyAsync(It.IsAny<string>()), Times.Never);
    }

    // Every policy lists the scheme, so this runs on nearly every request: it must not build a DbContext for them.
    [Fact]
    public async Task Authenticate_WithoutTheHeader_ResolvesNoAccountServices()
    {
        var context = CreateContext();
        using var empty = new ServiceCollection().BuildServiceProvider();
        var handler = await CreateHandlerAsync(context, requestServices: empty);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
    }

    [Fact]
    public async Task Authenticate_AKeyUsedAgain_IsAnsweredWithoutReadingTheAccountAgain()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);

        Assert.True((await AuthenticateAsync(CreateContext(Key))).Succeeded);
        var again = await AuthenticateAsync(CreateContext(Key));

        Assert.True(again.Succeeded);
        Assert.Equal(user.Id, again.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("User", again.Principal.FindFirstValue(ClaimTypes.Role));
        // The first request reads the key twice: once to find its owner, once more after taking the account version.
        _authService.Verify(a => a.GetUserByApiKeyAsync(Key), Times.Exactly(2));
        _userManager.Verify(u => u.GetRolesAsync(It.IsAny<ApplicationUser>()), Times.Once);
    }

    [Fact]
    public async Task Authenticate_ARememberedKey_IsReadAgainAfterTheCacheDuration()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        Assert.True((await AuthenticateAsync(CreateContext(Key))).Succeeded);

        _clock.Advance(AuthSessionValidator.CacheDuration + TimeSpan.FromSeconds(1));
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(default(ApplicationUser?));

        Assert.Equal("Invalid API key", (await AuthenticateAsync(CreateContext(Key))).Failure?.Message);
    }

    [Fact]
    public async Task Authenticate_ARememberedKey_IsRefusedAsSoonAsItsAccountChanges()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        Assert.True((await AuthenticateAsync(CreateContext(Key))).Succeeded);

        // A revoked or replaced key, a disabled account: each change made in the app invalidates the account.
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(default(ApplicationUser?));
        _sessions.Object.Invalidate(user.Id);

        Assert.Equal("Invalid API key", (await AuthenticateAsync(CreateContext(Key))).Failure?.Message);
    }

    [Fact]
    public async Task Authenticate_ARememberedKey_CarriesTheRolesOfTheAccountAfterAChange()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        Assert.True((await AuthenticateAsync(CreateContext(Key))).Succeeded);

        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(["ReadOnly"]);
        _sessions.Object.Invalidate(user.Id);

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.Equal("ReadOnly", Assert.Single(result.Principal!.FindAll(ClaimTypes.Role)).Value);
    }

    // The key was replaced between the two reads of the first request: what was read first is not trusted.
    [Fact]
    public async Task Authenticate_WhenTheKeyStopsWorkingWhileItIsRead_FailsAndRemembersNothing()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        _authService.SetupSequence(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user).ReturnsAsync(default(ApplicationUser?));
        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(["User"]);

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.Equal("Invalid API key", result.Failure?.Message);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public async Task Authenticate_ARememberedKeyWhoseUseWasJustRecorded_DoesNotWriteAgain()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        Assert.True((await AuthenticateAsync(CreateContext(Key))).Succeeded);
        var factory = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();

        Assert.True((await AuthenticateAsync(CreateContext(Key), factory.Object)).Succeeded);

        factory.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Authenticate_WithABlankKey_FailsWithoutLookingItUp(string value)
    {
        var result = await AuthenticateAsync(CreateContext(value));

        Assert.Equal("Invalid API key", result.Failure!.Message);
        _authService.Verify(a => a.GetUserByApiKeyAsync(It.IsAny<string>()), Times.Never);
    }

    // Two header values could be a proxy appending to a client's value: neither of them is taken on trust.
    [Fact]
    public async Task Authenticate_WithMoreThanOneKey_FailsWithoutLookingEitherUp()
    {
        var result = await AuthenticateAsync(CreateContext(Key, "cmk_other-key"));

        Assert.Equal("Invalid API key", result.Failure!.Message);
        _authService.Verify(a => a.GetUserByApiKeyAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WithAKeyNoActiveAccountHas_Fails()
    {
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(default(ApplicationUser?));

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.False(result.Succeeded);
        Assert.Equal("Invalid API key", result.Failure!.Message);
    }

    [Fact]
    public async Task Authenticate_WithAValidKey_ActsAsItsOwnerWithTheOwnersCurrentRoles()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(["User", "ReadOnly"]);

        // Surrounding spaces are not part of a key; copying one from a file often brings them.
        var result = await AuthenticateAsync(CreateContext($"  {Key} "));

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, result.Ticket!.AuthenticationScheme);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, principal.Identity!.AuthenticationType);
        Assert.Equal(user.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("reader", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("reader@example.com", principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(ApiKeys.AuthMethod, principal.FindFirstValue("auth_method"));
        Assert.Equal(new[] { "ReadOnly", "User" }, principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Authenticate_ForAnAccountWithoutANameOrEmail_StillSignsItInWithEmptyClaims()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        user.UserName = null;
        user.Email = null;
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(Array.Empty<string>());

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Principal!.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(string.Empty, result.Principal.FindFirstValue(ClaimTypes.Email));
        Assert.Empty(result.Principal.FindAll(ClaimTypes.Role));
    }

    [Fact]
    public async Task Authenticate_TheFirstUseOfAKey_RecordsIt()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        var before = DateTime.UtcNow.AddSeconds(-5);

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.True(result.Succeeded);
        var recorded = await ReadLastUsedAsync(user.Id);
        Assert.NotNull(recorded);
        Assert.InRange(recorded!.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Authenticate_AKeyLastUsedLongAgo_RecordsTheNewUse()
    {
        var old = DateTime.UtcNow - ApiKeyAuthenticationHandler.LastUsedResolution - TimeSpan.FromMinutes(5);
        var user = await SeedOwnerAsync(lastUsed: old);
        SignInAs(user);

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.True(result.Succeeded);
        var recorded = await ReadLastUsedAsync(user.Id);
        Assert.NotNull(recorded);
        Assert.True(recorded > old, $"Expected {recorded:O} to be later than {old:O}");
    }

    // A script polling the API must not take SQLite's write lock on every request.
    [Fact]
    public async Task Authenticate_AKeyUsedWithinTheResolution_DoesNotWriteAgain()
    {
        var user = await SeedOwnerAsync(lastUsed: DateTime.UtcNow.AddSeconds(-10));
        SignInAs(user);
        var factory = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();

        var result = await AuthenticateAsync(CreateContext(Key), factory.Object);

        Assert.True(result.Succeeded);
        factory.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // The account was loaded before another request recorded the use: the write's own check stops a second one.
    [Fact]
    public async Task Authenticate_WhenAnotherRequestJustRecordedTheUse_LeavesItsValue()
    {
        var justNow = DateTime.UtcNow.AddSeconds(-1);
        var user = await SeedOwnerAsync(lastUsed: justNow);
        user.ApiKeyLastUsedAt = DateTime.UtcNow.AddHours(-1);
        SignInAs(user);

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.True(result.Succeeded);
        Assert.Equal(justNow, await ReadLastUsedAsync(user.Id));
    }

    [Fact]
    public async Task Authenticate_WhenTheUseCannotBeRecorded_StillSignsIn()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        var broken = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();
        broken
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var result = await AuthenticateAsync(CreateContext(Key), broken.Object);

        Assert.True(result.Succeeded);
        Assert.Null(await ReadLastUsedAsync(user.Id));
    }

    [Fact]
    public async Task Authenticate_WhenTheRecordingFailsUnexpectedly_DoesNotHideTheBug()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        var broken = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();
        broken
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidCastException("a bug"));

        var handler = await CreateHandlerAsync(CreateContext(Key), broken.Object);

        // Only the database's own failures are bookkeeping problems; anything else is a bug that must surface.
        await Assert.ThrowsAsync<InvalidCastException>(() => handler.AuthenticateAsync());
    }

    [Fact]
    public async Task Authenticate_WhenTheDatabaseRefusesTheWrite_StillSignsIn()
    {
        var user = await SeedOwnerAsync(lastUsed: null);
        SignInAs(user);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUserRoles");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUserClaims");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUserLogins");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUserTokens");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUsers");
        }

        var result = await AuthenticateAsync(CreateContext(Key));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Challenge_AnswersUnauthorizedWithoutWritingABody()
    {
        var context = CreateContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var handler = await CreateHandlerAsync(context);

        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task Forbid_AnswersForbidden()
    {
        var context = CreateContext();
        var handler = await CreateHandlerAsync(context);

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    // Several schemes answer one challenge; a response another of them already started must stay as it is.
    [Fact]
    public async Task ChallengeAndForbid_AfterTheResponseStarted_DoNotChangeTheStatus()
    {
        var challenged = StartedContext(StatusCodes.Status200OK);
        await (await CreateHandlerAsync(challenged)).ChallengeAsync(new AuthenticationProperties());

        var forbidden = StartedContext(StatusCodes.Status200OK);
        await (await CreateHandlerAsync(forbidden)).ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status200OK, challenged.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, forbidden.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(params string[] apiKeys)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/files";
        if (apiKeys.Length > 0)
        {
            context.Request.Headers[ApiKeys.HeaderName] = apiKeys;
        }

        return context;
    }

    private static DefaultHttpContext StartedContext(int status)
    {
        var context = CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { StatusCode = status });
        return context;
    }

    private void SignInAs(ApplicationUser user)
    {
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(["User"]);
    }

    private async Task<ApplicationUser> SeedOwnerAsync(DateTime? lastUsed)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = "reader",
            NormalizedUserName = "READER",
            Email = "reader@example.com",
            NormalizedEmail = "READER@EXAMPLE.COM",
            ApiKeyHash = ApiKeys.Hash(Key),
            ApiKeyLastUsedAt = lastUsed,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<DateTime?> ReadLastUsedAsync(string userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.ApiKeyLastUsedAt).SingleAsync();
    }

    private async Task<ApiKeyAuthenticationHandler> CreateHandlerAsync(
        HttpContext context, IDbContextFactory<ComicMaintainerDbContext>? factory = null, IServiceProvider? requestServices = null)
    {
        var handler = new ApiKeyAuthenticationHandler(
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            factory ?? _factory,
            ClientAddressResolver.Direct,
            _sessions.Object,
            _cache);
        context.RequestServices = requestServices ?? _provider;
        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, displayName: null, typeof(ApiKeyAuthenticationHandler)),
            context);
        return handler;
    }

    private async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context, IDbContextFactory<ComicMaintainerDbContext>? factory = null) =>
        await (await CreateHandlerAsync(context, factory)).AuthenticateAsync();

    /// <summary>A response whose headers have gone out, as another scheme's challenge would leave it.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
