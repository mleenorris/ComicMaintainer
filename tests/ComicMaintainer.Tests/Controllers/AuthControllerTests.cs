using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class AuthControllerTests
{
    private readonly Mock<IAuthService> _mockAuthService;
    private readonly Mock<ILogger<AuthController>> _mockLogger;
    private readonly Mock<IOptions<AutheliaSettings>> _mockAutheliaSettings;
    private readonly Mock<IAuthorizationService> _mockAuthorizationService;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _mockAuthService = new Mock<IAuthService>();
        _mockLogger = new Mock<ILogger<AuthController>>();
        _mockAutheliaSettings = new Mock<IOptions<AutheliaSettings>>();
        
        // Setup default Authelia settings (disabled by default)
        _mockAutheliaSettings.Setup(x => x.Value).Returns(new AutheliaSettings { Enabled = false });

        // Capability lookups on GET /api/auth/user delegate to the authorization
        // service; succeed by default so existing assertions are unaffected.
        _mockAuthorizationService = new Mock<IAuthorizationService>();
        _mockAuthorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());

        _controller = new AuthController(
            _mockAuthService.Object, 
            _mockLogger.Object,
            _mockAutheliaSettings.Object,
            _mockAuthorizationService.Object);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsOkWithToken()
    {
        // Arrange
        var request = new LoginRequest("testuser", "password123");
        var expectedToken = "test-jwt-token";
        _mockAuthService
            .Setup(s => s.LoginAsync("testuser", "password123"))
            .ReturnsAsync((true, expectedToken, null));

        // Act
        var result = await _controller.Login(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var tokenProperty = value.GetType().GetProperty("token");
        Assert.NotNull(tokenProperty);
        Assert.Equal(expectedToken, tokenProperty.GetValue(value));
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        // Arrange
        var request = new LoginRequest("testuser", "wrongpassword");
        _mockAuthService
            .Setup(s => s.LoginAsync("testuser", "wrongpassword"))
            .ReturnsAsync((false, string.Empty, "Invalid username or password"));

        // Act
        var result = await _controller.Login(request);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        var value = unauthorizedResult.Value;
        Assert.NotNull(value);
        
        var errorProperty = value.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.Equal("Invalid username or password", errorProperty.GetValue(value));
    }

    [Fact]
    public async Task Login_WithNonExistentUser_ReturnsUnauthorized()
    {
        // Arrange
        var request = new LoginRequest("nonexistent", "password123");
        _mockAuthService
            .Setup(s => s.LoginAsync("nonexistent", "password123"))
            .ReturnsAsync((false, string.Empty, "Invalid username or password"));

        // Act
        var result = await _controller.Login(request);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(unauthorizedResult.Value);
    }

    [Fact]
    public async Task Register_WithValidData_ReturnsOk()
    {
        // Arrange
        var request = new RegisterRequest("newuser", "Password123!", "user@example.com", "New User");
        _mockAuthService
            .Setup(s => s.RegisterAsync("newuser", "Password123!", "user@example.com", "New User"))
            .ReturnsAsync((true, null));

        // Act
        var result = await _controller.Register(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var messageProperty = value.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal("User registered successfully", messageProperty.GetValue(value));
    }

    [Fact]
    public async Task Register_WithExistingUsername_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterRequest("existinguser", "Password123!", "user@example.com", "User");
        _mockAuthService
            .Setup(s => s.RegisterAsync("existinguser", "Password123!", "user@example.com", "User"))
            .ReturnsAsync((false, "Username is already taken"));

        // Act
        var result = await _controller.Register(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var value = badRequestResult.Value;
        Assert.NotNull(value);
        
        var errorProperty = value.GetType().GetProperty("error");
        Assert.NotNull(errorProperty);
        Assert.Equal("Username is already taken", errorProperty.GetValue(value));
    }

    [Fact]
    public async Task Register_WithInvalidEmail_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterRequest("newuser", "Password123!", "invalidemail", "User");
        _mockAuthService
            .Setup(s => s.RegisterAsync("newuser", "Password123!", "invalidemail", "User"))
            .ReturnsAsync((false, "Invalid email address"));

        // Act
        var result = await _controller.Register(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task Register_WithWeakPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new RegisterRequest("newuser", "weak", "user@example.com", "User");
        _mockAuthService
            .Setup(s => s.RegisterAsync("newuser", "weak", "user@example.com", "User"))
            .ReturnsAsync((false, "Password is too weak"));

        // Act
        var result = await _controller.Register(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_ReturnsOk()
    {
        // Arrange
        SetupAuthenticatedUser("user-id-123");
        var request = new ChangePasswordRequest("OldPassword123!", "NewPassword456!");
        _mockAuthService
            .Setup(s => s.ChangePasswordAsync("user-id-123", "OldPassword123!", "NewPassword456!"))
            .ReturnsAsync((true, null));

        // Act
        var result = await _controller.ChangePassword(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var messageProperty = value.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal("Password changed successfully", messageProperty.GetValue(value));
    }

    [Fact]
    public async Task ChangePassword_WithInvalidCurrentPassword_ReturnsBadRequest()
    {
        // Arrange
        SetupAuthenticatedUser("user-id-123");
        var request = new ChangePasswordRequest("WrongPassword!", "NewPassword456!");
        _mockAuthService
            .Setup(s => s.ChangePasswordAsync("user-id-123", "WrongPassword!", "NewPassword456!"))
            .ReturnsAsync((false, "Current password is incorrect"));

        // Act
        var result = await _controller.ChangePassword(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task ChangePassword_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange - Set up empty user context (no claims)
        SetupEmptyUserContext();
        var request = new ChangePasswordRequest("OldPassword!", "NewPassword!");

        // Act
        var result = await _controller.ChangePassword(request);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GenerateApiKey_WithAuthenticatedUser_ReturnsOkWithApiKey()
    {
        // Arrange
        SetupAuthenticatedUser("user-id-123");
        var expectedApiKey = "api-key-12345";
        _mockAuthService
            .Setup(s => s.GenerateApiKeyAsync("user-id-123"))
            .ReturnsAsync((true, expectedApiKey, null));

        // Act
        var result = await _controller.GenerateApiKey();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        var apiKeyProperty = value.GetType().GetProperty("apiKey");
        Assert.NotNull(apiKeyProperty);
        Assert.Equal(expectedApiKey, apiKeyProperty.GetValue(value));
    }

    [Fact]
    public async Task GenerateApiKey_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange - Set up empty user context (no claims)
        SetupEmptyUserContext();

        // Act
        var result = await _controller.GenerateApiKey();

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GenerateApiKey_WhenServiceFails_ReturnsBadRequest()
    {
        // Arrange
        SetupAuthenticatedUser("user-id-123");
        _mockAuthService
            .Setup(s => s.GenerateApiKeyAsync("user-id-123"))
            .ReturnsAsync((false, string.Empty, "Failed to generate API key"));

        // Act
        var result = await _controller.GenerateApiKey();

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public void GetAuthStatus_WithAutheliaDisabled_ReturnsCorrectStatus()
    {
        // Arrange
        SetupEmptyUserContext();
        
        // Act
        var result = _controller.GetAuthStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var autheliaEnabledProperty = value.GetType().GetProperty("autheliaEnabled");
        Assert.NotNull(autheliaEnabledProperty);
        Assert.False((bool)autheliaEnabledProperty.GetValue(value)!);
        
        var requiresLoginProperty = value.GetType().GetProperty("requiresLogin");
        Assert.NotNull(requiresLoginProperty);
        Assert.True((bool)requiresLoginProperty.GetValue(value)!);
    }

    [Fact]
    public void GetAuthStatus_WithAutheliaEnabled_ReturnsCorrectStatus()
    {
        // Arrange
        _mockAutheliaSettings.Setup(x => x.Value).Returns(new AutheliaSettings { Enabled = true });
        var controller = new AuthController(
            _mockAuthService.Object, 
            _mockLogger.Object,
            _mockAutheliaSettings.Object,
            _mockAuthorizationService.Object);
        SetupAuthenticatedUserOnController(controller, "user-id-123", "testuser");

        // Act
        var result = controller.GetAuthStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var autheliaEnabledProperty = value.GetType().GetProperty("autheliaEnabled");
        Assert.NotNull(autheliaEnabledProperty);
        Assert.True((bool)autheliaEnabledProperty.GetValue(value)!);
        
        var isAuthenticatedProperty = value.GetType().GetProperty("isAuthenticated");
        Assert.NotNull(isAuthenticatedProperty);
        Assert.True((bool)isAuthenticatedProperty.GetValue(value)!);
        
        var requiresLoginProperty = value.GetType().GetProperty("requiresLogin");
        Assert.NotNull(requiresLoginProperty);
        Assert.False((bool)requiresLoginProperty.GetValue(value)!);
    }

    [Fact]
    public async Task GetCurrentUser_WithAuthenticatedUser_ReturnsUserInfo()
    {
        // Arrange
        SetupAuthenticatedUser("user-id-123", "testuser", "test@example.com", new[] { "Admin", "User" });

        // Act
        var result = await _controller.GetCurrentUser();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var usernameProperty = value.GetType().GetProperty("username");
        Assert.NotNull(usernameProperty);
        Assert.Equal("testuser", usernameProperty.GetValue(value));
        
        var emailProperty = value.GetType().GetProperty("email");
        Assert.NotNull(emailProperty);
        Assert.Equal("test@example.com", emailProperty.GetValue(value));
    }

    [Fact]
    public async Task GetCurrentUser_WithoutAuthentication_ReturnsEmptyUserInfo()
    {
        // Arrange
        SetupEmptyUserContext();

        // Act
        var result = await _controller.GetCurrentUser();

        // Assert - endpoint returns OK with null/empty values when not authenticated
        // The [Authorize] attribute should prevent access, but in tests we're testing the controller directly
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task GetCurrentUser_WithAutheliaAuthentication_ReturnsAutheliaUsername()
    {
        // Arrange - Simulate Authelia authentication with username from header
        // This tests the fix for the issue where "admin" was shown instead of the Authelia username
        var autheliaUsername = "john.doe";
        var autheliaEmail = "john@example.com";
        
        // Setup authenticated user with Authelia auth method claim
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, "user-id-456"),
            new(System.Security.Claims.ClaimTypes.Name, autheliaUsername),  // This should be from Authelia header
            new(System.Security.Claims.ClaimTypes.Email, autheliaEmail),
            new(System.Security.Claims.ClaimTypes.Role, "User"),
            new("auth_method", "authelia")
        };
        
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Authelia");
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };

        // Act
        var result = await _controller.GetCurrentUser();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        // Verify username is from Authelia (not from database)
        var usernameProperty = value.GetType().GetProperty("username");
        Assert.NotNull(usernameProperty);
        Assert.Equal(autheliaUsername, usernameProperty.GetValue(value));
        
        // Verify auth method is Authelia
        var authMethodProperty = value.GetType().GetProperty("authMethod");
        Assert.NotNull(authMethodProperty);
        Assert.Equal("authelia", authMethodProperty.GetValue(value));
    }

    [Fact]
    public void GetAuthStatus_WithAutheliaAuthenticatedUser_ReturnsCorrectUsername()
    {
        // Arrange - Simulate Authelia authentication
        var autheliaUsername = "jane.doe";
        _mockAutheliaSettings.Setup(x => x.Value).Returns(new AutheliaSettings { Enabled = true });
        
        var controller = new AuthController(
            _mockAuthService.Object, 
            _mockLogger.Object,
            _mockAutheliaSettings.Object,
            _mockAuthorizationService.Object);
        
        SetupAuthenticatedUserOnController(controller, "user-id-789", autheliaUsername);

        // Act
        var result = controller.GetAuthStatus();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var usernameProperty = value.GetType().GetProperty("username");
        Assert.NotNull(usernameProperty);
        Assert.Equal(autheliaUsername, usernameProperty.GetValue(value));
        
        var isAuthenticatedProperty = value.GetType().GetProperty("isAuthenticated");
        Assert.NotNull(isAuthenticatedProperty);
        Assert.True((bool)isAuthenticatedProperty.GetValue(value)!);
    }

    private void SetupAuthenticatedUser(string userId, string? username = null, string? email = null, string[]? roles = null)
    {
        SetupAuthenticatedUserOnController(_controller, userId, username, email, roles);
    }

    private void SetupAuthenticatedUserOnController(AuthController controller, string userId, string? username = null, string? email = null, string[]? roles = null)
    {
        // Setup a mock user with claims
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId)
        };
        
        if (username != null)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username));
        }
        
        if (email != null)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, email));
        }
        
        if (roles != null)
        {
            foreach (var role in roles)
            {
                claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
            }
        }
        
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };
    }

    private void SetupEmptyUserContext()
    {
        // Setup an empty user context (no claims)
        var identity = new System.Security.Claims.ClaimsIdentity();
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };
    }
}

