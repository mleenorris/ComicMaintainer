using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the registration gate, first-run admin setup, stream token issuance and
/// the anonymous / Authelia branches of the status endpoints in <see cref="AuthController"/>.
/// </summary>
public class AuthControllerCoverageTests
{
    private const string RegistrationDisabledError = "Registration is disabled. Ask an administrator to create your account.";

    private readonly Mock<IAuthService> _authService = new();
    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly AppSettings _appSettings = new() { AllowRegistration = false };
    private readonly AutheliaSettings _autheliaSettings = new() { Enabled = false };

    public AuthControllerCoverageTests()
    {
        // Deny every policy unless a test grants it explicitly.
        _authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
    }

    private AuthController CreateController(ClaimsPrincipal user)
    {
        return new AuthController(
            _authService.Object,
            NullLogger<AuthController>.Instance,
            Options.Create(_autheliaSettings),
            _authorization.Object,
            new TestOptionsMonitor<AppSettings>(_appSettings),
            ClientAddressResolver.Direct)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    private void GrantPolicy(string policy)
    {
        _authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), policy))
            .ReturnsAsync(AuthorizationResult.Success());
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn(string? userId = "user-1", string? name = "reader", params Claim[] extra)
    {
        var claims = new List<Claim>(extra);
        if (userId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        if (name is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static T? Prop<T>(object? value, string name) =>
        (T?)value!.GetType().GetProperty(name)!.GetValue(value);

    private static readonly RegisterRequest NewAccount = new("newuser", "Password123!", "new@example.com", null);

    // ----- Register -------------------------------------------------------

    [Fact]
    public async Task Register_WhenDisabledAndAnonymous_ReturnsUnauthorizedWithoutCreatingAccount()
    {
        var controller = CreateController(Anonymous());

        var result = await controller.Register(NewAccount);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(RegistrationDisabledError, Prop<string>(unauthorized.Value, "error"));
        _authService.VerifyNoOtherCalls();
        _authorization.Verify(a => a.AuthorizeAsync(
            It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), AuthorizationPolicies.CanAdminister), Times.Once);
    }

    [Fact]
    public async Task Register_WhenDisabledAndPrincipalHasNoIdentity_ReturnsUnauthorized()
    {
        var controller = CreateController(new ClaimsPrincipal());

        var result = await controller.Register(NewAccount);

        Assert.IsType<UnauthorizedObjectResult>(result);
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Register_WhenDisabledAndSignedInNonAdmin_ReturnsForbidden()
    {
        var controller = CreateController(SignedIn());

        var result = await controller.Register(NewAccount);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(RegistrationDisabledError, Prop<string>(forbidden.Value, "error"));
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Register_WhenDisabled_AdministratorCanStillCreateAccounts()
    {
        GrantPolicy(AuthorizationPolicies.CanAdminister);
        _authService.Setup(s => s.RegisterAsync("newuser", "Password123!", "new@example.com", null)).ReturnsAsync((true, null));
        var controller = CreateController(SignedIn(name: "admin"));

        var result = await controller.Register(NewAccount);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("User registered successfully", Prop<string>(ok.Value, "message"));
        _authService.Verify(s => s.RegisterAsync("newuser", "Password123!", "new@example.com", null), Times.Once);
    }

    [Fact]
    public async Task Register_WhenEnabled_AnonymousVisitorCanRegister()
    {
        _appSettings.AllowRegistration = true;
        _authService.Setup(s => s.RegisterAsync("newuser", "Password123!", "new@example.com", null)).ReturnsAsync((true, null));
        var controller = CreateController(Anonymous());

        var result = await controller.Register(NewAccount);

        Assert.IsType<OkObjectResult>(result);
        _authService.Verify(s => s.RegisterAsync("newuser", "Password123!", "new@example.com", null), Times.Once);
    }

    [Fact]
    public async Task Register_WhenEnabledButServiceRejects_ReturnsBadRequestWithServiceError()
    {
        _appSettings.AllowRegistration = true;
        _authService.Setup(s => s.RegisterAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((false, "Username already taken"));
        var controller = CreateController(Anonymous());

        var result = await controller.Register(NewAccount);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Username already taken", Prop<string>(badRequest.Value, "error"));
    }

    // ----- CompleteFirstRunSetup -----------------------------------------------------

    private void SetupRequired(bool required) =>
        _authService.Setup(s => s.IsSetupRequiredAsync()).ReturnsAsync(required);

    private void VerifyNoAdminCreated() =>
        _authService.Verify(
            s => s.SetupAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public async Task SetupAdmin_WithInvalidUsername_ReturnsBadRequest(string? username)
    {
        SetupRequired(true);
        var controller = CreateController(Anonymous());

        var result = await controller.CompleteFirstRunSetup(new SetupRequest(username!, "LongEnough1", null, "code"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Username must be at least 3 characters", Prop<string>(badRequest.Value, "error"));
        VerifyNoAdminCreated();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("          ")]
    [InlineData("1234567")]
    public async Task SetupAdmin_WithInvalidPassword_ReturnsBadRequest(string? password)
    {
        SetupRequired(true);
        var controller = CreateController(Anonymous());

        var result = await controller.CompleteFirstRunSetup(new SetupRequest("admin", password!, null, "code"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Password must be at least 8 characters", Prop<string>(badRequest.Value, "error"));
        VerifyNoAdminCreated();
    }

    [Fact]
    public async Task SetupAdmin_WithValidInput_CreatesFirstAdministrator()
    {
        SetupRequired(true);
        _authService.Setup(s => s.SetupAdminAsync("admin", "12345678", "admin@example.com", "code", "unknown"))
            .ReturnsAsync((SetupOutcome.Created, null));
        var controller = CreateController(Anonymous());

        var result = await controller.CompleteFirstRunSetup(new SetupRequest("admin", "12345678", "admin@example.com", "code"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Admin user created successfully", Prop<string>(ok.Value, "message"));
        _authService.Verify(s => s.SetupAdminAsync("admin", "12345678", "admin@example.com", "code", "unknown"), Times.Once);
    }

    [Fact]
    public async Task SetupAdmin_WhenAConcurrentRequestCreatesTheAdminFirst_Returns403WithServiceError()
    {
        // Setup was still available when checked; the service then finds an account.
        SetupRequired(true);
        _authService.Setup(s => s.SetupAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((SetupOutcome.NotAvailable, AuthService.SetupNotAvailableError));
        var controller = CreateController(Anonymous());

        var result = await controller.CompleteFirstRunSetup(new SetupRequest("admin", "12345678", null, "code"));

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(AuthService.SetupNotAvailableError, Prop<string>(forbidden.Value, "error"));
    }

    [Fact]
    public async Task SetupAdmin_AfterSetup_RefusesAnAdministratorToo()
    {
        SetupRequired(false);
        GrantPolicy(AuthorizationPolicies.CanAdminister);
        var controller = CreateController(SignedIn("admin-1", "admin"));

        var result = await controller.CompleteFirstRunSetup(new SetupRequest("admin2", "12345678", null, "code"));

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        VerifyNoAdminCreated();
    }

    // ----- GetAuthStatus --------------------------------------------------

    [Fact]
    public void GetAuthStatus_WithAutheliaEnabledButNoIdentity_RequiresLogin()
    {
        _autheliaSettings.Enabled = true;
        var controller = CreateController(new ClaimsPrincipal());

        var ok = Assert.IsType<OkObjectResult>(controller.GetAuthStatus());

        Assert.True(Prop<bool>(ok.Value, "autheliaEnabled"));
        Assert.False(Prop<bool>(ok.Value, "isAuthenticated"));
        Assert.Null(Prop<string>(ok.Value, "username"));
        Assert.Equal("jwt", Prop<string>(ok.Value, "authMethod"));
        Assert.True(Prop<bool>(ok.Value, "requiresLogin"));
    }

    [Fact]
    public void GetAuthStatus_ReportsAuthMethodClaim()
    {
        _autheliaSettings.Enabled = true;
        var controller = CreateController(SignedIn(name: "jane", extra: new Claim("auth_method", "authelia")));

        var ok = Assert.IsType<OkObjectResult>(controller.GetAuthStatus());

        Assert.Equal("authelia", Prop<string>(ok.Value, "authMethod"));
        Assert.Equal("jane", Prop<string>(ok.Value, "username"));
        Assert.False(Prop<bool>(ok.Value, "requiresLogin"));
    }

    [Fact]
    public void GetAuthStatus_WithAutheliaDisabled_AlwaysRequiresAppLogin()
    {
        _autheliaSettings.Enabled = false;
        var controller = CreateController(SignedIn(name: "jane"));

        var ok = Assert.IsType<OkObjectResult>(controller.GetAuthStatus());

        Assert.False(Prop<bool>(ok.Value, "autheliaEnabled"));
        Assert.True(Prop<bool>(ok.Value, "isAuthenticated"));
        Assert.Equal("jwt", Prop<string>(ok.Value, "authMethod"));
        Assert.True(Prop<bool>(ok.Value, "requiresLogin"));
    }

    // ----- GetStreamToken -------------------------------------------------

    public static TheoryData<string> IncompleteUsers => new() { "no-identity", "no-user-id" };

    [Theory]
    [MemberData(nameof(IncompleteUsers))]
    public async Task GetStreamToken_WithoutUserId_ReturnsUnauthorized(string kind)
    {
        var user = kind switch
        {
            "no-identity" => new ClaimsPrincipal(),
            _ => SignedIn(userId: null, name: "reader"),
        };
        var controller = CreateController(user);

        var result = await controller.GetStreamToken();

        Assert.IsType<UnauthorizedResult>(result);
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStreamToken_ForSignedInUser_ReturnsAStreamTokenAndItsLifetime()
    {
        _authService.Setup(s => s.GenerateStreamTokenAsync("user-1")).ReturnsAsync((true, "stream-jwt", null));
        var controller = CreateController(SignedIn(extra: new Claim("auth_method", "authelia")));

        var result = await controller.GetStreamToken();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("stream-jwt", Prop<string>(ok.Value, "token"));
        Assert.Equal(60, Prop<int>(ok.Value, "expiresIn"));
        // Never a session token: the stream token is the only token that may travel in a URL.
        _authService.Verify(s => s.GenerateStreamTokenAsync("user-1"), Times.Once);
        _authService.Verify(s => s.GenerateTokenForUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetStreamToken_ForADisabledOrDeletedAccount_ReturnsUnauthorized()
    {
        _authService.Setup(s => s.GenerateStreamTokenAsync("user-1"))
            .ReturnsAsync((false, string.Empty, "User not found"));
        var controller = CreateController(SignedIn());

        var result = await controller.GetStreamToken();

        Assert.IsType<UnauthorizedResult>(result);
    }

    // ----- API key status and revocation ----------------------------------

    [Theory]
    [MemberData(nameof(IncompleteUsers))]
    public async Task GetApiKeyStatus_WithoutUserId_ReturnsUnauthorized(string kind)
    {
        var controller = CreateController(IncompleteUser(kind));

        Assert.IsType<UnauthorizedResult>(await controller.GetApiKeyStatus());
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetApiKeyStatus_ReportsTheAccountsKeyAndTheHeaderItIsSentIn()
    {
        var createdAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var usedAt = createdAt.AddDays(3);
        _authService.Setup(s => s.GetApiKeyStatusAsync("user-1")).ReturnsAsync(new ApiKeyStatus(true, createdAt, usedAt));

        var ok = Assert.IsType<OkObjectResult>(await CreateController(SignedIn()).GetApiKeyStatus());

        Assert.True(Prop<bool>(ok.Value, "hasApiKey"));
        Assert.Equal(createdAt, Prop<DateTime?>(ok.Value, "createdAt"));
        Assert.Equal(usedAt, Prop<DateTime?>(ok.Value, "lastUsedAt"));
        Assert.Equal(ApiKeys.HeaderName, Prop<string>(ok.Value, "header"));
    }

    [Theory]
    [MemberData(nameof(IncompleteUsers))]
    public async Task RevokeApiKey_WithoutUserId_ReturnsUnauthorized(string kind)
    {
        var controller = CreateController(IncompleteUser(kind));

        Assert.IsType<UnauthorizedResult>(await controller.RevokeApiKey());
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokeApiKey_WithAKey_ReturnsNoContent()
    {
        _authService.Setup(s => s.RevokeApiKeyAsync("user-1")).ReturnsAsync(true);

        Assert.IsType<NoContentResult>(await CreateController(SignedIn()).RevokeApiKey());
    }

    [Fact]
    public async Task RevokeApiKey_WithoutAKey_ReturnsNotFoundWithTheReason()
    {
        _authService.Setup(s => s.RevokeApiKeyAsync("user-1")).ReturnsAsync(false);

        var notFound = Assert.IsType<NotFoundObjectResult>(await CreateController(SignedIn()).RevokeApiKey());

        Assert.Equal("This account has no API key.", Prop<string>(notFound.Value, "error"));
    }

    // ----- Logout ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(IncompleteUsers))]
    public async Task Logout_WithoutUserId_ReturnsUnauthorized(string kind)
    {
        var controller = CreateController(IncompleteUser(kind));

        Assert.IsType<UnauthorizedResult>(await controller.Logout());
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Logout_RefusesTheSessionTokenThisRequestWasMadeWithUntilItExpires()
    {
        var expiresAt = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var controller = CreateController(SignedIn(extra:
        [
            new Claim(JwtRegisteredClaimNames.Jti, "token-1"),
            new Claim(JwtRegisteredClaimNames.Exp, expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        ]));

        Assert.IsType<NoContentResult>(await controller.Logout());

        _authService.Verify(s => s.LogoutAsync("user-1", "token-1", expiresAt.UtcDateTime, false), Times.Once);
    }

    [Fact]
    public async Task Logout_ForAllSessions_AsksForEverySessionToBeRefused()
    {
        var controller = CreateController(SignedIn(extra: new Claim(JwtRegisteredClaimNames.Jti, "token-1")));

        Assert.IsType<NoContentResult>(await controller.Logout(new LogoutRequest(AllSessions: true)));

        _authService.Verify(s => s.LogoutAsync("user-1", "token-1", It.IsAny<DateTime>(), true), Times.Once);
    }

    // A session without a usable expiry claim is still revoked, for at least as long as a session token can live.
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task Logout_WithoutAReadableExpiry_RefusesTheTokenForADay(string? expClaim)
    {
        Claim[] claims = expClaim is null ? [] : [new Claim(JwtRegisteredClaimNames.Exp, expClaim)];
        DateTime recorded = default;
        _authService
            .Setup(s => s.LogoutAsync("user-1", null, It.IsAny<DateTime>(), false))
            .Callback((string _, string? _, DateTime expires, bool _) => recorded = expires)
            .Returns(Task.CompletedTask);
        var before = DateTime.UtcNow;

        Assert.IsType<NoContentResult>(await CreateController(SignedIn(extra: claims)).Logout());

        Assert.InRange(recorded, before.AddDays(1).AddSeconds(-5), DateTime.UtcNow.AddDays(1).AddSeconds(5));
    }

    private static ClaimsPrincipal IncompleteUser(string kind) => kind switch
    {
        "no-identity" => new ClaimsPrincipal(),
        _ => SignedIn(userId: null, name: "reader"),
    };

    // ----- GetCurrentUser -------------------------------------------------

    [Fact]
    public async Task GetCurrentUser_ReflectsEachPolicyDecisionSeparately()
    {
        GrantPolicy(AuthorizationPolicies.CanModifyLibrary);
        var controller = CreateController(SignedIn(extra: new Claim(ClaimTypes.Role, "User")));

        var ok = Assert.IsType<OkObjectResult>(await controller.GetCurrentUser());

        Assert.True(Prop<bool>(ok.Value, "canModifyLibrary"));
        Assert.False(Prop<bool>(ok.Value, "canAdminister"));
        Assert.Equal(new[] { "User" }, Prop<List<string>>(ok.Value, "roles"));
        Assert.Equal("jwt", Prop<string>(ok.Value, "authMethod"));
    }

    [Fact]
    public async Task GetCurrentUser_WithoutIdentity_ReturnsNullUserAndNoCapabilities()
    {
        var controller = CreateController(new ClaimsPrincipal());

        var ok = Assert.IsType<OkObjectResult>(await controller.GetCurrentUser());

        Assert.Null(Prop<string>(ok.Value, "username"));
        Assert.Null(Prop<string>(ok.Value, "userId"));
        Assert.Empty(Prop<List<string>>(ok.Value, "roles")!);
        Assert.False(Prop<bool>(ok.Value, "canModifyLibrary"));
        Assert.False(Prop<bool>(ok.Value, "canAdminister"));
    }
}
