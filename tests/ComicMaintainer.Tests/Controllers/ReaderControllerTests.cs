using System.Security.Claims;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class ReaderControllerTests
{
    private readonly Mock<IReadingProgressService> _progressMock;
    private readonly Mock<IReaderPreferenceService> _preferenceMock;
    private readonly Mock<IReadingSessionService> _sessionMock;
    private readonly Mock<ILogger<ReaderController>> _loggerMock;
    private readonly ReaderController _controller;

    private const string TestUserId = "user-abc";

    public ReaderControllerTests()
    {
        _progressMock = new Mock<IReadingProgressService>();
        _preferenceMock = new Mock<IReaderPreferenceService>();
        _sessionMock = new Mock<IReadingSessionService>();
        _loggerMock = new Mock<ILogger<ReaderController>>();

        _controller = new ReaderController(
            _progressMock.Object,
            _preferenceMock.Object,
            _sessionMock.Object,
            _loggerMock.Object);

        SetAuthenticatedUser(TestUserId);
    }

    // ─── GetProgress ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProgress_ExistingProgress_ReturnsOk()
    {
        var expected = new ReadingProgress { UserId = TestUserId, ContentId = "c1", CurrentPage = 5, TotalPages = 10 };
        _progressMock.Setup(s => s.GetProgressAsync(TestUserId, "c1", default)).ReturnsAsync(expected);

        var result = await _controller.GetProgress("c1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(expected, ok.Value);
    }

    [Fact]
    public async Task GetProgress_NoProgress_ReturnsNotFound()
    {
        _progressMock.Setup(s => s.GetProgressAsync(TestUserId, "c1", default)).ReturnsAsync((ReadingProgress?)null);

        var result = await _controller.GetProgress("c1");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── PutProgress ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PutProgress_ValidRequest_ReturnsNoContent()
    {
        _progressMock.Setup(s => s.GetProgressAsync(TestUserId, "c1", default)).ReturnsAsync((ReadingProgress?)null);
        _progressMock.Setup(s => s.SaveProgressAsync(It.IsAny<ReadingProgress>(), default)).Returns(Task.CompletedTask);

        var request = new ReaderController.PutProgressRequest { CurrentPage = 3, TotalPages = 10 };
        var result = await _controller.PutProgress("c1", request);

        Assert.IsType<NoContentResult>(result);
        _progressMock.Verify(s => s.SaveProgressAsync(It.IsAny<ReadingProgress>(), default), Times.Once);
    }

    [Fact]
    public async Task PutProgress_ZeroTotalPages_ReturnsBadRequest()
    {
        var request = new ReaderController.PutProgressRequest { CurrentPage = 1, TotalPages = 0 };
        var result = await _controller.PutProgress("c1", request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutProgress_CurrentPageExceedsTotalPages_ReturnsBadRequest()
    {
        var request = new ReaderController.PutProgressRequest { CurrentPage = 15, TotalPages = 10 };
        var result = await _controller.PutProgress("c1", request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutProgress_ExistingProgress_UpdatesAndSaves()
    {
        var existing = new ReadingProgress
        {
            UserId = TestUserId,
            ContentId = "c1",
            CurrentPage = 2,
            TotalPages = 10,
            PercentComplete = 20.0,
            LastReadAt = DateTime.UtcNow
        };
        _progressMock.Setup(s => s.GetProgressAsync(TestUserId, "c1", default)).ReturnsAsync(existing);
        _progressMock.Setup(s => s.SaveProgressAsync(It.IsAny<ReadingProgress>(), default)).Returns(Task.CompletedTask);

        var request = new ReaderController.PutProgressRequest { CurrentPage = 7, TotalPages = 10 };
        var result = await _controller.PutProgress("c1", request);

        Assert.IsType<NoContentResult>(result);
        _progressMock.Verify(s => s.SaveProgressAsync(It.Is<ReadingProgress>(p => p.CurrentPage == 7), default), Times.Once);
    }

    // ─── GetPreferences ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPreferences_ReturnsDefaultsWhenNoneSaved()
    {
        var defaults = new ReaderPreferences { UserId = TestUserId };
        _preferenceMock.Setup(s => s.GetPreferencesAsync(TestUserId, default)).ReturnsAsync(defaults);

        var result = await _controller.GetPreferences();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(defaults, ok.Value);
    }

    // ─── PutPreferences ───────────────────────────────────────────────────────

    [Fact]
    public async Task PutPreferences_ValidRequest_ReturnsNoContent()
    {
        _preferenceMock.Setup(s => s.SavePreferencesAsync(It.IsAny<ReaderPreferences>(), default)).Returns(Task.CompletedTask);

        var prefs = new ReaderPreferences { DefaultReaderMode = ReaderMode.Longstrip };
        var result = await _controller.PutPreferences(prefs);

        Assert.IsType<NoContentResult>(result);
        _preferenceMock.Verify(s => s.SavePreferencesAsync(It.Is<ReaderPreferences>(p => p.UserId == TestUserId), default), Times.Once);
    }

    // ─── StartSession ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StartSession_ValidRequest_ReturnsOkWithSession()
    {
        var session = new ReadingSession { SessionId = Guid.NewGuid(), UserId = TestUserId, ContentId = "c1", StartPage = 1 };
        _sessionMock.Setup(s => s.StartSessionAsync(TestUserId, "c1", 1, false, default)).ReturnsAsync(session);

        var request = new ReaderController.StartSessionRequest { ContentId = "c1", StartPage = 1 };
        var result = await _controller.StartSession(request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(session, ok.Value);
    }

    [Fact]
    public async Task StartSession_EmptyContentId_ReturnsBadRequest()
    {
        var request = new ReaderController.StartSessionRequest { ContentId = " ", StartPage = 1 };
        var result = await _controller.StartSession(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task StartSession_ZeroStartPage_ReturnsBadRequest()
    {
        var request = new ReaderController.StartSessionRequest { ContentId = "c1", StartPage = 0 };
        var result = await _controller.StartSession(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── EndSession ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EndSession_ValidSession_ReturnsOkWithSession()
    {
        var sessionId = Guid.NewGuid();
        var session = new ReadingSession { SessionId = sessionId, EndPage = 8, EndedAt = DateTime.UtcNow };
        _sessionMock.Setup(s => s.EndSessionAsync(sessionId, 8, default)).ReturnsAsync(session);

        var request = new ReaderController.EndSessionRequest { SessionId = sessionId, EndPage = 8 };
        var result = await _controller.EndSession(request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(session, ok.Value);
    }

    [Fact]
    public async Task EndSession_UnknownSession_ReturnsNotFound()
    {
        var sessionId = Guid.NewGuid();
        _sessionMock.Setup(s => s.EndSessionAsync(sessionId, It.IsAny<int>(), default)).ReturnsAsync((ReadingSession?)null);

        var request = new ReaderController.EndSessionRequest { SessionId = sessionId, EndPage = 5 };
        var result = await _controller.EndSession(request);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task EndSession_ZeroEndPage_ReturnsBadRequest()
    {
        var request = new ReaderController.EndSessionRequest { SessionId = Guid.NewGuid(), EndPage = 0 };
        var result = await _controller.EndSession(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── Unauthenticated fallback ─────────────────────────────────────────────

    [Fact]
    public async Task GetProgress_NoUserClaim_ReturnsUnauthorized()
    {
        SetUnauthenticatedUser();
        var result = await _controller.GetProgress("c1");
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SetAuthenticatedUser(string userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private void SetUnauthenticatedUser()
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
        };
    }
}
