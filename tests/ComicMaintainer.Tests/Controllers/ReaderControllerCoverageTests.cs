using System.Security.Claims;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Every reader endpoint is per user: a caller without a user id must be
/// refused before any progress, preference or session is read or written.
/// </summary>
public class ReaderControllerCoverageTests
{
    private readonly Mock<IReadingProgressService> _progress = new();
    private readonly Mock<IReaderPreferenceService> _preferences = new();
    private readonly Mock<IReadingSessionService> _sessions = new();
    private readonly ReaderController _controller;

    public ReaderControllerCoverageTests()
    {
        _controller = new ReaderController(
            _progress.Object,
            _preferences.Object,
            _sessions.Object,
            Mock.Of<ILogger<ReaderController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    // Authenticated, but without a NameIdentifier claim.
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "guest") }, "Test"))
                }
            }
        };
    }

    [Fact]
    public async Task PutProgress_WithoutUserIdIsUnauthorized()
    {
        var result = await _controller.PutProgress("c1", new ReaderController.PutProgressRequest { CurrentPage = 1, TotalPages = 10 });

        Assert.IsType<UnauthorizedResult>(result);
        VerifyNoServiceCalls();
    }

    [Fact]
    public async Task GetPreferences_WithoutUserIdIsUnauthorized()
    {
        var result = await _controller.GetPreferences();

        Assert.IsType<UnauthorizedResult>(result.Result);
        VerifyNoServiceCalls();
    }

    [Fact]
    public async Task PutPreferences_WithoutUserIdIsUnauthorized()
    {
        var preferences = new ReaderPreferences { UserId = "someone-else" };

        var result = await _controller.PutPreferences(preferences);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal("someone-else", preferences.UserId);
        VerifyNoServiceCalls();
    }

    [Fact]
    public async Task StartSession_WithoutUserIdIsUnauthorized()
    {
        var result = await _controller.StartSession(new ReaderController.StartSessionRequest { ContentId = "c1" });

        Assert.IsType<UnauthorizedResult>(result.Result);
        VerifyNoServiceCalls();
    }

    [Fact]
    public async Task EndSession_WithoutUserIdIsUnauthorized()
    {
        var result = await _controller.EndSession(new ReaderController.EndSessionRequest { SessionId = Guid.NewGuid(), EndPage = 4 });

        Assert.IsType<UnauthorizedResult>(result.Result);
        VerifyNoServiceCalls();
    }

    private void VerifyNoServiceCalls()
    {
        _progress.VerifyNoOtherCalls();
        _preferences.VerifyNoOtherCalls();
        _sessions.VerifyNoOtherCalls();
    }
}
