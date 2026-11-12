using System.Security.Claims;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The overview page is per user (Continue Reading), so the controller must
/// refuse callers without an identity and let service failures reach the
/// global exception handler.
/// </summary>
public class OverviewControllerTests
{
    private readonly Mock<IOverviewService> _overview = new();

    [Fact]
    public async Task Get_ReturnsTheOverviewOfTheCallingUser()
    {
        var overview = new OverviewResult();
        _overview.Setup(o => o.GetOverviewAsync("user-7", It.IsAny<CancellationToken>())).ReturnsAsync(overview);

        var result = await CreateController(new Claim(ClaimTypes.NameIdentifier, "user-7")).Get();

        Assert.Same(overview, Assert.IsType<OkObjectResult>(result.Result).Value);
        _overview.Verify(o => o.GetOverviewAsync("user-7", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_WithoutAUserIdIsUnauthorized()
    {
        var result = await CreateController(new Claim(ClaimTypes.Name, "someone")).Get();

        Assert.IsType<UnauthorizedResult>(result.Result);
        _overview.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_WithAnEmptyUserIdIsUnauthorized()
    {
        var result = await CreateController(new Claim(ClaimTypes.NameIdentifier, string.Empty)).Get();

        Assert.IsType<UnauthorizedResult>(result.Result);
        _overview.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_ServiceFailureEscapes()
    {
        var failure = new InvalidOperationException("SELECT failed: secret connection detail");
        _overview.Setup(o => o.GetOverviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateController(new Claim(ClaimTypes.NameIdentifier, "user-7")).Get());

        Assert.Same(failure, thrown);
    }

    private OverviewController CreateController(params Claim[] claims) =>
        new(_overview.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
}
