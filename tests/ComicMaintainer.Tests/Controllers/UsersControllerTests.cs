using System.Net;
using System.Reflection;
using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// <see cref="UsersController"/> is a thin layer over <see cref="IUserAdministrationService"/>: it says who is acting
/// and how (their password, whether through Authelia), and maps each outcome to its status code with an error.
/// </summary>
public class UsersControllerTests
{
    private static readonly UserAccountSummary Account = new(
        "target-id", "reader", null, "reader@example.test", "User", true, null, DateTime.UtcNow, null, false, false);

    private readonly Mock<IUserAdministrationService> _service = new(MockBehavior.Strict);
    private readonly UsersController _controller;

    public UsersControllerTests()
    {
        _controller = new UsersController(_service.Object, ClientAddressResolver.Direct);
        SignIn("admin-id", "admin");
    }

    [Fact]
    public void TheWholeControllerIsForAdministrators()
    {
        var authorize = typeof(UsersController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(AuthorizationPolicies.CanAdminister, authorize?.Policy);
        Assert.Equal("api/users", typeof(UsersController).GetCustomAttribute<RouteAttribute>()?.Template);
    }

    [Theory]
    [InlineData(nameof(UsersController.Create))]
    [InlineData(nameof(UsersController.ChangeRole))]
    [InlineData(nameof(UsersController.ResetPassword))]
    [InlineData(nameof(UsersController.SetActive))]
    [InlineData(nameof(UsersController.Delete))]
    public void EveryChangeNamesWhatFailed(string action)
    {
        var message = typeof(UsersController).GetMethod(action)!.GetCustomAttribute<FailureMessageAttribute>();

        Assert.NotNull(message);
        Assert.EndsWith(".", message.Message);
    }

    [Fact]
    public async Task List_AsksForTheCallersView()
    {
        var list = new UserAccountList(false, [Account]);
        _service.Setup(s => s.ListAsync("admin-id", It.IsAny<CancellationToken>())).ReturnsAsync(list);

        var result = await _controller.List(CancellationToken.None);

        Assert.Same(list, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Create_PassesTheAccountAndTheAdministratorsPassword_AndAnswers201()
    {
        UserAdministrationActor? actor = null;
        NewUserAccount? account = null;
        _service
            .Setup(s => s.CreateAsync(It.IsAny<UserAdministrationActor>(), It.IsAny<NewUserAccount>()))
            .Callback<UserAdministrationActor, NewUserAccount>((a, n) => (actor, account) = (a, n))
            .ReturnsAsync(new UserAdministrationResult(UserAdministrationOutcome.Created, null, Account));

        var result = await _controller.Create(new CreateUserRequest("reader", "N3wPassw0rd", "reader@example.test", "Rita", "User", "Adm1nPassword"));

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Same(Account, created.Value);
        Assert.Equal(new NewUserAccount("reader", "N3wPassw0rd", "reader@example.test", "Rita", "User"), account);
        Assert.Equal("admin-id", actor!.UserId);
        Assert.Equal("admin", actor.Username);
        Assert.Equal("Adm1nPassword", actor.CurrentPassword);
        Assert.False(actor.SignedInThroughAuthelia);
    }

    [Fact]
    public async Task AnAutheliaSession_IsMarkedSoItIsNotAskedForAPassword()
    {
        SignIn("admin-id", "admin", AutheliaAuthenticationHandler.AuthMethod);
        UserAdministrationActor? actor = null;
        _service
            .Setup(s => s.SetActiveAsync(It.IsAny<UserAdministrationActor>(), "target-id", true))
            .Callback<UserAdministrationActor, string, bool>((a, _, _) => actor = a)
            .ReturnsAsync(new UserAdministrationResult(UserAdministrationOutcome.Succeeded, null, Account));

        var result = await _controller.SetActive("target-id", new SetUserActiveRequest(true));

        Assert.Same(Account, Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(actor!.SignedInThroughAuthelia);
        Assert.Null(actor.CurrentPassword);
    }

    [Theory]
    [InlineData(UserAdministrationOutcome.Invalid, HttpStatusCode.BadRequest)]
    [InlineData(UserAdministrationOutcome.NotFound, HttpStatusCode.NotFound)]
    [InlineData(UserAdministrationOutcome.Conflict, HttpStatusCode.Conflict)]
    [InlineData(UserAdministrationOutcome.PasswordNotConfirmed, HttpStatusCode.Forbidden)]
    public async Task EveryRefusalAnswersWithItsStatusAndError(UserAdministrationOutcome outcome, HttpStatusCode status)
    {
        var refusal = new UserAdministrationResult(outcome, "the reason");
        _service.Setup(s => s.ChangeRoleAsync(It.IsAny<UserAdministrationActor>(), "target-id", "Admin")).ReturnsAsync(refusal);
        _service.Setup(s => s.ResetPasswordAsync(It.IsAny<UserAdministrationActor>(), "target-id", "N3wPassw0rd")).ReturnsAsync(refusal);
        _service.Setup(s => s.SetActiveAsync(It.IsAny<UserAdministrationActor>(), "target-id", false)).ReturnsAsync(refusal);
        _service.Setup(s => s.DeleteAsync(It.IsAny<UserAdministrationActor>(), "target-id")).ReturnsAsync(refusal);
        _service.Setup(s => s.CreateAsync(It.IsAny<UserAdministrationActor>(), It.IsAny<NewUserAccount>())).ReturnsAsync(refusal);

        ActionResult[] results =
        [
            await _controller.Create(new CreateUserRequest("reader", "N3wPassw0rd")),
            await _controller.ChangeRole("target-id", new ChangeUserRoleRequest("Admin", "Adm1nPassword")),
            await _controller.ResetPassword("target-id", new ResetUserPasswordRequest("N3wPassw0rd", "Adm1nPassword")),
            await _controller.SetActive("target-id", new SetUserActiveRequest(false)),
            await _controller.Delete("target-id"),
        ];

        var answers = results.Select(result => Assert.IsAssignableFrom<ObjectResult>(result)).ToList();
        Assert.All(answers, answer =>
        {
            Assert.Equal((int)status, answer.StatusCode);
            Assert.Equal("the reason", answer.Value?.GetType().GetProperty("error")?.GetValue(answer.Value));
        });
    }

    [Fact]
    public async Task Delete_AnswersNoContent()
    {
        UserAdministrationActor? actor = null;
        _service
            .Setup(s => s.DeleteAsync(It.IsAny<UserAdministrationActor>(), "target-id"))
            .Callback<UserAdministrationActor, string>((a, _) => actor = a)
            .ReturnsAsync(new UserAdministrationResult(UserAdministrationOutcome.Succeeded));

        Assert.IsType<NoContentResult>(await _controller.Delete("target-id"));
        Assert.Null(actor!.CurrentPassword);
    }

    [Fact]
    public async Task SetActive_WithoutSayingWhich_IsABadRequest()
    {
        var result = await _controller.SetActive("target-id", new SetUserActiveRequest(null, "Adm1nPassword"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value?.GetType().GetProperty("error")?.GetValue(badRequest.Value));
    }

    [Fact]
    public async Task ACallerWithoutAnAccountId_IsUnauthorized()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } };

        Assert.IsType<UnauthorizedResult>((await _controller.List(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await _controller.Create(new CreateUserRequest("reader", "N3wPassw0rd")));
        Assert.IsType<UnauthorizedResult>(await _controller.ChangeRole("target-id", new ChangeUserRoleRequest("User")));
        Assert.IsType<UnauthorizedResult>(await _controller.ResetPassword("target-id", new ResetUserPasswordRequest("N3wPassw0rd")));
        Assert.IsType<UnauthorizedResult>(await _controller.SetActive("target-id", new SetUserActiveRequest(true)));
        Assert.IsType<UnauthorizedResult>(await _controller.Delete("target-id"));
    }

    private void SignIn(string userId, string username, string? authMethod = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Name, username) };
        if (authMethod is not null)
        {
            claims.Add(new Claim("auth_method", authMethod));
        }

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
    }
}
