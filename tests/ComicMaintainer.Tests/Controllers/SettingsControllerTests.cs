using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class SettingsControllerTests
{
    private const int BYTES_PER_MB = 1048576;
    
    private readonly Mock<IOptions<AppSettings>> _appSettingsMock;
    private readonly Mock<ILogger<SettingsController>> _loggerMock;
    private readonly Mock<IDbContextFactory<ComicMaintainer.Core.Data.ComicMaintainerDbContext>> _dbContextFactoryMock;
    private readonly Mock<IComicProcessorService> _processorServiceMock;
    private readonly Mock<IFileStoreService> _fileStoreMock;
    private readonly Mock<ISettingsService> _settingsServiceMock;
    private readonly Mock<IHostApplicationLifetime> _applicationLifetimeMock;
    private readonly SettingsController _controller;
    private readonly AppSettings _appSettings;

    public SettingsControllerTests()
    {
        _appSettings = new AppSettings
        {
            FilenameFormat = "{series} - Chapter {issue}",
            IssueNumberPadding = 4,
            WatcherEnableRename = true,
            WatcherEnableNormalize = true,
            LogMaxBytes = 10485760,
            DatabaseCleanupIntervalHours = 12
        };

        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _appSettingsMock.Setup(x => x.Value).Returns(_appSettings);
        
        _loggerMock = new Mock<ILogger<SettingsController>>();
        _dbContextFactoryMock = new Mock<IDbContextFactory<ComicMaintainer.Core.Data.ComicMaintainerDbContext>>();
        _processorServiceMock = new Mock<IComicProcessorService>();
        _fileStoreMock = new Mock<IFileStoreService>();
        _settingsServiceMock = new Mock<ISettingsService>();
        _applicationLifetimeMock = new Mock<IHostApplicationLifetime>();
        
        _controller = new SettingsController(
            _appSettingsMock.Object, 
            _loggerMock.Object,
            _dbContextFactoryMock.Object,
            _processorServiceMock.Object,
            _fileStoreMock.Object,
            _settingsServiceMock.Object,
            _applicationLifetimeMock.Object);
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
        
        // Verify format property
        var formatProperty = objectResult.Value?.GetType().GetProperty("format");
        Assert.NotNull(formatProperty);
        Assert.Equal("{series} - Chapter {issue}", formatProperty.GetValue(objectResult.Value));
        
        // Verify default property is included
        var defaultProperty = objectResult.Value?.GetType().GetProperty("default");
        Assert.NotNull(defaultProperty);
        Assert.Equal("{series} - Chapter {issue}", defaultProperty.GetValue(objectResult.Value));
    }

    [Fact]
    public async Task SetFilenameFormat_ReturnsOkResult()
    {
        // Arrange
        var request = new SettingsController.FilenameFormatRequest 
        { 
            Format = "{series} v{volume} #{issue}" 
        };

        // Act
        var result = await _controller.SetFilenameFormat(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateFilenameFormatAsync(request.Format, It.IsAny<CancellationToken>()), Times.Once);
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

    // GetWatcherEnabled removed - watcher is now enabled when rename or normalize is enabled
    
    [Fact]
    public void GetWatcherEnableRename_ReturnsCorrectValue()
    {
        // Act
        var result = _controller.GetWatcherEnableRename();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var enabled = objectResult.Value;
        Assert.NotNull(enabled);
        var enabledProperty = enabled.GetType().GetProperty("enabled");
        Assert.NotNull(enabledProperty);
        Assert.Equal(true, enabledProperty.GetValue(enabled));
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
    public async Task UpdateFilenameFormat_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.FilenameFormatRequest 
        { 
            Format = "{series} v{volume} #{issue}" 
        };

        // Act
        var result = await _controller.UpdateFilenameFormat(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateFilenameFormatAsync(request.Format, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateIssueNumberPadding_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.IssueNumberPaddingRequest { Padding = 3 };

        // Act
        var result = await _controller.UpdateIssueNumberPadding(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateIssueNumberPaddingAsync(request.Padding, It.IsAny<CancellationToken>()), Times.Once);
    }

    // UpdateWatcherEnabled removed - use UpdateWatcherEnableRename and UpdateWatcherEnableNormalize instead

    [Fact]
    public void GetLogMaxBytes_ReturnsMaxMB()
    {
        // Arrange - AppSettings has LogMaxBytes = 10485760 (10 MB)
        
        // Act
        var result = _controller.GetLogMaxBytes();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = okResult.Value;
        
        // Use reflection to get the anonymous type property
        var maxMBProperty = value?.GetType().GetProperty("maxMB");
        Assert.NotNull(maxMBProperty);
        
        var maxMB = maxMBProperty.GetValue(value);
        Assert.Equal(10.0, maxMB); // 10485760 bytes = 10 MB
    }

    [Fact]
    public async Task UpdateLogMaxBytes_ReturnsOk()
    {
        // Arrange
        const int requestedMB = 20;
        const int expectedBytes = requestedMB * BYTES_PER_MB;
        var request = new SettingsController.LogMaxBytesRequest { MaxMB = requestedMB };

        // Act
        var result = await _controller.UpdateLogMaxBytes(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateLogMaxBytesAsync(expectedBytes, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateLogMaxBytes_InvalidValue_ReturnsBadRequest()
    {
        // Arrange - Test with value that's too large
        var request = new SettingsController.LogMaxBytesRequest { MaxMB = 3000 };

        // Act
        var result = await _controller.UpdateLogMaxBytes(request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateLogMaxBytesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLogMaxBytes_ZeroValue_ReturnsBadRequest()
    {
        // Arrange
        var request = new SettingsController.LogMaxBytesRequest { MaxMB = 0 };

        // Act
        var result = await _controller.UpdateLogMaxBytes(request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateLogMaxBytesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RestartApplication_ReturnsOk()
    {
        // Arrange - Set up a mock user context
        var claims = new List<System.Security.Claims.Claim>
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "test-user-id"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "testuser"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };

        // Act
        var result = _controller.RestartApplication();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        
        var value = okResult.Value;
        var successProperty = value.GetType().GetProperty("success");
        Assert.NotNull(successProperty);
        Assert.Equal(true, successProperty.GetValue(value));
        
        // Verify that StopApplication would eventually be called (we can't verify the delayed task directly)
        // The test validates that the endpoint returns successfully without throwing
    }

    [Fact]
    public void GetDatabaseCleanupIntervalHours_ReturnsCorrectValue()
    {
        // Arrange
        _appSettings.DatabaseCleanupIntervalHours = 24;
        
        // Act
        var result = _controller.GetDatabaseCleanupIntervalHours();

        // Assert
        var okResult = Assert.IsType<ActionResult<object>>(result);
        var objectResult = Assert.IsType<OkObjectResult>(okResult.Result);
        
        var hours = objectResult.Value;
        Assert.NotNull(hours);
        var hoursProperty = hours.GetType().GetProperty("hours");
        Assert.NotNull(hoursProperty);
        Assert.Equal(24, hoursProperty.GetValue(hours));
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_ReturnsOk()
    {
        // Arrange
        var request = new SettingsController.DatabaseCleanupIntervalRequest { Hours = 24 };

        // Act
        var result = await _controller.UpdateDatabaseCleanupIntervalHours(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateDatabaseCleanupIntervalHoursAsync(request.Hours, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateDatabaseCleanupIntervalHours_WithZero_ReturnsOk()
    {
        // Arrange - 0 means only run on startup
        var request = new SettingsController.DatabaseCleanupIntervalRequest { Hours = 0 };

        // Act
        var result = await _controller.UpdateDatabaseCleanupIntervalHours(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _settingsServiceMock.Verify(s => s.UpdateDatabaseCleanupIntervalHoursAsync(request.Hours, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanupDatabase_RemovesStaleEntries_ReturnsOk()
    {
        // Arrange
        _fileStoreMock.Setup(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // Simulate 5 stale entries removed

        // Act
        var result = await _controller.CleanupDatabase();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        
        var value = okResult.Value;
        var successProperty = value.GetType().GetProperty("success");
        Assert.NotNull(successProperty);
        Assert.Equal(true, successProperty.GetValue(value));
        
        var removedCountProperty = value.GetType().GetProperty("removedCount");
        Assert.NotNull(removedCountProperty);
        Assert.Equal(5, removedCountProperty.GetValue(value));
        
        _fileStoreMock.Verify(f => f.CleanupStaleEntriesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
