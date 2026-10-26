using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class AuthServiceCoverageTests
{
    private const string Secret = "ThisIsASecretKeyForTestingPurposesWithAtLeast32Characters!";

    private readonly Mock<UserManager<ApplicationUser>> _userManager;
    private readonly AuthService _authService;

    public AuthServiceCoverageTests()
    {
        _userManager = new Mock<UserManager<ApplicationUser>>(
            new Mock<IUserStore<ApplicationUser>>().Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var settings = Options.Create(new JwtSettings
        {
            Secret = Secret,
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            ExpirationMinutes = 60
        });

        _authService = new AuthService(_userManager.Object, settings);
    }

    [Fact]
    public async Task ChangePasswordAsync_ReportsEveryIdentityErrorWhenTheChangeIsRejected()
    {
        var user = new ApplicationUser { Id = "user-1", UserName = "reader", IsActive = true };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);
        _userManager.Setup(x => x.ChangePasswordAsync(user, "old", "new"))
            .ReturnsAsync(IdentityResult.Failed(
                new IdentityError { Description = "Incorrect password." },
                new IdentityError { Description = "Passwords must have at least one digit." }));

        var (success, error) = await _authService.ChangePasswordAsync("user-1", "old", "new");

        Assert.False(success);
        Assert.Equal("Incorrect password., Passwords must have at least one digit.", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetUserByApiKeyAsync_RejectsAMissingKeyWithoutQueryingUsers(string? apiKey)
    {
        Assert.Null(await _authService.GetUserByApiKeyAsync(apiKey!));
        Assert.False(await _authService.ValidateApiKeyAsync(apiKey!));

        _userManager.VerifyGet(x => x.Users, Times.Never());
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_RejectsTheKeyOfAUserOutsideTheUserRole()
    {
        var user = new ApplicationUser { Id = "admin-only", ApiKey = "the-key", IsActive = true };
        _userManager.Setup(x => x.Users).Returns(new List<ApplicationUser> { user }.AsQueryable());
        _userManager.Setup(x => x.IsInRoleAsync(user, "User")).ReturnsAsync(false);

        Assert.Null(await _authService.GetUserByApiKeyAsync("the-key"));
        Assert.False(await _authService.ValidateApiKeyAsync("the-key"));
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_IgnoresTheKeyOfADisabledUser()
    {
        var user = new ApplicationUser { Id = "disabled", ApiKey = "the-key", IsActive = false };
        _userManager.Setup(x => x.Users).Returns(new List<ApplicationUser> { user }.AsQueryable());
        _userManager.Setup(x => x.IsInRoleAsync(user, "User")).ReturnsAsync(true);

        Assert.Null(await _authService.GetUserByApiKeyAsync("the-key"));
        _userManager.Verify(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_ReturnsTheActiveUserHoldingTheKey()
    {
        var other = new ApplicationUser { Id = "other", ApiKey = "another-key", IsActive = true };
        var owner = new ApplicationUser { Id = "owner", ApiKey = "the-key", IsActive = true };
        _userManager.Setup(x => x.Users).Returns(new List<ApplicationUser> { other, owner }.AsQueryable());
        _userManager.Setup(x => x.IsInRoleAsync(owner, "User")).ReturnsAsync(true);

        Assert.Same(owner, await _authService.GetUserByApiKeyAsync("the-key"));
    }

    [Fact]
    public async Task GenerateApiKeyAsync_FailsWhenTheUserCannotBeSaved()
    {
        var user = new ApplicationUser { Id = "user-1", IsActive = true };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);
        _userManager.Setup(x => x.UpdateAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Concurrency failure" }));

        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync("user-1");

        Assert.False(success);
        Assert.Null(apiKey);
        Assert.Equal("Failed to update user", error);
    }

    [Fact]
    public async Task GenerateTokenForUserAsync_FailsForAnUnknownUser()
    {
        _userManager.Setup(x => x.FindByIdAsync("missing")).ReturnsAsync((ApplicationUser?)null);

        var (success, token, error) = await _authService.GenerateTokenForUserAsync("missing");

        Assert.False(success);
        Assert.Equal(string.Empty, token);
        Assert.Equal("User not found", error);
    }

    [Fact]
    public async Task GenerateTokenForUserAsync_FailsForADisabledAccount()
    {
        var user = new ApplicationUser { Id = "user-1", UserName = "reader", IsActive = false };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);

        var (success, token, error) = await _authService.GenerateTokenForUserAsync("user-1");

        Assert.False(success);
        Assert.Equal(string.Empty, token);
        Assert.Equal("Account is disabled", error);
        _userManager.Verify(x => x.GetRolesAsync(It.IsAny<ApplicationUser>()), Times.Never());
    }

    [Fact]
    public async Task GenerateTokenForUserAsync_IssuesASignedTokenCarryingTheUsersIdentityAndRoles()
    {
        var user = new ApplicationUser { Id = "user-1", UserName = "reader", Email = "reader@example.com", IsActive = true };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);
        _userManager.Setup(x => x.GetRolesAsync(user)).ReturnsAsync(new List<string> { "User", "Admin" });

        var (success, token, error) = await _authService.GenerateTokenForUserAsync("user-1");

        Assert.True(success);
        Assert.Null(error);
        var principal = Validate(token);
        Assert.Equal("user-1", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("reader", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("reader@example.com", principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(new[] { "Admin", "User" }, principal.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r).ToArray());
    }

    [Fact]
    public async Task GenerateTokenForUserAsync_UsesEmptyNameAndEmailClaimsWhenTheUserHasNone()
    {
        var user = new ApplicationUser { Id = "user-2", UserName = null, Email = null, IsActive = true };
        _userManager.Setup(x => x.FindByIdAsync("user-2")).ReturnsAsync(user);
        _userManager.Setup(x => x.GetRolesAsync(user)).ReturnsAsync(new List<string>());

        var (success, token, _) = await _authService.GenerateTokenForUserAsync("user-2");

        Assert.True(success);
        var principal = Validate(token);
        Assert.Equal("user-2", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(string.Empty, principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(string.Empty, principal.FindFirstValue(ClaimTypes.Email));
        Assert.Empty(principal.FindAll(ClaimTypes.Role));
    }

    private static ClaimsPrincipal Validate(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "TestIssuer",
            ValidAudience = "TestAudience",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            ValidateLifetime = true
        };

        return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
    }
}
