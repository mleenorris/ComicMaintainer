using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class AuthControllerTests
{
    private readonly Mock<IAuthService> _mockAuthService;
    private readonly Mock<ILogger<AuthController>> _mockLogger;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _mockAuthService = new Mock<IAuthService>();
        _mockLogger = new Mock<ILogger<AuthController>>();
        _controller = new AuthController(_mockAuthService.Object, _mockLogger.Object);
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

    private void SetupAuthenticatedUser(string userId)
    {
        // Setup a mock user with claims
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId)
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
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
