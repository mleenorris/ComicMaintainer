using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.Tests.Helpers;
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

        _authService = new AuthService(
            _userManager.Object,
            settings,
            Options.Create(new AutheliaSettings()),
            new ComicMaintainer.Core.Services.Auth.SetupTokenProvider(null),
            Mock.Of<ComicMaintainer.Core.Services.Auth.IAuthSessionValidator>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance);
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

        _userManager.VerifyGet(x => x.Users, Times.Never());
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_RejectsAnOverlongKeyWithoutQueryingUsers()
    {
        Assert.Null(await _authService.GetUserByApiKeyAsync(new string('k', ApiKeys.MaxLength + 1)));

        _userManager.VerifyGet(x => x.Users, Times.Never());
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_IgnoresTheKeyOfADisabledUser()
    {
        var user = new ApplicationUser { Id = "disabled", ApiKeyHash = ApiKeys.Hash("the-key"), IsActive = false };
        _userManager.Setup(x => x.Users).Returns(InMemoryUsers.Of(user));

        Assert.Null(await _authService.GetUserByApiKeyAsync("the-key"));
    }

    // The key acts as its owner whatever the owner's roles: before 3.0.0 only accounts in the User role matched.
    [Fact]
    public async Task GetUserByApiKeyAsync_ReturnsTheActiveUserWhoseKeyHashMatches()
    {
        var other = new ApplicationUser { Id = "other", ApiKeyHash = ApiKeys.Hash("another-key"), IsActive = true };
        var owner = new ApplicationUser { Id = "owner", ApiKeyHash = ApiKeys.Hash("the-key"), IsActive = true };
        _userManager.Setup(x => x.Users).Returns(InMemoryUsers.Of(other, owner));

        Assert.Equal(owner.Id, (await _authService.GetUserByApiKeyAsync("the-key"))?.Id);
        Assert.Null(await _authService.GetUserByApiKeyAsync(owner.ApiKeyHash!));
        _userManager.Verify(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task GenerateApiKeyAsync_StoresOnlyTheHashAndReplacesThePreviousKey()
    {
        var user = new ApplicationUser
        {
            Id = "user-1",
            IsActive = true,
            ApiKeyHash = ApiKeys.Hash("old-key"),
            ApiKeyLastUsedAt = DateTime.UtcNow.AddDays(-1),
        };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);
        _userManager.Setup(x => x.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var (success, apiKey, error) = await _authService.GenerateApiKeyAsync("user-1");

        Assert.True(success, error);
        Assert.StartsWith(ApiKeys.Prefix, apiKey);
        Assert.Equal(ApiKeys.Hash(apiKey!), user.ApiKeyHash);
        Assert.DoesNotContain(apiKey!, user.ApiKeyHash);
        Assert.NotNull(user.ApiKeyCreatedAt);
        Assert.Null(user.ApiKeyLastUsedAt);
    }

    [Fact]
    public async Task GenerateApiKeyAsync_RefusesADisabledAccount()
    {
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(new ApplicationUser { Id = "user-1", IsActive = false });

        var (success, apiKey, _) = await _authService.GenerateApiKeyAsync("user-1");

        Assert.False(success);
        Assert.Null(apiKey);
        _userManager.Verify(x => x.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never());
    }

    [Fact]
    public async Task RevokeApiKeyAsync_ClearsTheKeyAndReportsWhetherThereWasOne()
    {
        var user = new ApplicationUser { Id = "user-1", IsActive = true, ApiKeyHash = ApiKeys.Hash("k"), ApiKeyCreatedAt = DateTime.UtcNow };
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(user);
        _userManager.Setup(x => x.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        Assert.True(await _authService.RevokeApiKeyAsync("user-1"));
        Assert.Null(user.ApiKeyHash);
        Assert.Null(user.ApiKeyCreatedAt);
        Assert.Equal(ApiKeyStatus.None, await _authService.GetApiKeyStatusAsync("user-1"));

        Assert.False(await _authService.RevokeApiKeyAsync("user-1"));
        _userManager.Verify(x => x.UpdateAsync(user), Times.Once());
    }

    [Fact]
    public async Task GetApiKeyStatusAsync_ReportsWhenTheKeyWasCreatedAndLastUsed()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var used = created.AddHours(5);
        _userManager.Setup(x => x.FindByIdAsync("user-1")).ReturnsAsync(
            new ApplicationUser { Id = "user-1", ApiKeyHash = ApiKeys.Hash("k"), ApiKeyCreatedAt = created, ApiKeyLastUsedAt = used });

        Assert.Equal(new ApiKeyStatus(true, created, used), await _authService.GetApiKeyStatusAsync("user-1"));
        Assert.Equal(ApiKeyStatus.None, await _authService.GetApiKeyStatusAsync("missing"));
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
