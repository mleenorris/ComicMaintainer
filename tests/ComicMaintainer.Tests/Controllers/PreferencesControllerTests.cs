using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class PreferencesControllerTests
{
    private const string UserId = "user-1";

    private readonly AppSettings _appSettings;
    private readonly Mock<IUserPreferencesService> _preferencesServiceMock;
    private readonly PreferencesController _controller;

    /// <summary>In-memory stand-in for the persisted preference row.</summary>
    private UserPreferences _stored = new() { UserId = UserId };

    public PreferencesControllerTests()
    {
        var loggerMock = new Mock<ILogger<PreferencesController>>();
        _appSettings = new AppSettings();
        var appSettingsMock = new Mock<IOptionsMonitor<AppSettings>>();
        appSettingsMock.Setup(x => x.CurrentValue).Returns(_appSettings);

        _preferencesServiceMock = new Mock<IUserPreferencesService>();
        _preferencesServiceMock
            .Setup(x => x.GetPreferencesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);
        _preferencesServiceMock
            .Setup(x => x.SavePreferencesAsync(It.IsAny<UserPreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPreferences update, CancellationToken _) =>
            {
                // Mirror the service's partial-merge semantics.
                _stored = new UserPreferences
                {
                    UserId = update.UserId,
                    Theme = update.Theme ?? _stored.Theme,
                    PerPage = update.PerPage ?? _stored.PerPage,
                    ReadingMode = update.ReadingMode ?? _stored.ReadingMode,
                    LibraryViewMode = update.LibraryViewMode ?? _stored.LibraryViewMode,
                    FilterMode = update.FilterMode ?? _stored.FilterMode,
                    SortMode = update.SortMode ?? _stored.SortMode
                };
                return _stored;
            });

        _controller = new PreferencesController(
            loggerMock.Object,
            appSettingsMock.Object,
            _preferencesServiceMock.Object);

        SetUser(UserId);
    }

    private void SetUser(string? userId)
    {
        var claims = userId is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, userId) };

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    private static object GetValue(ActionResult<object> result, string property)
    {
        var objectResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(objectResult.Value);
        var prop = objectResult.Value!.GetType().GetProperty(property);
        Assert.NotNull(prop);
        return prop!.GetValue(objectResult.Value)!;
    }

    private static object? GetValueOrNull(ActionResult<object> result, string property)
    {
        var objectResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(objectResult.Value);
        var prop = objectResult.Value!.GetType().GetProperty(property);
        Assert.NotNull(prop);
        return prop!.GetValue(objectResult.Value);
    }

    [Fact]
    public async Task GetPreferences_WithoutStoredValues_ReturnsDefaults()
    {
        var result = await _controller.GetPreferences();

        // Theme is null when unset so the client can use the OS colour scheme.
        Assert.Null(GetValueOrNull(result, "theme"));
        Assert.Equal(100, GetValue(result, "perPage"));
        Assert.Equal("manga", GetValue(result, "readingMode"));
        Assert.Equal("all", GetValue(result, "filterMode"));
        Assert.Equal("name", GetValue(result, "sortMode"));
    }

    [Fact]
    public async Task GetPreferences_ReturnsStoredValues()
    {
        _stored = new UserPreferences
        {
            UserId = UserId,
            Theme = "light",
            PerPage = 25,
            ReadingMode = "webcomic",
            LibraryViewMode = "series",
            FilterMode = "unmarked",
            SortMode = "date"
        };

        var result = await _controller.GetPreferences();

        Assert.Equal("light", GetValue(result, "theme"));
        Assert.Equal(25, GetValue(result, "perPage"));
        Assert.Equal("webcomic", GetValue(result, "readingMode"));
        Assert.Equal("series", GetValue(result, "libraryViewMode"));
        Assert.Equal("unmarked", GetValue(result, "filterMode"));
        Assert.Equal("date", GetValue(result, "sortMode"));
    }

    [Fact]
    public async Task GetPreferences_LibraryViewMode_DefaultsToFiles()
    {
        var result = await _controller.GetPreferences();

        Assert.Equal("files", GetValue(result, "libraryViewMode"));
    }

    [Theory]
    [InlineData("series")]
    [InlineData("files")]
    public async Task GetPreferences_ReflectsConfiguredDefaultLibraryView(string view)
    {
        _appSettings.DefaultLibraryView = view;

        var result = await _controller.GetPreferences();

        Assert.Equal(view, GetValue(result, "libraryViewMode"));
    }

    [Fact]
    public async Task GetPreferences_FallsBackToFiles_WhenDefaultLibraryViewIsInvalid()
    {
        _appSettings.DefaultLibraryView = "not-a-real-view";

        var result = await _controller.GetPreferences();

        Assert.Equal("files", GetValue(result, "libraryViewMode"));
    }

    [Fact]
    public async Task GetPreferences_WithoutUserId_ReturnsUnauthorized()
    {
        SetUser(null);

        var result = await _controller.GetPreferences();

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePreferences_PersistsValues()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest
        {
            Theme = "light",
            PerPage = 50,
            ReadingMode = "webcomic"
        });

        Assert.Equal("light", GetValue(result, "theme"));
        Assert.Equal(50, GetValue(result, "perPage"));
        Assert.Equal("webcomic", GetValue(result, "readingMode"));

        _preferencesServiceMock.Verify(
            x => x.SavePreferencesAsync(It.Is<UserPreferences>(p => p.UserId == UserId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePreferences_PartialUpdate_DoesNotClobberOtherValues()
    {
        await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { PerPage = 25 });

        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { Theme = "dark" });

        Assert.Equal("dark", GetValue(result, "theme"));
        Assert.Equal(25, GetValue(result, "perPage"));
    }

    [Fact]
    public async Task UpdatePreferences_NormalizesCasing()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { Theme = "LIGHT" });

        Assert.Equal("light", GetValue(result, "theme"));
    }

    [Fact]
    public async Task UpdatePreferences_WithEmptyRequest_IsANoOpAndReturnsOk()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest());

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("purple")]
    public async Task UpdatePreferences_WithInvalidTheme_ReturnsBadRequest(string theme)
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { Theme = theme });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePreferences_WithInvalidReadingMode_ReturnsBadRequest()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { ReadingMode = "vertical" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePreferences_WithInvalidFilterMode_ReturnsBadRequest()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { FilterMode = "everything" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5000)]
    public async Task UpdatePreferences_WithOutOfRangePerPage_ReturnsBadRequest(int perPage)
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { PerPage = perPage });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePreferences_WithoutUserId_ReturnsUnauthorized()
    {
        SetUser(null);

        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { Theme = "dark" });

        Assert.IsType<UnauthorizedResult>(result.Result);
        _preferencesServiceMock.Verify(
            x => x.SavePreferencesAsync(It.IsAny<UserPreferences>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SavePreferences_LegacyPostEndpoint_PersistsValues()
    {
        var result = await _controller.SavePreferences(new PreferencesController.PreferencesRequest { Theme = "light" });

        Assert.Equal("light", GetValue(result, "theme"));
    }
}
