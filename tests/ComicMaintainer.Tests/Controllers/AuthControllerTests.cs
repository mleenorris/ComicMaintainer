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
}
