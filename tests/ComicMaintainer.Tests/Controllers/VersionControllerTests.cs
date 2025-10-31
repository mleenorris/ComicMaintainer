using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.Tests.Controllers;

public class VersionControllerTests
{
    private readonly VersionController _controller;

    public VersionControllerTests()
    {
        _controller = new VersionController();
    }

    [Fact]
    public void GetVersion_ReturnsOkResultWithVersionInfo()
    {
        // Act
        var result = _controller.GetVersion();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.NotNull(objectResult.Value);
        
        var version = objectResult.Value;
        var versionProperty = version.GetType().GetProperty("version");
        var platformProperty = version.GetType().GetProperty("platform");
        
        Assert.NotNull(versionProperty);
        Assert.NotNull(platformProperty);
        Assert.Equal(".NET", platformProperty.GetValue(version));
    }

    [Fact]
    public void GetVersion_ReturnsValidVersionFormat()
    {
        // Act
        var result = _controller.GetVersion();
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var version = objectResult.Value;
        var versionProperty = version.GetType().GetProperty("version");
        var versionValue = versionProperty?.GetValue(version)?.ToString();

        // Assert
        Assert.NotNull(versionValue);
        Assert.Matches(@"^\d+\.\d+\.\d+", versionValue);
    }
}
