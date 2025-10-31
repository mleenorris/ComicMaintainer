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
}
