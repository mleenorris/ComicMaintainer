using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Covers the registration gate, first-run admin setup, SSE token issuance and
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
            new TestOptionsMonitor<AppSettings>(_appSettings))
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

    // ----- SetupAdmin -----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public async Task SetupAdmin_WithInvalidUsername_ReturnsBadRequest(string? username)
    {
        var controller = CreateController(Anonymous());

        var result = await controller.SetupAdmin(new SetupRequest(username!, "LongEnough1", null));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Username must be at least 3 characters", Prop<string>(badRequest.Value, "error"));
        _authService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("          ")]
    [InlineData("1234567")]
    public async Task SetupAdmin_WithInvalidPassword_ReturnsBadRequest(string? password)
    {
        var controller = CreateController(Anonymous());

        var result = await controller.SetupAdmin(new SetupRequest("admin", password!, null));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Password must be at least 8 characters", Prop<string>(badRequest.Value, "error"));
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetupAdmin_WithValidInput_CreatesFirstAdministrator()
    {
        _authService.Setup(s => s.SetupAdminAsync("admin", "12345678", "admin@example.com")).ReturnsAsync((true, null));
        var controller = CreateController(Anonymous());

        var result = await controller.SetupAdmin(new SetupRequest("admin", "12345678", "admin@example.com"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Admin user created successfully", Prop<string>(ok.Value, "message"));
        _authService.Verify(s => s.SetupAdminAsync("admin", "12345678", "admin@example.com"), Times.Once);
    }

    [Fact]
    public async Task SetupAdmin_WhenSetupAlreadyCompleted_ReturnsBadRequestWithServiceError()
    {
        _authService.Setup(s => s.SetupAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((false, "Setup has already been completed"));
        var controller = CreateController(Anonymous());

        var result = await controller.SetupAdmin(new SetupRequest("admin", "12345678", null));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Setup has already been completed", Prop<string>(badRequest.Value, "error"));
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

    // ----- GetSseToken ----------------------------------------------------

    public static TheoryData<string> IncompleteUsers => new() { "no-identity", "no-user-id", "no-name" };

    [Theory]
    [MemberData(nameof(IncompleteUsers))]
    public async Task GetSseToken_WithoutUserIdOrName_ReturnsUnauthorized(string kind)
    {
        var user = kind switch
        {
            "no-identity" => new ClaimsPrincipal(),
            "no-user-id" => SignedIn(userId: null, name: "reader"),
            _ => SignedIn(userId: "user-1", name: null),
        };
        var controller = CreateController(user);

        var result = await controller.GetSseToken();

        Assert.IsType<UnauthorizedResult>(result);
        _authService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetSseToken_ForSignedInUser_ReturnsFreshToken()
    {
        _authService.Setup(s => s.GenerateTokenForUserAsync("user-1")).ReturnsAsync((true, "sse-jwt", null));
        var controller = CreateController(SignedIn(extra: new Claim("auth_method", "authelia")));

        var result = await controller.GetSseToken();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("sse-jwt", Prop<string>(ok.Value, "token"));
        _authService.Verify(s => s.GenerateTokenForUserAsync("user-1"), Times.Once);
    }

    [Fact]
    public async Task GetSseToken_WhenTokenGenerationFails_Returns500WithGenericError()
    {
        _authService.Setup(s => s.GenerateTokenForUserAsync("user-1"))
            .ReturnsAsync((false, string.Empty, "User not found in database"));
        var controller = CreateController(SignedIn());

        var result = await controller.GetSseToken();

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, error.StatusCode);
        Assert.Equal("Failed to generate token", Prop<string>(error.Value, "error"));
    }

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
