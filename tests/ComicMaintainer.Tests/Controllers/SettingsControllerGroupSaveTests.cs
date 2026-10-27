using System.Text.Json;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Regression tests for #872: the grouped settings endpoints, run against the
/// real <see cref="SettingsService"/>, save every field of a group or none.
/// </summary>
public sealed class SettingsControllerGroupSaveTests : IDisposable
{
    private const int BytesPerMb = 1048576;

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
    private readonly HookLogger _serviceLogger = new();
    private readonly SettingsController _controller;

    public SettingsControllerGroupSaveTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"SettingsGroupSave_{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_configDir, "user-settings.json");

        var options = new TestOptionsMonitor<AppSettings>(new AppSettings { ConfigDirectory = _configDir });
        _controller = new SettingsController(
            options,
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IComicProcessorService>(),
            Mock.Of<IFileStoreService>(),
            new SettingsService(_serviceLogger, options),
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
    public async Task UpdateExternalSeriesMetadata_InterruptedOnceASettingIsSaved_HasSavedTheWholeGroup(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        InterruptAfterFirstSavedSetting(interruption, request);

        var result = await _controller.UpdateExternalSeriesMetadata(NewMetadataSettings(), request.Token);

        Assert.IsType<OkObjectResult>(result);
        var section = ReadSection();
        Assert.True(section["EnableExternalSeriesMetadata"].GetBoolean());
        Assert.Equal("new-key", section["ComicVineApiKey"].GetString());
        Assert.Equal("https://comicvine.example/api", section["ComicVineBaseUrl"].GetString());
        Assert.True(section["EnableMangaDexMetadata"].GetBoolean());
        Assert.Equal("https://mangadex.example", section["MangaDexBaseUrl"].GetString());
        Assert.True(section["EnableAniListMetadata"].GetBoolean());
        Assert.Equal("https://anilist.example/graphql", section["AniListBaseUrl"].GetString());
        Assert.Equal(1000, section["SeriesImageMaxDimension"].GetInt32());
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
    public async Task UpdateSeriesImageLimits_InterruptedOnceALimitIsSaved_HasSavedAllThree(Interruption interruption)
    {
        using var request = new CancellationTokenSource();
        InterruptAfterFirstSavedSetting(interruption, request);

        var result = await _controller.UpdateSeriesImageLimits(NewImageLimits(), request.Token);

        Assert.IsType<OkObjectResult>(result);
        var section = ReadSection();
        Assert.Equal(4 * BytesPerMb, section["SeriesImageMaxBytes"].GetInt32());
        Assert.Equal(16 * BytesPerMb, section["SeriesImageMaxDownloadBytes"].GetInt32());
        Assert.Equal(3000, section["SeriesImageMaxDimension"].GetInt32());
        Assert.Equal("old-key", section["ComicVineApiKey"].GetString());
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
    /// Interrupts the save as soon as the service reports the first setting it
    /// has written — the point where a field-by-field save had stored only
    /// part of the group.
    /// </summary>
    private void InterruptAfterFirstSavedSetting(Interruption interruption, CancellationTokenSource request)
    {
        var interrupted = 0;
        _serviceLogger.OnMessage = message =>
        {
            if (message.StartsWith("Updated setting ", StringComparison.Ordinal)
                && Interlocked.Exchange(ref interrupted, 1) == 0)
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

    private Dictionary<string, JsonElement> ReadSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        return document.RootElement.GetProperty("AppSettings")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private sealed class HookLogger : ILogger<SettingsService>
    {
        public Action<string>? OnMessage { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => OnMessage?.Invoke(formatter(state, exception));
    }
}
