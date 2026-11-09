using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// HTTP Basic on the OPDS routes, on a real SQLite users table: which credentials it accepts and how it refuses the
/// rest, the sign-in attempt budget they draw on, what a credential that passed is remembered for, and the answer to a
/// request that has to be refused.
/// </summary>
public sealed class OpdsBasicAuthenticationHandlerTests : IAsyncLifetime
{
    private const string Username = "reader";
    private const string Password = "correct horse battery";
    private const string ClientAddress = "192.0.2.10";

    // A key in the exact form the application generates.
    private static readonly string Key = ApiKeys.Generate();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Mock<IAuthService> _authService = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManager = new(
        Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
    private readonly AuthTestHost.CapturingLoggerProvider _logs = new();
    private readonly ManualClock _clock = new();
    private readonly AuthenticationAttemptLimiter _attempts = new();
    private readonly MemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private bool _autheliaEnabled;

    public OpdsBasicAuthenticationHandlerTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));

        // Names compare the way Identity normalizes them; a loose mock would call every two names equal.
        _userManager.Setup(u => u.NormalizeName(It.IsAny<string>())).Returns<string?>(name => name?.ToUpperInvariant()!);
        _userManager.Setup(u => u.GetRolesAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(["User"]);
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
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
        _attempts.Dispose();
        _loggerFactory.Dispose();
    }

    [Fact]
    public async Task Authenticate_WithoutTheHeader_LeavesTheRequestUnauthenticatedAndTakesNoAttempt()
    {
        var result = await AuthenticateAsync(CreateContext());

        Assert.True(result.None);
        VerifyNoPasswordCheck();
        Assert.Equal(AuthenticationRateLimit.PermitLimit, await CountRemainingAttemptsAsync());
    }

    public static TheoryData<string> MalformedHeaders()
    {
        var invalidUtf8 = new byte[] { 0xC3, 0x28, 0x3A, 0x41 };
        return new TheoryData<string>
        {
            "Bearer some-token",
            "Basic",
            "Basic !!!not-base64!!!",
            "Basic " + Convert.ToBase64String(invalidUtf8),
            Basic("no-separator-anywhere"),
            Basic(":the-username-is-empty"),
            Basic("the-password-is-empty:"),
            Basic(new string('u', 257) + ":password"),
            Basic("user:" + new string('s', 1025)),
        };
    }

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public async Task Authenticate_WithMalformedCredentials_FailsWithoutCheckingThemOrTakingAnAttempt(string header)
    {
        var result = await AuthenticateAsync(CreateContext(header));

        Assert.Equal("Malformed Basic credentials", result.Failure!.Message);
        VerifyNoPasswordCheck();
        _authService.Verify(a => a.GetUserByApiKeyAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal(AuthenticationRateLimit.PermitLimit, await CountRemainingAttemptsAsync());
    }

    [Fact]
    public async Task Authenticate_AtTheLongestAcceptedCredentials_ChecksThem()
    {
        var username = new string('u', 256);
        var secret = new string('s', 1024);

        await AuthenticateAsync(CreateContext(Basic($"{username}:{secret}")));

        _authService.Verify(a => a.VerifyPasswordAsync(username, secret, ClientAddress), Times.Once);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("basic")]
    [InlineData("BASIC")]
    public async Task Authenticate_ReadsTheSchemeInAnyCaseAndIgnoresPadding(string scheme)
    {
        AcceptPassword(Account());
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

        var result = await AuthenticateAsync(CreateContext($"{scheme}   {encoded}  "));

        Assert.True(result.Succeeded);
    }

    // RFC 7617: the first colon separates the two, so a password may hold more of them.
    [Fact]
    public async Task Authenticate_WithColonsInThePassword_SplitsOnTheFirstOnly()
    {
        await AuthenticateAsync(CreateContext(Basic($"{Username}:pa:ss:word")));

        _authService.Verify(a => a.VerifyPasswordAsync(Username, "pa:ss:word", ClientAddress), Times.Once);
    }

    [Fact]
    public async Task Authenticate_WithTheRightPassword_ActsAsTheAccountWithItsCurrentRoles()
    {
        var user = Account();
        AcceptPassword(user);
        _userManager.Setup(u => u.GetRolesAsync(user)).ReturnsAsync(["User", "ReadOnly"]);

        var result = await AuthenticateAsync(CreateContext(Credentials()));

        Assert.True(result.Succeeded);
        Assert.Equal(OpdsBasicAuthenticationHandler.SchemeName, result.Ticket!.AuthenticationScheme);
        var principal = result.Principal!;
        Assert.Equal(OpdsBasicAuthenticationHandler.SchemeName, principal.Identity!.AuthenticationType);
        Assert.Equal(user.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(Username, principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("reader@example.com", principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(OpdsBasicAuthenticationHandler.PasswordAuthMethod, principal.FindFirstValue("auth_method"));
        Assert.Equal(new[] { "ReadOnly", "User" }, principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal));
        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, ClientAddress), Times.Once);
    }

    [Fact]
    public async Task Authenticate_ForAnAccountWithoutANameOrEmail_StillSignsItInWithEmptyClaims()
    {
        var user = Account();
        user.UserName = null;
        user.Email = null;
        AcceptPassword(user);

        var result = await AuthenticateAsync(CreateContext(Credentials()));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Principal!.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(string.Empty, result.Principal.FindFirstValue(ClaimTypes.Email));
    }

    [Fact]
    public async Task Authenticate_WithAPassword_RecordsNoKeyUse()
    {
        AcceptPassword(Account());
        var factory = new Mock<IDbContextFactory<ComicMaintainerDbContext>>();

        var result = await AuthenticateAsync(CreateContext(Credentials()), factory.Object);

        Assert.True(result.Succeeded);
        factory.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WithTheWrongPassword_Fails()
    {
        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:wrong")));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
    }

    [Fact]
    public async Task Authenticate_WithTheOwnersKey_ActsAsTheOwnerAndRecordsTheUse()
    {
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        var before = DateTime.UtcNow.AddSeconds(-5);

        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:{Key}")));

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(ApiKeys.AuthMethod, result.Principal.FindFirstValue("auth_method"));
        VerifyNoPasswordCheck();
        var recorded = await ReadLastUsedAsync(user.Id);
        Assert.NotNull(recorded);
        Assert.InRange(recorded!.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Authenticate_WithTheKeyUnderTheUsernameInAnotherCase_StillMatchesTheOwner()
    {
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);

        var result = await AuthenticateAsync(CreateContext(Basic($"READER:{Key}")));

        Assert.True(result.Succeeded);
    }

    // A device that still holds a replaced key must not lock its owner out with it.
    [Fact]
    public async Task Authenticate_WithAGeneratedKeyThatIsNoLongerValid_FailsWithoutCountingAgainstTheAccount()
    {
        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:{Key}")));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
        VerifyNoPasswordCheck();
        var refusal = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Refused an API key for reader from 192.0.2.10", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticate_WithAnotherAccountsKey_IsRefusedAsAKey()
    {
        var owner = Account();
        owner.UserName = "someone-else";
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(owner);

        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:{Key}")));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
        VerifyNoPasswordCheck();
    }

    // Only the generated form is certain to be a key; a shorter value after the prefix can be someone's password.
    [Fact]
    public async Task Authenticate_WithAKeyPrefixedPasswordThatIsNoKey_TriesItAsAPassword()
    {
        var password = ApiKeys.Prefix + "my-real-password";

        await AuthenticateAsync(CreateContext(Basic($"{Username}:{password}")));

        _authService.Verify(a => a.GetUserByApiKeyAsync(password), Times.Once);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, password, ClientAddress), Times.Once);
    }

    [Fact]
    public async Task Authenticate_WithAValueLongerThanAnyKey_DoesNotLookItUp()
    {
        var password = ApiKeys.Prefix + new string('x', ApiKeys.MaxLength);

        await AuthenticateAsync(CreateContext(Basic($"{Username}:{password}")));

        _authService.Verify(a => a.GetUserByApiKeyAsync(It.IsAny<string>()), Times.Never);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, password, ClientAddress), Times.Once);
    }

    [Fact]
    public async Task Authenticate_WithAutheliaEnabled_RefusesAPasswordWithoutCheckingIt()
    {
        _autheliaEnabled = true;
        AcceptPassword(Account());

        var result = await AuthenticateAsync(CreateContext(Credentials()));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
        VerifyNoPasswordCheck();
        var refusal = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("only an API key is accepted", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticate_WithAutheliaEnabled_StillAcceptsAnApiKey()
    {
        _autheliaEnabled = true;
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);

        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:{Key}")));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Authenticate_NeverLogsOrRemembersTheSecret()
    {
        AcceptPassword(Account());

        await AuthenticateAsync(CreateContext(Credentials()));
        await AuthenticateAsync(CreateContext(Basic($"{Username}:{Key}")));

        var remembered = Assert.Single(_cache.Keys);
        Assert.DoesNotContain(Password, remembered.ToString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(Password)), remembered.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.All(_logs.Entries, entry =>
        {
            Assert.DoesNotContain(Password, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Authenticate_PastTheAttemptBudget_RefusesWithoutCheckingAndNamesTheWait()
    {
        await SpendAttemptsAsync(AuthenticationRateLimit.PermitLimit);
        AcceptPassword(Account());
        var context = CreateContext(Credentials());
        context.Response.Body = new MemoryStream();
        var handler = await CreateHandlerAsync(context);

        var result = await handler.AuthenticateAsync();
        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal("Too many attempts", result.Failure!.Message);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Never);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        var seconds = int.Parse(context.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, (int)Math.Ceiling(AuthenticationRateLimit.Window.TotalSeconds));
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"), "a client must wait, not ask for a password");
        var problem = ReadBody(context);
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal(RateLimitRejection.DescribeWait(seconds), problem.GetProperty("error").GetString());
        Assert.DoesNotContain(_logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Authenticate_AfterOneClientSpendsItsBudget_StillChecksAnotherClient()
    {
        await SpendAttemptsAsync(AuthenticationRateLimit.PermitLimit);
        AcceptPassword(Account());

        var result = await AuthenticateAsync(CreateContext(Credentials(), clientAddress: "192.0.2.11"));

        Assert.True(result.Succeeded);
    }

    // A reader app repeats the credentials on every page and must not run out of sign-in attempts.
    [Fact]
    public async Task Authenticate_ARememberedPassword_IsNotVerifiedAgainAndTakesNoAttempt()
    {
        var user = Account();
        AcceptPassword(user);
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);

        for (var i = 0; i < AuthenticationRateLimit.PermitLimit + 5; i++)
        {
            Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);
        }

        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Authenticate_WithAPasswordOtherThanTheRememberedOne_ChecksIt()
    {
        AcceptPassword(Account());
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);

        var result = await AuthenticateAsync(CreateContext(Basic($"{Username}:another guess")));

        Assert.False(result.Succeeded);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, "another guess", ClientAddress), Times.Once);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("password changed")]
    [InlineData("locked out")]
    [InlineData("deleted")]
    public async Task Authenticate_ARememberedPasswordOfAnAccountThatHasChanged_IsNotTrusted(string change)
    {
        var user = Account();
        AcceptPassword(user);
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);
        Assert.Equal(1, _cache.Count);

        switch (change)
        {
            case "disabled":
                user.IsActive = false;
                break;
            case "password changed":
                user.SecurityStamp = "a-new-stamp";
                break;
            case "locked out":
                _userManager.Setup(u => u.IsLockedOutAsync(user)).ReturnsAsync(true);
                break;
            case "deleted":
                _userManager.Setup(u => u.FindByNameAsync(Username)).ReturnsAsync((ApplicationUser?)null);
                break;
        }

        // Whatever the change was, the sign-in rules now answer: here they refuse the password.
        RefusePassword();

        var result = await AuthenticateAsync(CreateContext(Credentials()));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Exactly(2));
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public async Task Authenticate_ARememberedKey_IsLookedUpEachTimeAndStopsWorkingWhenRevoked()
    {
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        // The account itself is fine, so a key wrongly remembered as a password would pass the password recheck.
        _userManager.Setup(u => u.FindByNameAsync(Username)).ReturnsAsync(user);
        var credentials = Basic($"{Username}:{Key}");
        Assert.True((await AuthenticateAsync(CreateContext(credentials))).Succeeded);

        var remembered = await AuthenticateAsync(CreateContext(credentials));

        Assert.True(remembered.Succeeded);
        _authService.Verify(a => a.GetUserByApiKeyAsync(Key), Times.Exactly(2));
        Assert.Equal(ApiKeys.AuthMethod, remembered.Principal!.FindFirstValue("auth_method"));

        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync((ApplicationUser?)null);

        var result = await AuthenticateAsync(CreateContext(credentials));

        Assert.Equal("Invalid credentials", result.Failure!.Message);
        VerifyNoPasswordCheck();
        Assert.Equal(0, _cache.Count);
    }

    // The last-used time shown for a key has to keep moving while the key is being remembered.
    [Fact]
    public async Task Authenticate_ARememberedKey_StillRecordsTheUse()
    {
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        _userManager.Setup(u => u.FindByNameAsync(Username)).ReturnsAsync(user);
        var credentials = Basic($"{Username}:{Key}");
        Assert.True((await AuthenticateAsync(CreateContext(credentials))).Succeeded);
        await BackdateLastUseAsync(user.Id, TimeSpan.FromHours(1));
        var before = DateTime.UtcNow.AddSeconds(-5);

        Assert.True((await AuthenticateAsync(CreateContext(credentials))).Succeeded);

        var recorded = await ReadLastUsedAsync(user.Id);
        Assert.NotNull(recorded);
        Assert.InRange(recorded!.Value, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Authenticate_ARememberedKeyAfterItsOwnerIsRenamed_NoLongerMatches()
    {
        var user = await SeedOwnerAsync();
        _authService.Setup(a => a.GetUserByApiKeyAsync(Key)).ReturnsAsync(user);
        var credentials = Basic($"{Username}:{Key}");
        Assert.True((await AuthenticateAsync(CreateContext(credentials))).Succeeded);

        user.UserName = "renamed";

        var result = await AuthenticateAsync(CreateContext(credentials));

        Assert.False(result.Succeeded);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public async Task Authenticate_AfterTheMemoryLapses_VerifiesThePasswordAgain()
    {
        AcceptPassword(Account());
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);

        _clock.Advance(OpdsBasicAuthenticationHandler.RememberedFor + TimeSpan.FromSeconds(1));

        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Exactly(2));
    }

    // Steady use keeps a credential remembered only up to the limit: a stolen password does not live on in memory.
    [Fact]
    public async Task Authenticate_InSteadyUse_VerifiesThePasswordAgainAtTheLongestMemory()
    {
        AcceptPassword(Account());
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);

        var step = OpdsBasicAuthenticationHandler.RememberedFor - TimeSpan.FromSeconds(30);
        var elapsed = TimeSpan.Zero;
        while (elapsed + step < OpdsBasicAuthenticationHandler.RememberedAtMost)
        {
            _clock.Advance(step);
            elapsed += step;
            Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);
        }

        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Once);

        _clock.Advance(step);
        Assert.True((await AuthenticateAsync(CreateContext(Credentials()))).Succeeded);
        _authService.Verify(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Challenge_AsksForCredentialsWithAProblemDocument()
    {
        var context = CreateContext();
        context.Response.Body = new MemoryStream();
        var handler = await CreateHandlerAsync(context);

        await handler.ChallengeAsync(new AuthenticationProperties());

        const string expected = "Authentication required. Use your username with your password or API key.";
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(OpdsBasicAuthenticationHandler.Challenge, context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var problem = ReadBody(context);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal(expected, problem.GetProperty("title").GetString());
        Assert.Equal(expected, problem.GetProperty("error").GetString());
        Assert.Equal("/opds/v1.2/catalog", problem.GetProperty("instance").GetString());
    }

    // Authentication can run after the response has begun; the started response must be left alone.
    [Fact]
    public async Task Challenge_AfterTheResponseStarted_ChangesNothing()
    {
        var context = CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { StatusCode = StatusCodes.Status200OK });
        var handler = await CreateHandlerAsync(context);

        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    private static string Basic(string value) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Credentials() => Basic($"{Username}:{Password}");

    private static DefaultHttpContext CreateContext(string? authorization = null, string clientAddress = ClientAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(clientAddress);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/opds/v1.2/catalog";
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static ApplicationUser Account() => new()
    {
        Id = "account-1",
        UserName = Username,
        NormalizedUserName = Username.ToUpperInvariant(),
        Email = "reader@example.com",
        NormalizedEmail = "READER@EXAMPLE.COM",
        SecurityStamp = "the-stamp",
    };

    private async Task<ApplicationUser> SeedOwnerAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = Account();
        user.ApiKeyHash = ApiKeys.Hash(Key);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<DateTime?> ReadLastUsedAsync(string userId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.ApiKeyLastUsedAt).SingleAsync();
    }

    private async Task BackdateLastUseAsync(string userId, TimeSpan by)
    {
        var earlier = DateTime.UtcNow - by;
        await using var db = await _factory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(u => u.ApiKeyLastUsedAt, earlier));
    }

    private void AcceptPassword(ApplicationUser user)
    {
        _authService.Setup(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>())).ReturnsAsync(user);
        _userManager.Setup(u => u.FindByNameAsync(Username)).ReturnsAsync(user);
        _userManager.Setup(u => u.IsLockedOutAsync(user)).ReturnsAsync(false);
    }

    private void RefusePassword() =>
        _authService.Setup(a => a.VerifyPasswordAsync(Username, Password, It.IsAny<string?>())).ReturnsAsync((ApplicationUser?)null);

    private void VerifyNoPasswordCheck() =>
        _authService.Verify(a => a.VerifyPasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);

    /// <summary>Makes <paramref name="count"/> refused checks of the same client, each taking one attempt.</summary>
    private async Task SpendAttemptsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var refused = await AuthenticateAsync(CreateContext(Basic($"{Username}:wrong guess {i}")));
            Assert.Equal("Invalid credentials", refused.Failure!.Message);
        }
    }

    /// <summary>
    /// How many attempts the client can still make, found by drawing its real budget down until it refuses. That spends
    /// the budget, so this can only be the last step of a test.
    /// </summary>
    private Task<int> CountRemainingAttemptsAsync()
    {
        var address = System.Net.IPAddress.Parse(ClientAddress);
        var remaining = 0;
        while (true)
        {
            using var lease = _attempts.AttemptAcquire(address);
            if (!lease.IsAcquired)
            {
                return Task.FromResult(remaining);
            }

            remaining++;
        }
    }

    private static JsonElement ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.Clone();
    }

    private async Task<OpdsBasicAuthenticationHandler> CreateHandlerAsync(
        HttpContext context, IDbContextFactory<ComicMaintainerDbContext>? factory = null)
    {
        var handler = new OpdsBasicAuthenticationHandler(
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            _loggerFactory,
            UrlEncoder.Default,
            _authService.Object,
            _userManager.Object,
            factory ?? _factory,
            ClientAddressResolver.Direct,
            _attempts,
            _cache,
            Options.Create(new AutheliaSettings { Enabled = _autheliaEnabled }));
        await handler.InitializeAsync(
            new AuthenticationScheme(OpdsBasicAuthenticationHandler.SchemeName, displayName: null, typeof(OpdsBasicAuthenticationHandler)),
            context);
        return handler;
    }

    private async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context, IDbContextFactory<ComicMaintainerDbContext>? factory = null) =>
        await (await CreateHandlerAsync(context, factory)).AuthenticateAsync();

    /// <summary>A response whose headers have gone out.</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>A cache clock the test moves by hand, so expiry needs no waiting.</summary>
    private sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
