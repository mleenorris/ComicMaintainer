using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary><c>PUT /api/settings</c>: one request for every group of the Settings page.</summary>
public sealed class SettingsControllerSaveAllTests
{
    private const int BytesPerMb = 1048576;

    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly SettingsController _controller;

    public SettingsControllerSaveAllTests()
    {
        _controller = new SettingsController(
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IBatchJobRegistry>(),
            Mock.Of<IFileStoreService>(),
            _settingsService.Object,
            Mock.Of<IHostApplicationLifetime>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            Mock.Of<IEventBroadcaster>(),
            Mock.Of<IProcessingHistoryService>(),
            ComicMaintainer.Tests.Helpers.TestAuthorization.Administrator);
    }

    private static JsonElement ProblemJson(ObjectResult result, int status)
    {
        Assert.Equal(status, result.StatusCode);
        return JsonSerializer.SerializeToElement(result.Value);
    }

    [Fact]
    public async Task UpdateAllSettings_PassesEveryGroupInOneCallConvertingMegabytes()
    {
        var email = new EmailUpdate(SmtpPort: 465);
        var request = new SettingsController.AllSettingsRequest
        {
            FilenameFormat = "{series}",
            LogMaxMB = 1.5,
            IssueNumberPadding = 3,
            DatabaseCleanup = new DatabaseCleanupUpdate(IntervalHours: 6),
            ExternalSeriesMetadata = new ExternalSeriesMetadataUpdate(Enabled: true),
            SeriesImageLimits = new SettingsController.AllSettingsImageLimits { MaxMB = 0.5, MaxDownloadMB = 2, MaxDimension = 900 },
            Email = email,
            EmailRateLimit = new EmailRateLimitUpdate(1, 2, 3),
            Suwayomi = new SuwayomiUpdate(Enabled: false),
            ErrorReporting = new ErrorReportingUpdate(Enabled: false),
            Notifications = new NotificationsUpdate(Komga: new NotificationTargetUpdate(Enabled: true)),
        };
        SettingsUpdate? saved = null;
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<SettingsUpdate, CancellationToken>((update, _) => saved = update)
            .Returns(Task.CompletedTask);

        var result = await _controller.UpdateAllSettings(request);

        Assert.IsType<OkObjectResult>(result);
        _settingsService.Verify(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()), Times.Once());
        _settingsService.VerifyNoOtherCalls();
        Assert.Equal("{series}", saved!.FilenameFormat);
        Assert.Equal(BytesPerMb + (BytesPerMb / 2), saved.LogMaxBytes);
        Assert.Equal(3, saved.IssueNumberPadding);
        Assert.Equal(6, saved.DatabaseCleanup!.IntervalHours);
        Assert.Equal(new SeriesImageLimitsUpdate(BytesPerMb / 2, 2 * BytesPerMb, 900), saved.SeriesImageLimits);
        Assert.Same(email, saved.Email);
        Assert.Equal(1, saved.EmailRateLimit!.MaxSendsPerHour);
        Assert.False(saved.Suwayomi!.Enabled);
        Assert.False(saved.ErrorReporting!.Enabled);
        Assert.True(saved.Notifications!.Komga!.Enabled);
    }

    [Fact]
    public async Task UpdateAllSettings_OmittedValues_StayNull()
    {
        SettingsUpdate? saved = null;
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<SettingsUpdate, CancellationToken>((update, _) => saved = update)
            .Returns(Task.CompletedTask);

        await _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest
        {
            SeriesImageLimits = new SettingsController.AllSettingsImageLimits { MaxDimension = 100 },
        });

        Assert.Null(saved!.FilenameFormat);
        Assert.Null(saved.LogMaxBytes);
        Assert.Null(saved.Email);
        Assert.Equal(new SeriesImageLimitsUpdate(null, null, 100), saved.SeriesImageLimits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2047.5)]
    public async Task UpdateAllSettings_LogSizeOutOfRange_Returns400WithoutSaving(double megabytes)
    {
        var result = await _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest { LogMaxMB = megabytes });

        var json = ProblemJson(Assert.IsType<BadRequestObjectResult>(result), 400);
        Assert.Equal("MaxMB must be between 1 and 2047", json.GetProperty("error").GetString());
        _settingsService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAllSettings_ValidationFailure_Returns400WithTheReasonAndProblemFields()
    {
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SettingsValidationException("Padding must be between 0 and 10", "padding"));

        var result = await _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest { IssueNumberPadding = 99 });

        var json = ProblemJson(Assert.IsType<BadRequestObjectResult>(result), 400);
        Assert.Equal("Padding must be between 0 and 10", json.GetProperty("error").GetString());
        Assert.Equal("Padding must be between 0 and 10", json.GetProperty("detail").GetString());
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task UpdateAllSettings_Pinned_Returns409()
    {
        var pinned = AppSettingsEnvironmentOverrides.All.Single(o => o.Variable == "SMTP_HOST");
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SettingsLockedException([pinned]));

        var result = await _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest());

        var json = ProblemJson(Assert.IsType<ConflictObjectResult>(result), 409);
        Assert.Contains("SMTP_HOST", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("smtp_host", Assert.Single(json.GetProperty("env_locked").EnumerateArray()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task UpdateAllSettings_PinnedWithoutAUiField_ListsNothingForThePage()
    {
        var pinned = AppSettingsEnvironmentOverrides.All.Single(o => o.Variable == "WATCHED_DIR");
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SettingsLockedException([pinned]));

        var result = await _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest());

        var json = ProblemJson(Assert.IsType<ConflictObjectResult>(result), 409);
        Assert.Empty(json.GetProperty("env_locked").EnumerateArray());
        Assert.Contains("WATCHED_DIR", json.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAllSettings_UnexpectedFailure_ReachesTheGlobalHandler()
    {
        var failure = new IOException("disk full: /config");
        _settingsService
            .Setup(s => s.UpdateSettingsAsync(It.IsAny<SettingsUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var thrown = await Assert.ThrowsAsync<IOException>(() => _controller.UpdateAllSettings(new SettingsController.AllSettingsRequest()));

        Assert.Same(failure, thrown);
    }
}
