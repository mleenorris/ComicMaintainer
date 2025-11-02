using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class PreferencesControllerTests
{
    private readonly Mock<ILogger<PreferencesController>> _loggerMock;
    private readonly PreferencesController _controller;

    public PreferencesControllerTests()
    {
        _loggerMock = new Mock<ILogger<PreferencesController>>();
        _controller = new PreferencesController(_loggerMock.Object);
    }

    [Fact]
    public void GetPreferences_ReturnsOkResultWithDefaultPreferences()
    {
        // Act
        var result = _controller.GetPreferences();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var preferences = objectResult.Value;
        var themeProperty = preferences.GetType().GetProperty("theme");
        Assert.NotNull(themeProperty);
        Assert.Equal("dark", themeProperty.GetValue(preferences));
    }

    [Fact]
    public void SavePreferences_ReturnsOkResult()
    {
        // Arrange
        var preferences = new { theme = "light", perPage = 50 };

        // Act
        var result = _controller.SavePreferences(preferences);

        // Assert
        Assert.IsType<OkResult>(result);
    }

    // New RESTful endpoint tests

    [Fact]
    public void UpdatePreferences_WithValidPreferences_ReturnsOkWithMessage()
    {
        // Arrange
        var request = new PreferencesController.PreferencesRequest
        {
            Theme = "light",
            PerPage = 50,
            FilenameFormat = "{series} #{issue}",
            IssueNumberPadding = 3,
            WatcherEnabled = false
        };

        // Act
        var result = _controller.UpdatePreferences(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var message = okResult.Value.GetType().GetProperty("message");
        Assert.NotNull(message);
        Assert.Equal("Preferences updated successfully", message.GetValue(okResult.Value));
    }

    [Fact]
    public void UpdatePreferences_WithPartialPreferences_ReturnsOk()
    {
        // Arrange - Only update theme and perPage
        var request = new PreferencesController.PreferencesRequest
        {
            Theme = "light",
            PerPage = 75
        };

        // Act
        var result = _controller.UpdatePreferences(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void UpdatePreferences_WithEmptyPreferences_ReturnsOk()
    {
        // Arrange - No preferences set (all null/default)
        var request = new PreferencesController.PreferencesRequest();

        // Act
        var result = _controller.UpdatePreferences(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }
}
