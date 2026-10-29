using System.Security.Claims;
using System.Text.Json;
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

/// <summary>
/// Validation paths of <see cref="PreferencesController"/> not covered by
/// <see cref="PreferencesControllerTests"/>: a missing payload, invalid view and
/// sort modes, an invalid sort direction, and a blank configured default library view.
/// </summary>
public class PreferencesControllerCoverageTests
{
    private readonly AppSettings _appSettings = new();
    private readonly Mock<IUserPreferencesService> _preferencesService = new();
    private readonly PreferencesController _controller;

    public PreferencesControllerCoverageTests()
    {
        _controller = new PreferencesController(
            Mock.Of<ILogger<PreferencesController>>(),
            Mock.Of<IOptionsMonitor<AppSettings>>(m => m.CurrentValue == _appSettings),
            _preferencesService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") }, "Test"))
                }
            }
        };
    }

    [Fact]
    public async Task UpdatePreferences_WithoutPayloadIsBadRequest()
    {
        var result = await _controller.UpdatePreferences(null!);

        Assert.Equal("A preferences payload is required.", ErrorOf(result));
        VerifyNothingSaved();
    }

    [Fact]
    public async Task UpdatePreferences_UnknownLibraryViewModeIsBadRequest()
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { LibraryViewMode = "grid" });

        Assert.Equal("LibraryViewMode must be one of: files, series.", ErrorOf(result));
        VerifyNothingSaved();
    }

    [Fact]
    public async Task SavePreferences_UnknownSortModeIsBadRequest()
    {
        var result = await _controller.SavePreferences(new PreferencesController.PreferencesRequest { SortMode = "rating" });

        Assert.Equal("SortMode must be one of: name, date, size.", ErrorOf(result));
        VerifyNothingSaved();
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("descending")]
    public async Task UpdatePreferences_UnknownSortDirectionIsBadRequest(string sortDirection)
    {
        var result = await _controller.UpdatePreferences(new PreferencesController.PreferencesRequest { SortMode = "date", SortDirection = sortDirection });

        Assert.Equal("SortDirection must be one of: asc, desc.", ErrorOf(result));
        VerifyNothingSaved();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPreferences_BlankConfiguredDefaultViewFallsBackToFiles(string configuredView)
    {
        _appSettings.DefaultLibraryView = configuredView;
        _preferencesService
            .Setup(s => s.GetPreferencesAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserPreferences { UserId = "user-1" });

        var result = await _controller.GetPreferences();

        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("files", json.GetProperty("libraryViewMode").GetString());
    }

    private void VerifyNothingSaved() =>
        _preferencesService.Verify(s => s.SavePreferencesAsync(It.IsAny<UserPreferences>(), It.IsAny<CancellationToken>()), Times.Never);

    private static string? ErrorOf(ActionResult<object> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<BadRequestObjectResult>(result.Result).Value).GetProperty("error").GetString();
}
