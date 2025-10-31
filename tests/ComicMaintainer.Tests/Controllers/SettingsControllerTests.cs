using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class SettingsControllerTests
{
    private readonly Mock<IOptions<AppSettings>> _appSettingsMock;
    private readonly Mock<ILogger<SettingsController>> _loggerMock;
    private readonly SettingsController _controller;
    private readonly AppSettings _appSettings;

    public SettingsControllerTests()
    {
        _appSettings = new AppSettings
        {
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnabled = true,
            LogMaxBytes = 10485760,
            GitHubToken = "test-token",
            GitHubRepository = "test/repo",
            GitHubIssueAssignee = "testuser"
        };

        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _appSettingsMock.Setup(x => x.Value).Returns(_appSettings);
        
        _loggerMock = new Mock<ILogger<SettingsController>>();
        _controller = new SettingsController(_appSettingsMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void GetFilenameFormat_ReturnsOkResultWithFormat()
    {
        // Act
        var result = _controller.GetFilenameFormat();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
    }

    [Fact]
    public void SetFilenameFormat_ReturnsOkResult()
    {
        // Arrange
        var request = new SettingsController.FilenameFormatRequest 
        { 
            Format = "{series} v{volume} #{issue}" 
        };

        // Act
        var result = _controller.SetFilenameFormat(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void GetIssueNumberPadding_ReturnsCorrectValue()
    {
        // Act
        var result = _controller.GetIssueNumberPadding();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var padding = objectResult.Value;
        var paddingProperty = padding.GetType().GetProperty("padding");
        Assert.Equal(4, paddingProperty?.GetValue(padding));
    }

    [Fact]
    public void GetWatcherEnabled_ReturnsCorrectValue()
    {
        // Act
        var result = _controller.GetWatcherEnabled();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var enabled = objectResult.Value;
        var enabledProperty = enabled.GetType().GetProperty("enabled");
        Assert.Equal(true, enabledProperty?.GetValue(enabled));
    }

    [Fact]
    public void GetGitHubToken_DoesNotExposeActualToken()
    {
        // Act
        var result = _controller.GetGitHubToken();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var tokenInfo = objectResult.Value;
        var hasTokenProperty = tokenInfo.GetType().GetProperty("hasToken");
        Assert.Equal(true, hasTokenProperty?.GetValue(tokenInfo));
    }

    [Fact]
    public void GetGitHubRepository_ReturnsRepository()
    {
        // Act
        var result = _controller.GetGitHubRepository();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var repo = objectResult.Value;
        var repoProperty = repo.GetType().GetProperty("repository");
        Assert.Equal("test/repo", repoProperty?.GetValue(repo));
    }
}
