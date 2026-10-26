using System.Security.Claims;
using System.Text.Encodings.Web;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// Unit tests for the Authelia forward-auth handler: header handling, just-in-time
/// user provisioning and the group → role mapping.
/// </summary>
public class AutheliaAuthenticationHandlerTests
{
    private const string SchemeName = "Authelia";
    private const string PasswordAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";

    private readonly Mock<UserManager<ApplicationUser>> _userManager;
    private readonly Mock<RoleManager<ApplicationRole>> _roleManager;
    private readonly Dictionary<string, List<string>> _rolesByUserName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existingRoles = new(StringComparer.Ordinal) { "Admin", "User", "ReadOnly" };

    public AutheliaAuthenticationHandlerTests()
    {
        _userManager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        _roleManager = new Mock<RoleManager<ApplicationRole>>(
            Mock.Of<IRoleStore<ApplicationRole>>(), null!, null!, null!, null!);

        // A tiny stateful role store so the handler sees the effect of its own
        // role assignment when it builds the claims afterwards.
        _roleManager
            .Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
            .ReturnsAsync((string role) => _existingRoles.Contains(role));
        _userManager
            .Setup(u => u.GetRolesAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync((ApplicationUser user) => (IList<string>)RolesOf(user).ToList());
        _userManager
            .Setup(u => u.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((ApplicationUser user, IEnumerable<string> roles) =>
            {
                var removed = roles.ToList();
                RolesOf(user).RemoveAll(removed.Contains);
                return IdentityResult.Success;
            });
        _userManager
            .Setup(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync((ApplicationUser user, string role) =>
            {
                RolesOf(user).Add(role);
                return IdentityResult.Success;
            });
        _userManager
            .Setup(u => u.UpdateAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);
    }

    [Fact]
    public async Task Authenticate_WhenAutheliaIsDisabled_ReturnsNoResultEvenWithHeaders()
    {
        var context = CreateContext(("Remote-User", "alice"));

        var result = await AuthenticateAsync(new AutheliaSettings { Enabled = false }, context);

        Assert.True(result.None);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WithoutUserHeader_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-Email", "alice@example.com")));

        Assert.True(result.None);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Authenticate_WithBlankUserHeader_Fails(string headerValue)
    {
        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-User", headerValue)));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal("Empty Remote-User header", result.Failure!.Message);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_HonoursCustomHeaderNames()
    {
        var settings = Enabled();
        settings.UserHeader = "X-Auth-User";
        settings.EmailHeader = "X-Auth-Email";
        settings.NameHeader = "X-Auth-Name";
        settings.GroupsHeader = "X-Auth-Groups";
        var existing = ExistingUser("carol", email: "old@example.com", fullName: "Old Name", "User");

        var result = await AuthenticateAsync(settings, CreateContext(
            ("Remote-User", "mallory"),
            ("X-Auth-User", "carol"),
            ("X-Auth-Email", "carol@example.com"),
            ("X-Auth-Name", "Carol C"),
            ("X-Auth-Groups", "readers")));

        Assert.True(result.Succeeded);
        Assert.Equal("carol", result.Principal!.Identity!.Name);
        Assert.Equal("carol@example.com", existing.Email);
        Assert.Equal("Carol C", existing.FullName);
        Assert.Contains(result.Principal.Claims, c => c.Type == "authelia_group" && c.Value == "readers");
        _userManager.Verify(u => u.FindByNameAsync("mallory"), Times.Never);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithUnchangedDetails_SucceedsWithoutUpdating()
    {
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A", "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Email", "alice@example.com"),
            ("Remote-Name", "Alice A")));

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(SchemeName, result.Ticket!.AuthenticationScheme);
        Assert.Equal(SchemeName, principal.Identity!.AuthenticationType);
        Assert.Equal(existing.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("alice", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("alice@example.com", principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal("authelia", principal.FindFirstValue("auth_method"));
        Assert.Equal(new[] { "User" }, principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
        Assert.DoesNotContain(principal.Claims, c => c.Type == "authelia_group");

        _userManager.Verify(u => u.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
        _userManager.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        _userManager.Verify(
            u => u.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()),
            Times.Never);
        _userManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithoutOptionalHeaders_KeepsStoredDetails()
    {
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A", "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-User", "alice")));

        Assert.True(result.Succeeded);
        Assert.Equal("alice@example.com", existing.Email);
        Assert.Equal("Alice A", existing.FullName);
        Assert.Equal("alice@example.com", result.Principal!.FindFirstValue(ClaimTypes.Email));
        _userManager.Verify(u => u.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithChangedEmailAndName_UpdatesTheUser()
    {
        var existing = ExistingUser("alice", email: "old@example.com", fullName: "Old Name", "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Email", "new@example.com"),
            ("Remote-Name", "New Name")));

        Assert.True(result.Succeeded);
        Assert.Equal("new@example.com", existing.Email);
        Assert.Equal("New Name", existing.FullName);
        Assert.Equal("new@example.com", result.Principal!.FindFirstValue(ClaimTypes.Email));
        _userManager.Verify(
            u => u.UpdateAsync(It.Is<ApplicationUser>(x =>
                x.Email == "new@example.com" && x.FullName == "New Name")),
            Times.Once);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithOnlyChangedEmail_UpdatesOnlyTheEmail()
    {
        var existing = ExistingUser("alice", email: "old@example.com", fullName: "Alice A", "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Email", "new@example.com"),
            ("Remote-Name", "Alice A")));

        Assert.True(result.Succeeded);
        Assert.Equal("new@example.com", existing.Email);
        Assert.Equal("Alice A", existing.FullName);
        _userManager.Verify(u => u.UpdateAsync(existing), Times.Once);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithOnlyChangedName_UpdatesOnlyTheName()
    {
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Old Name", "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Name", "New Name")));

        Assert.True(result.Succeeded);
        Assert.Equal("alice@example.com", existing.Email);
        Assert.Equal("New Name", existing.FullName);
        _userManager.Verify(u => u.UpdateAsync(existing), Times.Once);
    }

    [Fact]
    public async Task Authenticate_ExistingUserWithoutAStoredEmail_StillAuthenticates()
    {
        var existing = ExistingUser("alice", email: null, fullName: null, "User");

        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-User", "alice")));

        Assert.True(result.Succeeded);
        Assert.Equal(existing.Id, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("alice", result.Principal.FindFirstValue(ClaimTypes.Name));
        _userManager.Verify(u => u.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_UnknownUser_IsProvisionedWithTheDefaultRole()
    {
        ApplicationUser? created = null;
        string? password = null;
        _userManager.Setup(u => u.FindByNameAsync("bob")).ReturnsAsync((ApplicationUser?)null);
        _userManager
            .Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback((ApplicationUser user, string pwd) =>
            {
                created = user;
                password = pwd;
            })
            .ReturnsAsync(IdentityResult.Success);

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "bob"),
            ("Remote-Email", "bob@example.com"),
            ("Remote-Name", "Bob B")));

        Assert.True(result.Succeeded);
        Assert.NotNull(created);
        Assert.Equal("bob", created!.UserName);
        Assert.Equal("bob@example.com", created.Email);
        Assert.Equal("Bob B", created.FullName);
        Assert.True(created.EmailConfirmed);
        Assert.True(created.IsActive);

        // The generated password is never used, but it must still be long and
        // drawn from the documented alphabet.
        Assert.NotNull(password);
        Assert.Equal(32, password!.Length);
        Assert.All(password, ch => Assert.Contains(ch, PasswordAlphabet));

        _userManager.Verify(u => u.AddToRoleAsync(created, "User"), Times.Once);
        _userManager.Verify(
            u => u.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()),
            Times.Never);
        Assert.Equal(created.Id, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("bob@example.com", result.Principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(new[] { "User" }, result.Principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task Authenticate_UnknownUserWithoutOptionalHeaders_IsStillProvisioned()
    {
        ApplicationUser? created = null;
        _userManager.Setup(u => u.FindByNameAsync("bob")).ReturnsAsync((ApplicationUser?)null);
        _userManager
            .Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .Callback((ApplicationUser user, string _) => created = user)
            .ReturnsAsync(IdentityResult.Success);

        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-User", "bob")));

        // Email and FullName are deliberately not asserted: the handler's "@authelia.local" and
        // username fallbacks never apply because a missing header reads as "" (reported as a bug).
        Assert.True(result.Succeeded);
        Assert.NotNull(created);
        Assert.Equal("bob", created!.UserName);
        Assert.True(created.EmailConfirmed);
        Assert.Equal("bob", result.Principal!.Identity!.Name);
        Assert.Equal(new[] { "User" }, result.Principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task Authenticate_WhenUserCreationFails_FailsAuthentication()
    {
        _userManager.Setup(u => u.FindByNameAsync("bob")).ReturnsAsync((ApplicationUser?)null);
        _userManager
            .Setup(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(
                new IdentityError { Code = "DuplicateEmail", Description = "Email taken" },
                new IdentityError { Code = "InvalidUserName", Description = "Bad name" }));

        var result = await AuthenticateAsync(Enabled(), CreateContext(
            ("Remote-User", "bob"),
            ("Remote-Email", "bob@example.com")));

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to create or retrieve user", result.Failure!.Message);
        _userManager.Verify(u => u.AddToRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WhenTheUserStoreThrows_FailsAuthentication()
    {
        _userManager
            .Setup(u => u.FindByNameAsync("alice"))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var result = await AuthenticateAsync(Enabled(), CreateContext(("Remote-User", "alice")));

        Assert.False(result.Succeeded);
        Assert.Equal("Failed to create or retrieve user", result.Failure!.Message);
    }

    [Fact]
    public async Task Authenticate_MemberOfAnAdminGroup_IsPromotedToAdmin()
    {
        var settings = Enabled();
        settings.AdminGroups = " admins , Super-Users ,";
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A", "User");

        var result = await AuthenticateAsync(settings, CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Groups", "readers, SUPER-USERS ,")));

        Assert.True(result.Succeeded);
        _userManager.Verify(
            u => u.RemoveFromRolesAsync(existing, It.Is<IEnumerable<string>>(r => r.SequenceEqual(new[] { "User" }))),
            Times.Once);
        _userManager.Verify(u => u.AddToRoleAsync(existing, "Admin"), Times.Once);
        Assert.Equal(new[] { "Admin" }, RolesOf(existing));

        var principal = result.Principal!;
        Assert.Equal(new[] { "Admin" }, principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
        Assert.Equal(
            new[] { "readers", "SUPER-USERS" },
            principal.FindAll("authelia_group").Select(c => c.Value));
    }

    [Fact]
    public async Task Authenticate_AdminWhoLeftTheAdminGroups_IsDemotedToTheDefaultRole()
    {
        var settings = Enabled();
        settings.AdminGroups = "admins";
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A", "Admin");

        var result = await AuthenticateAsync(settings, CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Groups", "readers")));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "User" }, RolesOf(existing));
        Assert.Equal(new[] { "User" }, result.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task Authenticate_WithGroupsButNoAdminGroupsConfigured_UsesTheDefaultRole()
    {
        var settings = Enabled();
        settings.AdminGroups = "   ";
        settings.DefaultRole = "ReadOnly";
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A");

        var result = await AuthenticateAsync(settings, CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Groups", "admins")));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "ReadOnly" }, RolesOf(existing));
        _userManager.Verify(
            u => u.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()),
            Times.Never);
        _roleManager.Verify(r => r.RoleExistsAsync("Admin"), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WithOnlySeparatorsInGroupsHeader_AddsNoGroupClaims()
    {
        var settings = Enabled();
        settings.AdminGroups = "admins";
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A", "User");

        var result = await AuthenticateAsync(settings, CreateContext(
            ("Remote-User", "alice"),
            ("Remote-Groups", " , ,")));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "User" }, RolesOf(existing));
        Assert.DoesNotContain(result.Principal!.Claims, c => c.Type == "authelia_group");
    }

    [Fact]
    public async Task Authenticate_WhenTheConfiguredRoleDoesNotExist_FallsBackToUser()
    {
        var settings = Enabled();
        settings.DefaultRole = "Moderator";
        var existing = ExistingUser("alice", email: "alice@example.com", fullName: "Alice A");

        var result = await AuthenticateAsync(settings, CreateContext(("Remote-User", "alice")));

        Assert.True(result.Succeeded);
        _roleManager.Verify(r => r.RoleExistsAsync("Moderator"), Times.Once);
        _userManager.Verify(u => u.AddToRoleAsync(existing, "User"), Times.Once);
        _userManager.Verify(u => u.AddToRoleAsync(existing, "Moderator"), Times.Never);
        Assert.Equal(new[] { "User" }, result.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task ChallengeAndForbid_UseTheStandardStatusCodes()
    {
        var challengeContext = CreateContext();
        var challengeHandler = await CreateHandlerAsync(Enabled(), challengeContext);
        await challengeHandler.ChallengeAsync(new AuthenticationProperties());

        var forbidContext = CreateContext();
        var forbidHandler = await CreateHandlerAsync(Enabled(), forbidContext);
        await forbidHandler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, challengeContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidContext.Response.StatusCode);
    }

    // Requests come from a reverse proxy inside the allowlist; AutheliaTrustedProxyTests covers the allowlist itself.
    private static AutheliaSettings Enabled() => new() { Enabled = true, TrustedProxies = "10.0.0.0/8" };

    private static DefaultHttpContext CreateContext(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }
        return context;
    }

    private ApplicationUser ExistingUser(string userName, string? email, string? fullName, params string[] roles)
    {
        var user = new ApplicationUser
        {
            Id = $"id-{userName}",
            UserName = userName,
            Email = email,
            FullName = fullName
        };
        _rolesByUserName[userName] = roles.ToList();
        _userManager.Setup(u => u.FindByNameAsync(userName)).ReturnsAsync(user);
        return user;
    }

    private List<string> RolesOf(ApplicationUser user)
    {
        if (!_rolesByUserName.TryGetValue(user.UserName!, out var roles))
        {
            roles = new List<string>();
            _rolesByUserName[user.UserName!] = roles;
        }
        return roles;
    }

    private async Task<AutheliaAuthenticationHandler> CreateHandlerAsync(AutheliaSettings settings, HttpContext context)
    {
        var handler = new AutheliaAuthenticationHandler(
            new TestOptionsMonitor<AutheliaAuthenticationOptions>(new AutheliaAuthenticationOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            Options.Create(settings),
            _userManager.Object,
            _roleManager.Object,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>());

        await handler.InitializeAsync(
            new AuthenticationScheme(SchemeName, displayName: null, typeof(AutheliaAuthenticationHandler)),
            context);
        return handler;
    }

    private async Task<AuthenticateResult> AuthenticateAsync(AutheliaSettings settings, HttpContext context)
    {
        var handler = await CreateHandlerAsync(settings, context);
        return await handler.AuthenticateAsync();
    }
}
