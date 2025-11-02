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

        // Assert - Returns OkObjectResult with message about read-only settings
        Assert.IsType<OkObjectResult>(result);
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
        Assert.NotNull(padding);
        var paddingProperty = padding.GetType().GetProperty("padding");
        Assert.NotNull(paddingProperty);
        Assert.Equal(4, paddingProperty.GetValue(padding));
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
        Assert.NotNull(enabled);
        var enabledProperty = enabled.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.Equal(true, enabledProperty.GetValue(enabled));
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
        Assert.NotNull(tokenInfo);
        var hasTokenProperty = tokenInfo.GetType().GetProperty("hasToken");
        Assert.NotNull(hasTokenProperty);
        Assert.Equal(true, hasTokenProperty.GetValue(tokenInfo));
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
        Assert.NotNull(repo);
        var repoProperty = repo.GetType().GetProperty("repository");
        Assert.NotNull(repoProperty);
        Assert.Equal("test/repo", repoProperty.GetValue(repo));
    }

    // New RESTful endpoint tests

    [Fact]
    public void GetAllSettings_ReturnsAllSettings()
    {
        // Act
        var result = _controller.GetAllSettings();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var settings = objectResult.Value;
        var filenameFormatProp = settings.GetType().GetProperty("filename_format");
        Assert.NotNull(filenameFormatProp);
        Assert.Equal("{series} - Chapter {issue}", filenameFormatProp.GetValue(settings));
    }

    [Fact]
    public void UpdateFilenameFormat_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.FilenameFormatRequest 
        { 
            Format = "{series} v{volume} #{issue}" 
        };

        // Act
        var result = _controller.UpdateFilenameFormat(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void UpdateIssueNumberPadding_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.IssueNumberPaddingRequest { Padding = 3 };

        // Act
        var result = _controller.UpdateIssueNumberPadding(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void UpdateWatcherEnabled_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.WatcherEnabledRequest { Enabled = false };

        // Act
        var result = _controller.UpdateWatcherEnabled(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void UpdateLogMaxBytes_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.LogMaxBytesRequest { MaxBytes = 20971520 };

        // Act
        var result = _controller.UpdateLogMaxBytes(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void UpdateGitHubToken_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.GitHubTokenRequest { Token = "new-token" };

        // Act
        var result = _controller.UpdateGitHubToken(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void UpdateGitHubRepository_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.GitHubRepositoryRequest { Repository = "newowner/newrepo" };

        // Act
        var result = _controller.UpdateGitHubRepository(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void UpdateGitHubIssueAssignee_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.GitHubIssueAssigneeRequest { Assignee = "newuser" };

        // Act
        var result = _controller.UpdateGitHubIssueAssignee(request);

        // Assert
        Assert.IsType<OkResult>(result);
    }
}
