using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class AuthServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
    private readonly Mock<IOptions<JwtSettings>> _mockJwtSettings;
    private readonly AuthService _authService;

    public AuthServiceTests()
    {
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);
        
        var jwtSettings = new JwtSettings
        {
            Secret = "ThisIsASecretKeyForTestingPurposesWithAtLeast32Characters!",
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            ExpirationMinutes = 60
        };
        _mockJwtSettings = new Mock<IOptions<JwtSettings>>();
        _mockJwtSettings.Setup(x => x.Value).Returns(jwtSettings);

        _authService = new AuthService(_mockUserManager.Object, _mockJwtSettings.Object);
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsSuccessWithToken()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user-id",
            UserName = "testuser",
            Email = "test@example.com",
            IsActive = true
        };

        _mockUserManager.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.CheckPasswordAsync(user, "password123"))
            .ReturnsAsync(true);
        _mockUserManager.Setup(x => x.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.GetRolesAsync(user))
            .ReturnsAsync(new List<string> { "User" });

        // Act
        var (success, token, error) = await _authService.LoginAsync("testuser", "password123");

        // Assert
        Assert.True(success);
        Assert.NotEmpty(token);
        Assert.Null(error);
    }

    [Fact]
    public async Task LoginAsync_WithInvalidPassword_ReturnsFailure()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user-id",
            UserName = "testuser",
            Email = "test@example.com",
            IsActive = true
        };

        _mockUserManager.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.CheckPasswordAsync(user, "wrongpassword"))
            .ReturnsAsync(false);

        // Act
        var (success, token, error) = await _authService.LoginAsync("testuser", "wrongpassword");

        // Assert
        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal("Invalid username or password", error);
    }

    [Fact]
    public async Task LoginAsync_WithNonExistentUser_ReturnsFailure()
    {
        // Arrange
        _mockUserManager.Setup(x => x.FindByNameAsync("nonexistent"))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var (success, token, error) = await _authService.LoginAsync("nonexistent", "password123");

        // Assert
        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal("Invalid username or password", error);
    }

    [Fact]
    public async Task LoginAsync_WithInactiveUser_ReturnsFailure()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user-id",
            UserName = "testuser",
            Email = "test@example.com",
            IsActive = false
        };

        _mockUserManager.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(user);

        // Act
        var (success, token, error) = await _authService.LoginAsync("testuser", "password123");

        // Assert
        Assert.False(success);
        Assert.Empty(token);
        Assert.Equal("Account is disabled", error);
    }

    [Fact]
    public async Task RegisterAsync_WithValidData_ReturnsSuccess()
    {
        // Arrange
        _mockUserManager.Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), "Password123!"))
            .ReturnsAsync(IdentityResult.Success);
        _mockUserManager.Setup(x => x.AddToRoleAsync(It.IsAny<ApplicationUser>(), "User"))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var (success, error) = await _authService.RegisterAsync("newuser", "Password123!", "user@example.com", "New User");

        // Assert
        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task RegisterAsync_WithExistingUsername_ReturnsFailure()
    {
        // Arrange
        var identityError = new IdentityError { Description = "Username is already taken" };
        _mockUserManager.Setup(x => x.CreateAsync(It.IsAny<ApplicationUser>(), "Password123!"))
            .ReturnsAsync(IdentityResult.Failed(identityError));

        // Act
        var (success, error) = await _authService.RegisterAsync("existinguser", "Password123!", "user@example.com", "User");

        // Assert
        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Username is already taken", error);
    }

    [Fact]
    public async Task ChangePasswordAsync_WithValidData_ReturnsSuccess()
    {
        // Arrange
        var user = new ApplicationUser { Id = "test-user-id" };
        _mockUserManager.Setup(x => x.FindByIdAsync("test-user-id"))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.ChangePasswordAsync(user, "OldPassword123!", "NewPassword123!"))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var (success, error) = await _authService.ChangePasswordAsync("test-user-id", "OldPassword123!", "NewPassword123!");

        // Assert
        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task ChangePasswordAsync_WithInvalidUserId_ReturnsFailure()
    {
        // Arrange
        _mockUserManager.Setup(x => x.FindByIdAsync("invalid-id"))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var (success, error) = await _authService.ChangePasswordAsync("invalid-id", "OldPassword123!", "NewPassword123!");

        // Assert
        Assert.False(success);
        Assert.Equal("User not found", error);
    }

    [Fact]
    public async Task GenerateApiKeyAsync_WithValidUser_ReturnsSuccess()
    {
        // Arrange
        var user = new ApplicationUser { Id = "test-user-id" };
        _mockUserManager.Setup(x => x.FindByIdAsync("test-user-id"))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync("test-user-id");

        // Assert
        Assert.True(success);
        Assert.NotNull(apiKey);
        Assert.NotEmpty(apiKey);
        Assert.Null(error);
    }

    [Fact]
    public async Task GenerateApiKeyAsync_WithInvalidUser_ReturnsFailure()
    {
        // Arrange
        _mockUserManager.Setup(x => x.FindByIdAsync("invalid-id"))
            .ReturnsAsync((ApplicationUser?)null);

        // Act
        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync("invalid-id");

        // Assert
        Assert.False(success);
        Assert.Null(apiKey);
        Assert.Equal("User not found", error);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_WithValidKey_ReturnsTrue()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = "test-user-id",
            ApiKey = "valid-api-key",
            IsActive = true
        };

        _mockUserManager.Setup(x => x.GetUsersInRoleAsync("User"))
            .ReturnsAsync(new List<ApplicationUser> { user });

        // Act
        var isValid = await _authService.ValidateApiKeyAsync("valid-api-key");

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_WithInvalidKey_ReturnsFalse()
    {
        // Arrange
        _mockUserManager.Setup(x => x.GetUsersInRoleAsync("User"))
            .ReturnsAsync(new List<ApplicationUser>());

        // Act
        var isValid = await _authService.ValidateApiKeyAsync("invalid-api-key");

        // Assert
        Assert.False(isValid);
    }
}
