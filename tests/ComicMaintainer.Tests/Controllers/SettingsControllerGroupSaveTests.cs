using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Regression tests for #872: the grouped settings endpoints, run against the
/// real <see cref="SettingsService"/>, save every field of a group or none.
/// </summary>
public sealed class SettingsControllerGroupSaveTests : IDisposable
{
    private const string SeededSettings = """
        {
          "AppSettings": {
            "EnableExternalSeriesMetadata": false,
            "ComicVineApiKey": "old-key",
            "ComicVineBaseUrl": "https://old.comicvine.example/api",
            "EnableMangaDexMetadata": false,
            "MangaDexBaseUrl": "https://old.mangadex.example",
            "EnableAniListMetadata": false,
            "AniListBaseUrl": "https://old.anilist.example",
            "SeriesImageMaxBytes": 1048576,
            "SeriesImageMaxDownloadBytes": 2097152,
            "SeriesImageMaxDimension": 1000
          }
        }
        """;

    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly InterruptibleSettingsService _settingsService;
    private readonly SettingsController _controller;

    public SettingsControllerGroupSaveTests()
    {
        _configDir = Path.Join(Path.GetTempPath(), $"SettingsGroupSave_{Guid.NewGuid():N}");
        _settingsPath = Path.Join(_configDir, "user-settings.json");

        var options = new TestOptionsMonitor<AppSettings>(new AppSettings { ConfigDirectory = _configDir });
        _settingsService = new InterruptibleSettingsService(options);
        _controller = new SettingsController(
            options,
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IComicProcessorService>(),
            Mock.Of<IFileStoreService>(),
            _settingsService,
            Mock.Of<IHostApplicationLifetime>());

        File.WriteAllText(_settingsPath, SeededSettings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    /// <summary>How a save is cut short.</summary>
    public enum Interruption
    {
        /// <summary>The client aborts the request.</summary>
        Abort,

        /// <summary>The settings file can no longer be written.</summary>
        WriteFailure,
    }

    private static SettingsController.ExternalSeriesMetadataSettingsRequest NewMetadataSettings() => new()
    {
        Enabled = true,
        ComicVineApiKey = "new-key",
        ComicVineBaseUrl = "https://comicvine.example/api",
        EnableMangaDex = true,
        MangaDexBaseUrl = "https://mangadex.example",
        EnableAniList = true,
        AniListBaseUrl = "https://anilist.example/graphql",
    };

    private static SettingsController.SeriesImageLimitsRequest NewImageLimits() => new()
    {
        MaxMB = 4,
        MaxDownloadMB = 16,
        MaxDimension = 3000,
    };

    [Theory]
    [InlineData(Interruption.Abort)]
    [InlineData(Interruption.WriteFailure)]
    public async Task UpdateExternalSeriesMetadata_InterruptedBetweenFields_ChangesNothing(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        InterruptBeforeSecondStagedSetting(interruption, request);

        var result = await _controller.UpdateExternalSeriesMetadata(NewMetadataSettings(), request.Token);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(SeededSettings, File.ReadAllText(_settingsPath));
    }

    [Theory]
    [InlineData(Interruption.Abort)]
    [InlineData(Interruption.WriteFailure)]
    public async Task UpdateExternalSeriesMetadata_InterruptedBeforeSaving_ChangesNothing(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        Interrupt(interruption, request);

        var result = await _controller.UpdateExternalSeriesMetadata(NewMetadataSettings(), request.Token);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(SeededSettings, File.ReadAllText(_settingsPath));
    }

    [Theory]
    [InlineData(Interruption.Abort)]
    [InlineData(Interruption.WriteFailure)]
    public async Task UpdateSeriesImageLimits_InterruptedBetweenLimits_ChangesNothing(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        InterruptBeforeSecondStagedSetting(interruption, request);

        var result = await _controller.UpdateSeriesImageLimits(NewImageLimits(), request.Token);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(SeededSettings, File.ReadAllText(_settingsPath));
    }

    [Theory]
    [InlineData(Interruption.Abort)]
    [InlineData(Interruption.WriteFailure)]
    public async Task UpdateSeriesImageLimits_InterruptedBeforeSaving_ChangesNothing(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        Interrupt(interruption, request);

        var result = await _controller.UpdateSeriesImageLimits(NewImageLimits(), request.Token);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(SeededSettings, File.ReadAllText(_settingsPath));
    }

    /// <summary>
    /// Interrupts the save inside persistence, after the first field of the
    /// group has been staged and before the second one is. A field-by-field
    /// save has already written the first field by then, so it would leave
    /// the group half-saved; an atomic save writes nothing.
    /// </summary>
    private void InterruptBeforeSecondStagedSetting(Interruption interruption, CancellationTokenSource request)
    {
        var staged = 0;
        _settingsService.OnStaging = _ =>
        {
            if (Interlocked.Increment(ref staged) == 2)
            {
                Interrupt(interruption, request);
            }
        };
    }

    private void Interrupt(Interruption interruption, CancellationTokenSource request)
    {
        switch (interruption)
        {
            case Interruption.Abort:
                request.Cancel();
                break;
            case Interruption.WriteFailure:
                // A directory where the service stages its temporary file makes
                // every later write of user-settings.json fail.
                Directory.CreateDirectory(_settingsPath + ".tmp");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(interruption), interruption, null);
        }
    }

    private sealed class InterruptibleSettingsService(TestOptionsMonitor<AppSettings> options)
        : SettingsService(NullLogger<SettingsService>.Instance, options)
    {
        public Action<string>? OnStaging { get; set; }

        protected override void OnStagingSetting(string settingName) => OnStaging?.Invoke(settingName);
    }
}