// Tests for record types
public class AuthRequestRecordsTests
{
    [Fact]
    public void LoginRequest_CanBeCreated()
    {
        // Act
        var request = new LoginRequest("testuser", "password123");

        // Assert
        Assert.Equal("testuser", request.Username);
        Assert.Equal("password123", request.Password);
    }

    [Fact]
    public void RegisterRequest_CanBeCreated()
    {
        // Act
        var request = new RegisterRequest("testuser", "password123", "test@example.com", "Test User");

        // Assert
        Assert.Equal("testuser", request.Username);
        Assert.Equal("password123", request.Password);
        Assert.Equal("test@example.com", request.Email);
        Assert.Equal("Test User", request.FullName);
    }

    [Fact]
    public void RegisterRequest_WithNullFullName_CanBeCreated()
    {
        // Act
        var request = new RegisterRequest("testuser", "password123", "test@example.com", null);

        // Assert
        Assert.Equal("testuser", request.Username);
        Assert.Null(request.FullName);
    }

    [Fact]
    public void ChangePasswordRequest_CanBeCreated()
    {
        // Act
        var request = new ChangePasswordRequest("oldpass", "newpass");

        // Assert
        Assert.Equal("oldpass", request.CurrentPassword);
        Assert.Equal("newpass", request.NewPassword);
    }

    [Fact]
    public void SetupRequest_CanBeCreated()
    {
        // Act
        var request = new SetupRequest("admin", "password123", "admin@example.com");

        // Assert
        Assert.Equal("admin", request.Username);
        Assert.Equal("password123", request.Password);
        Assert.Equal("admin@example.com", request.Email);
    }

    [Fact]
    public void SetupRequest_WithNullEmail_CanBeCreated()
    {
        // Act
        var request = new SetupRequest("admin", "password123", null);

        // Assert
        Assert.Equal("admin", request.Username);
        Assert.Equal("password123", request.Password);
        Assert.Null(request.Email);
    }
}
