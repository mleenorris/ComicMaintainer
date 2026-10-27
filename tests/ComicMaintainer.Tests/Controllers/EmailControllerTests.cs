using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Endpoint behaviour of <see cref="EmailController"/>: request validation, how
/// service results and exceptions map to status codes, the JSON shape the UI
/// reads, and the lifetime of the temporary books streamed back as downloads.
/// </summary>
public sealed class EmailControllerTests : IDisposable
{
    private const string UserId = "user-42";
    private const string SeriesId = "series-saga";
    private const int MaxSeriesIssues = 1000;

    private static readonly DateTime CreatedAt = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IEreaderDeviceService> _devices = new();
    private readonly Mock<IComicEmailService> _email = new();
    private readonly Mock<ICondensedBookBuildTracker> _builds = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<ILogger<EmailController>> _logger = new();
    private readonly CapturingResponseFeature _responseFeature = new();
    private readonly AppSettings _settings = new()
    {
        EmailFromAddress = "library@example.test",
        EmailMaxAttachmentMegabytes = 40
    };
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cm_email_ctrl_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _filesOutsideTempDir = new();
    private readonly List<string> _overlongDirectories = new();

    public EmailControllerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var file in _filesOutsideTempDir)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        foreach (var directory in _overlongDirectories)
        {
            try { OverlongPath.Release(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ─── Status and devices ─────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetStatus_ReportsWhetherEmailIsConfiguredAndTheLimits(bool configured)
    {
        _email.SetupGet(e => e.IsConfigured).Returns(configured);

        var json = OkJson(CreateController().GetStatus());

        Assert.Equal(configured, json.GetProperty("configured").GetBoolean());
        Assert.Equal("library@example.test", json.GetProperty("from_address").GetString());
        Assert.Equal(40, json.GetProperty("max_attachment_mb").GetInt32());
    }

    [Fact]
    public async Task GetDevices_ReturnsTheSavedDevices()
    {
        _devices.Setup(d => d.GetDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Device(3), Device(4) });

        var json = OkJson(await CreateController().GetDevices(CancellationToken.None));

        Assert.Equal(new[] { 3, 4 }, json.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("Id").GetInt32()));
    }

    [Fact]
    public async Task CreateDevice_WithoutBody_IsBadRequest()
    {
        var result = await CreateController().CreateDevice(null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf<BadRequestObjectResult>(result.Result));
        _devices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateDevice_ReturnsTheCreatedDevice()
    {
        var created = Device(9);
        _devices.Setup(d => d.CreateDeviceAsync("Kindle", "me@kindle.test", "azw3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var result = await CreateController().CreateDevice(
            new EmailController.DeviceRequest { Name = "Kindle", EmailAddress = "me@kindle.test", DeliveryFormat = "azw3" },
            CancellationToken.None);

        Assert.Same(created, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task CreateDevice_MissingFieldsAreSentAsEmptyStringsForTheServiceToValidate()
    {
        _devices.Setup(d => d.CreateDeviceAsync(string.Empty, string.Empty, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Name is required"));

        var result = await CreateController().CreateDevice(new EmailController.DeviceRequest(), CancellationToken.None);

        Assert.Equal("Name is required", ErrorOf<BadRequestObjectResult>(result.Result));
    }

    [Fact]
    public async Task CreateDevice_DuplicateAddressIsConflict()
    {
        _devices.Setup(d => d.CreateDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("A device with that address already exists"));

        var result = await CreateController().CreateDevice(
            new EmailController.DeviceRequest { Name = "Kobo", EmailAddress = "dup@kobo.test" },
            CancellationToken.None);

        Assert.Equal("A device with that address already exists", ErrorOf<ConflictObjectResult>(result.Result));
    }

    [Fact]
    public async Task UpdateDevice_WithoutBody_IsBadRequest()
    {
        var result = await CreateController().UpdateDevice(1, null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf<BadRequestObjectResult>(result.Result));
        _devices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateDevice_PassesOmittedFieldsAsNullSoTheyAreLeftUntouched()
    {
        var updated = Device(5);
        _devices.Setup(d => d.UpdateDeviceAsync(5, "Renamed", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var result = await CreateController().UpdateDevice(
            5,
            new EmailController.DeviceRequest { Name = "Renamed" },
            CancellationToken.None);

        Assert.Same(updated, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpdateDevice_UnknownDeviceIsNotFound()
    {
        _devices.Setup(d => d.UpdateDeviceAsync(77, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EreaderDeviceDto?)null);

        var result = await CreateController().UpdateDevice(77, new EmailController.DeviceRequest { Name = "x" }, CancellationToken.None);

        Assert.Equal("Device not found", ErrorOf<NotFoundObjectResult>(result.Result));
    }

    [Fact]
    public async Task UpdateDevice_MapsValidationToBadRequestAndDuplicatesToConflict()
    {
        var controller = CreateController();
        var request = new EmailController.DeviceRequest { EmailAddress = "not-an-address" };

        _devices.Setup(d => d.UpdateDeviceAsync(1, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid email address"));
        Assert.Equal("Invalid email address", ErrorOf<BadRequestObjectResult>((await controller.UpdateDevice(1, request, CancellationToken.None)).Result));

        _devices.Setup(d => d.UpdateDeviceAsync(1, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Address already in use"));
        Assert.Equal("Address already in use", ErrorOf<ConflictObjectResult>((await controller.UpdateDevice(1, request, CancellationToken.None)).Result));
    }

    [Fact]
    public async Task DeleteDevice_ReportsWhetherTheDeviceExisted()
    {
        _devices.Setup(d => d.DeleteDeviceAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _devices.Setup(d => d.DeleteDeviceAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = CreateController();

        var deleted = Assert.IsType<OkObjectResult>(await controller.DeleteDevice(1, CancellationToken.None));
        Assert.Equal("Device deleted", ToJson(deleted.Value).GetProperty("message").GetString());
        Assert.Equal("Device not found", ErrorOf<NotFoundObjectResult>(await controller.DeleteDevice(2, CancellationToken.None)));
    }

    [Fact]
    public async Task SendTestEmail_ConfirmsDelivery()
    {
        _email.Setup(e => e.SendTestEmailAsync(4, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().SendTestEmail(4, CancellationToken.None));

        Assert.Equal("Test email sent", ToJson(ok.Value).GetProperty("message").GetString());
        _email.Verify(e => e.SendTestEmailAsync(4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendTestEmail_ConfigurationProblemIsBadRequest()
    {
        _email.Setup(e => e.SendTestEmailAsync(4, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP host is not configured"));

        var result = await CreateController().SendTestEmail(4, CancellationToken.None);

        Assert.Equal("SMTP host is not configured", ErrorOf<BadRequestObjectResult>(result));
        VerifyLogged(LogLevel.Error, Times.Never());
    }

    [Fact]
    public async Task SendTestEmail_DeliveryFailureIsBadGatewayAndLogged()
    {
        var failure = new IOException("Connection refused");
        _email.Setup(e => e.SendTestEmailAsync(4, It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = Assert.IsType<ObjectResult>(await CreateController().SendTestEmail(4, CancellationToken.None));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal("Failed to send test email: Connection refused", ToJson(result.Value).GetProperty("error").GetString());
        _logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            failure,
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    // ─── Sending files ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendFiles_RequiresAtLeastOneFile()
    {
        var controller = CreateController();

        foreach (var request in new EmailController.SendFilesRequest?[]
                 {
                     null,
                     new() { Files = null! },
                     new() { Files = new List<string>() }
                 })
        {
            var result = await controller.SendFiles(request!, CancellationToken.None);
            Assert.Equal("At least one file is required", ErrorOf<BadRequestObjectResult>(result.Result));
        }

        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendFiles_WithoutCondensing_QueuesOneDeliveryPerFile()
    {
        var files = new List<string> { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" };
        SetupQueueFiles(QueueResult(new[] { files[0] }, new Dictionary<string, string> { [files[1]] = "Already delivered" }));

        var json = OkJson(await CreateController().SendFiles(
            new EmailController.SendFilesRequest
            {
                Files = files,
                DeviceId = 7,
                DeliveryFormat = "epub",
                SkipAlreadyDelivered = false,
                CondenseMode = "  NONE "
            },
            CancellationToken.None));

        Assert.Equal(files[0], Assert.Single(json.GetProperty("queued").EnumerateArray()).GetProperty("FilePath").GetString());
        Assert.Equal("Already delivered", json.GetProperty("skipped").GetProperty(files[1]).GetString());
        _email.Verify(e => e.QueueFilesAsync(
            files, 7, "epub", EmailDeliverySource.Manual, false, null, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoCondensedQueue();
    }

    [Fact]
    public async Task SendFiles_CondenseModeIsNormalizedAndKeepsSelectionOrderUnpreserved()
    {
        var files = new List<string> { "/lib/B/B 001.cbz", "/lib/A/A 001.cbz" };
        SetupQueueCondensed(QueueResult(new[] { "/lib/A/A 001.cbz" }));

        var json = OkJson(await CreateController().SendFiles(
            new EmailController.SendFilesRequest
            {
                Files = files,
                DeviceId = 2,
                CondenseMode = " Count ",
                IssuesPerBook = 5,
                DeliveryFormat = "azw3"
            },
            CancellationToken.None));

        Assert.Single(json.GetProperty("queued").EnumerateArray());
        Assert.Empty(json.GetProperty("skipped").EnumerateObject());
        // A free selection can span series, so the service regroups it by folder.
        _email.Verify(e => e.QueueCondensedFilesAsync(
            files, 2, EmailCondenseMode.Count, 5, EmailDeliverySource.Manual, true, false, "azw3", It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoPlainQueue();
    }

    [Fact]
    public async Task SendFiles_UnknownCondenseModeIsRejectedBeforeAnythingIsQueued()
    {
        var result = await CreateController().SendFiles(
            new EmailController.SendFilesRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "weekly" },
            CancellationToken.None);

        Assert.Contains("Unsupported condense mode 'weekly'", ErrorOf<BadRequestObjectResult>(result.Result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendFiles_ServiceRejectionsAreBadRequests()
    {
        var controller = CreateController();
        var request = new EmailController.SendFilesRequest { Files = new List<string> { "/lib/a.cbz" }, DeviceId = 99 };

        _email.Setup(e => e.QueueFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unsupported delivery format"));
        Assert.Equal("Unsupported delivery format", ErrorOf<BadRequestObjectResult>((await controller.SendFiles(request, CancellationToken.None)).Result));

        _email.Setup(e => e.QueueFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Device not found"));
        Assert.Equal("Device not found", ErrorOf<BadRequestObjectResult>((await controller.SendFiles(request, CancellationToken.None)).Result));
    }

    // ─── Sending a series ───────────────────────────────────────────────────

    [Fact]
    public async Task SendSeries_RequiresASeriesId()
    {
        var controller = CreateController();

        Assert.Equal("Series id is required", ErrorOf<BadRequestObjectResult>((await controller.SendSeries(null!, CancellationToken.None)).Result));
        Assert.Equal("Series id is required", ErrorOf<BadRequestObjectResult>(
            (await controller.SendSeries(new EmailController.SendSeriesRequest { SeriesId = "  " }, CancellationToken.None)).Result));
        _seriesLibrary.VerifyNoOtherCalls();
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendSeries_UnknownSeriesIsNotFound()
    {
        SetupSeries(null);

        var result = await CreateController().SendSeries(new EmailController.SendSeriesRequest { SeriesId = SeriesId }, CancellationToken.None);

        Assert.Equal("Series not found", ErrorOf<NotFoundObjectResult>(result.Result));
        _email.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(MaxSeriesIssues + 1, 1)]
    [InlineData(10, 2)]
    public async Task SendSeries_RefusesToSilentlyTruncateALargeSeries(int issueCount, int totalPages)
    {
        var series = Series("/lib/Saga/Saga 001.cbz");
        series.IssueCount = issueCount;
        series.TotalPages = totalPages;
        SetupSeries(series);

        var result = await CreateController().SendSeries(new EmailController.SendSeriesRequest { SeriesId = SeriesId }, CancellationToken.None);

        Assert.Equal(
            $"Series has {issueCount} issues, which exceeds the 1000 issue limit for a single send. Select the issues to send instead.",
            ErrorOf<BadRequestObjectResult>(result.Result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendSeries_EmptySeriesIsBadRequest()
    {
        SetupSeries(Series());

        var result = await CreateController().SendSeries(new EmailController.SendSeriesRequest { SeriesId = SeriesId }, CancellationToken.None);

        Assert.Equal("Series has no issues to send", ErrorOf<BadRequestObjectResult>(result.Result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendSeries_QueuesEveryIssueOfTheSeries()
    {
        SetupSeries(Series("/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz"));
        SetupQueueFiles(QueueResult(new[] { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" }));

        var json = OkJson(await CreateController().SendSeries(
            new EmailController.SendSeriesRequest { SeriesId = SeriesId, DeviceId = 3, DeliveryFormat = "original" },
            CancellationToken.None));

        Assert.Equal("Saga", json.GetProperty("series").GetString());
        Assert.Equal(2, json.GetProperty("queued").GetArrayLength());
        _email.Verify(e => e.QueueFilesAsync(
            It.Is<IEnumerable<string>>(f => f.SequenceEqual(new[] { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" })),
            3, "original", EmailDeliverySource.Manual, true, null, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoCondensedQueue();
    }

    [Fact]
    public async Task SendSeries_CondensedSendKeepsTheSeriesIssueOrder()
    {
        SetupSeries(Series("/lib/Saga/Saga 002.cbz", "/lib/Saga/Saga 001.cbz"));
        SetupQueueCondensed(QueueResult(new[] { "/lib/Saga/Saga 002.cbz" }));

        var json = OkJson(await CreateController().SendSeries(
            new EmailController.SendSeriesRequest
            {
                SeriesId = SeriesId,
                DeviceId = 3,
                CondenseMode = "ALL",
                SkipAlreadyDelivered = false
            },
            CancellationToken.None));

        Assert.Equal("Saga", json.GetProperty("series").GetString());
        _email.Verify(e => e.QueueCondensedFilesAsync(
            It.Is<IEnumerable<string>>(f => f.SequenceEqual(new[] { "/lib/Saga/Saga 002.cbz", "/lib/Saga/Saga 001.cbz" })),
            3, EmailCondenseMode.All, null, EmailDeliverySource.Manual, false, true, null, It.IsAny<CancellationToken>()), Times.Once);
        VerifyNoPlainQueue();
    }

    [Fact]
    public async Task SendSeries_UnknownCondenseModeIsBadRequest()
    {
        SetupSeries(Series("/lib/Saga/Saga 001.cbz"));

        var result = await CreateController().SendSeries(
            new EmailController.SendSeriesRequest { SeriesId = SeriesId, CondenseMode = "pairs" },
            CancellationToken.None);

        Assert.Contains("Unsupported condense mode 'pairs'", ErrorOf<BadRequestObjectResult>(result.Result));
        VerifyNoPlainQueue();
        VerifyNoCondensedQueue();
    }

    [Fact]
    public async Task SendSeries_ServiceRejectionsAreBadRequests()
    {
        SetupSeries(Series("/lib/Saga/Saga 001.cbz"));
        var controller = CreateController();
        var request = new EmailController.SendSeriesRequest { SeriesId = SeriesId, CondenseMode = "count" };

        _email.Setup(e => e.QueueCondensedFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Condensing by count requires at least 2 issues per book."));
        Assert.Equal("Condensing by count requires at least 2 issues per book.", ErrorOf<BadRequestObjectResult>((await controller.SendSeries(request, CancellationToken.None)).Result));

        _email.Setup(e => e.QueueCondensedFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Device not found"));
        Assert.Equal("Device not found", ErrorOf<BadRequestObjectResult>((await controller.SendSeries(request, CancellationToken.None)).Result));
    }

    // ─── Condense plan ──────────────────────────────────────────────────────

    [Fact]
    public async Task PlanCondense_WithoutBody_IsBadRequest()
    {
        var result = await CreateController().PlanCondense(null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf<BadRequestObjectResult>(result.Result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlanCondense_RequiresFilesWhenNoSeriesIsGiven()
    {
        var controller = CreateController();

        Assert.Equal("At least one file is required", ErrorOf<BadRequestObjectResult>(
            (await controller.PlanCondense(new EmailController.CondenseRequest(), CancellationToken.None)).Result));
        Assert.Equal("At least one file is required", ErrorOf<BadRequestObjectResult>(
            (await controller.PlanCondense(new EmailController.CondenseRequest { Files = null! }, CancellationToken.None)).Result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlanCondense_DescribesTheBooksOfASelection()
    {
        var files = new List<string> { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz", "/lib/Saga/Saga 003.cbz" };
        var plan = new CondensePlanDto(
            EmailCondenseMode.Count,
            TotalIssues: 3,
            IssuesPerBook: 2,
            MaxAttachmentBytes: 1000,
            Books: new[]
            {
                new CondensedBookDto("Saga #1-2", files.Take(2).ToList(), 1500, ExceedsAttachmentLimit: true),
                new CondensedBookDto("Saga #3", files.Skip(2).ToList(), 400, ExceedsAttachmentLimit: false)
            },
            Skipped: new Dictionary<string, string> { ["/lib/missing.cbz"] = "File not found" });
        _email.Setup(e => e.PlanCondensedDeliveryAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var json = OkJson(await CreateController().PlanCondense(
            new EmailController.CondenseRequest
            {
                Files = files,
                CondenseMode = "count",
                IssuesPerBook = 2,
                DeviceId = 8,
                SkipAlreadyDelivered = false
            },
            CancellationToken.None));

        Assert.Equal("count", json.GetProperty("mode").GetString());
        Assert.Equal(3, json.GetProperty("total_issues").GetInt32());
        Assert.Equal(2, json.GetProperty("issues_per_book").GetInt32());
        Assert.Equal(1000, json.GetProperty("max_attachment_bytes").GetInt64());
        Assert.False(json.GetProperty("can_email").GetBoolean());
        Assert.Equal(1, json.GetProperty("oversized_book_count").GetInt32());
        var books = json.GetProperty("books").EnumerateArray().ToList();
        Assert.Equal(2, books.Count);
        Assert.Equal("Saga #1-2", books[0].GetProperty("display_name").GetString());
        Assert.Equal(2, books[0].GetProperty("issue_count").GetInt32());
        Assert.Equal(1500, books[0].GetProperty("estimated_bytes").GetInt64());
        Assert.True(books[0].GetProperty("exceeds_attachment_limit").GetBoolean());
        Assert.False(books[1].GetProperty("exceeds_attachment_limit").GetBoolean());
        Assert.Equal("File not found", json.GetProperty("skipped").GetProperty("/lib/missing.cbz").GetString());
        _email.Verify(e => e.PlanCondensedDeliveryAsync(files, "count", 2, false, 8, false, It.IsAny<CancellationToken>()), Times.Once);
        _seriesLibrary.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlanCondense_ForASeriesUsesItsIssuesInSeriesOrder()
    {
        SetupSeries(Series("/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz"));
        _email.Setup(e => e.PlanCondensedDeliveryAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CondensePlanDto("all", 2, 2, 1000, Array.Empty<CondensedBookDto>(), new Dictionary<string, string>()));

        var json = OkJson(await CreateController().PlanCondense(
            new EmailController.CondenseRequest
            {
                SeriesId = SeriesId,
                // Ignored: the series decides which issues are condensed.
                Files = new List<string> { "/lib/Other/Other 001.cbz" },
                CondenseMode = "all"
            },
            CancellationToken.None));

        // No books means nothing can be emailed.
        Assert.False(json.GetProperty("can_email").GetBoolean());
        _email.Verify(e => e.PlanCondensedDeliveryAsync(
            It.Is<IEnumerable<string>>(f => f.SequenceEqual(new[] { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" })),
            "all", null, true, null, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlanCondense_SeriesProblemsAreReportedWithoutPlanning()
    {
        var controller = CreateController();
        var request = new EmailController.CondenseRequest { SeriesId = SeriesId, CondenseMode = "all" };

        SetupSeries(null);
        Assert.Equal("Series not found", ErrorOf<NotFoundObjectResult>((await controller.PlanCondense(request, CancellationToken.None)).Result));

        var huge = Series("/lib/Saga/Saga 001.cbz");
        huge.IssueCount = 1500;
        SetupSeries(huge);
        Assert.Contains("exceeds the 1000 issue limit", ErrorOf<BadRequestObjectResult>((await controller.PlanCondense(request, CancellationToken.None)).Result));

        var paged = Series("/lib/Saga/Saga 001.cbz");
        paged.TotalPages = 3;
        SetupSeries(paged);
        Assert.Contains("exceeds the 1000 issue limit", ErrorOf<BadRequestObjectResult>((await controller.PlanCondense(request, CancellationToken.None)).Result));

        SetupSeries(Series());
        Assert.Equal("Series has no issues to send", ErrorOf<BadRequestObjectResult>((await controller.PlanCondense(request, CancellationToken.None)).Result));

        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlanCondense_InvalidSettingsAreBadRequest()
    {
        _email.Setup(e => e.PlanCondensedDeliveryAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("At most 500 issues can be condensed into a single book."));

        var result = await CreateController().PlanCondense(
            new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "count", IssuesPerBook = 9000 },
            CancellationToken.None);

        Assert.Equal("At most 500 issues can be condensed into a single book.", ErrorOf<BadRequestObjectResult>(result.Result));
    }

    // ─── Synchronous condensed download ─────────────────────────────────────

    [Fact]
    public async Task DownloadCondensedBook_WithoutBody_IsBadRequest()
    {
        var result = await CreateController().DownloadCondensedBook(null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf<BadRequestObjectResult>(result));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadCondensedBook_UnresolvableSelectionIsReportedWithoutBuilding()
    {
        SetupSeries(null);
        var controller = CreateController();

        Assert.Equal("At least one file is required", ErrorOf<BadRequestObjectResult>(
            await controller.DownloadCondensedBook(new EmailController.CondenseRequest(), CancellationToken.None)));
        Assert.Equal("Series not found", ErrorOf<NotFoundObjectResult>(
            await controller.DownloadCondensedBook(new EmailController.CondenseRequest { SeriesId = SeriesId }, CancellationToken.None)));
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadCondensedBook_StreamsTheBookAndRemovesItsWorkDirectoryOnceTheResponseCompletes()
    {
        var workDirectory = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var bookPath = Path.Combine(workDirectory, "Saga 001-002.epub");
        await File.WriteAllTextAsync(bookPath, "epub-bytes");
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "scratch.bin"), "intermediate");
        var files = new List<string> { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" };
        SetupCreateBook(new CondensedBookFile(bookPath, "Saga 001-002.epub"));

        var result = await CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest
            {
                Files = files,
                CondenseMode = "count",
                IssuesPerBook = 2,
                BookIndex = 1,
                DeviceId = 6,
                SkipAlreadyDelivered = false,
                DeliveryFormat = "epub"
            },
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/epub+zip", file.ContentType);
        Assert.Equal("Saga 001-002.epub", file.FileDownloadName);
        _email.Verify(e => e.CreateCondensedBookAsync(
            files, "count", 2, 1, false, 6, false, null, "epub", It.IsAny<CancellationToken>()), Times.Once);

        using (var reader = new StreamReader(file.FileStream))
        {
            Assert.Equal("epub-bytes", await reader.ReadToEndAsync());
        }

        // The book exists only for this response: closing the stream deletes it,
        // and the rest of the work directory goes once the response completes.
        Assert.False(File.Exists(bookPath));
        Assert.True(Directory.Exists(workDirectory));

        await _responseFeature.RunOnCompletedAsync();
        Assert.False(Directory.Exists(workDirectory));

        // Running the cleanup again (the directory is already gone) is harmless.
        await _responseFeature.RunOnCompletedAsync();
    }

    [Fact]
    public async Task DownloadCondensedBook_Azw3BookOfASeriesIsServedAsKindleFormat()
    {
        SetupSeries(Series("/lib/Saga/Saga 002.cbz", "/lib/Saga/Saga 001.cbz"));
        var workDirectory = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var bookPath = Path.Combine(workDirectory, "Saga.AZW3");
        await File.WriteAllTextAsync(bookPath, "azw3-bytes");
        SetupCreateBook(new CondensedBookFile(bookPath, "Saga.AZW3"));

        var result = await CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest { SeriesId = SeriesId, CondenseMode = "all", DeliveryFormat = "azw3" },
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        await file.FileStream.DisposeAsync();
        Assert.Equal("application/x-mobi8-ebook", file.ContentType);
        Assert.Equal("Saga.AZW3", file.FileDownloadName);
        _email.Verify(e => e.CreateCondensedBookAsync(
            It.Is<IEnumerable<string>>(f => f.SequenceEqual(new[] { "/lib/Saga/Saga 002.cbz", "/lib/Saga/Saga 001.cbz" })),
            "all", null, 0, true, null, true, null, "azw3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadCondensedBook_BookWithoutADirectoryPartDeletesNothingElse()
    {
        // A bare file name has an empty directory part; the cleanup must treat
        // that as "nothing to remove", never as the current directory.
        var fileName = $"cm-email-ctrl-{Guid.NewGuid():N}.epub";
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        _filesOutsideTempDir.Add(fullPath);
        await File.WriteAllTextAsync(fullPath, "book");
        SetupCreateBook(new CondensedBookFile(fileName, fileName));

        var result = await CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "all" },
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        await file.FileStream.DisposeAsync();
        Assert.Null(await Record.ExceptionAsync(() => _responseFeature.RunOnCompletedAsync()));
        Assert.Single(_responseFeature.OnCompletedCallbacks);
        Assert.True(Directory.Exists(Directory.GetCurrentDirectory()));
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task DownloadCondensedBook_WorkDirectoryThatCannotBeRemovedIsLoggedInsteadOfFailingTheResponse()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workDirectory = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var bookPath = Path.Combine(workDirectory, "Saga.epub");
        await File.WriteAllTextAsync(bookPath, "epub-bytes");
        _overlongDirectories.Add(OverlongPath.CreateDirectoryHoldingAnUnreachableFile(workDirectory, ".part"));
        SetupCreateBook(new CondensedBookFile(bookPath, "Saga.epub"));

        var result = await CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "all" },
            CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        await file.FileStream.DisposeAsync();
        Assert.Null(await Record.ExceptionAsync(() => _responseFeature.RunOnCompletedAsync()));
        Assert.True(Directory.Exists(workDirectory));
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state!.ToString() == "Failed to clean up the condensed book work directory"),
            It.Is<Exception?>(ex => ex is IOException),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task DownloadCondensedBook_RejectedRequestsAreBadRequests()
    {
        var controller = CreateController();
        var request = new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "count" };

        SetupCreateBookThrows(new ArgumentException("Book index 4 is out of range."));
        Assert.Equal("Book index 4 is out of range.", ErrorOf<BadRequestObjectResult>(await controller.DownloadCondensedBook(request, CancellationToken.None)));

        SetupCreateBookThrows(new InvalidOperationException("Nothing left to condense."));
        Assert.Equal("Nothing left to condense.", ErrorOf<BadRequestObjectResult>(await controller.DownloadCondensedBook(request, CancellationToken.None)));

        Assert.Empty(_responseFeature.OnCompletedCallbacks);
        VerifyLogged(LogLevel.Error, Times.Never());
    }

    [Fact]
    public async Task DownloadCondensedBook_UnexpectedBuildFailureIsServerErrorAndLogged()
    {
        SetupCreateBookThrows(new IOException("Disk full"));

        var result = Assert.IsType<ObjectResult>(await CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "all" },
            CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("Failed to build the condensed book: Disk full", ToJson(result.Value).GetProperty("error").GetString());
        VerifyLogged(LogLevel.Error, Times.Once());
    }

    [Fact]
    public async Task DownloadCondensedBook_CancellationIsNotTurnedIntoAServerError()
    {
        SetupCreateBookThrows(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateController().DownloadCondensedBook(
            new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "all" },
            CancellationToken.None));

        VerifyLogged(LogLevel.Error, Times.Never());
    }

    // ─── Background condensed builds ────────────────────────────────────────

    [Fact]
    public async Task StartCondenseBuild_WithoutBody_IsBadRequest()
    {
        var result = await CreateController().StartCondenseBuild(null!, CancellationToken.None);

        Assert.Equal("Request body is required", ErrorOf<BadRequestObjectResult>(result.Result));
        _builds.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartCondenseBuild_UnresolvableSelectionIsReportedWithoutStarting()
    {
        SetupSeries(Series());

        var result = await CreateController().StartCondenseBuild(
            new EmailController.CondenseRequest { SeriesId = SeriesId },
            CancellationToken.None);

        Assert.Equal("Series has no issues to send", ErrorOf<BadRequestObjectResult>(result.Result));
        _builds.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartCondenseBuild_StartsABuildOwnedByTheCaller()
    {
        var buildId = Guid.NewGuid();
        CondensedBookBuildRequest? captured = null;
        _builds.Setup(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<CondensedBookBuildRequest, string?, CancellationToken>((request, _, _) => captured = request)
            .ReturnsAsync(Build(buildId, CondensedBookBuildStatus.Queued));
        var files = new List<string> { "/lib/Saga/Saga 001.cbz", "/lib/Saga/Saga 002.cbz" };

        var result = await CreateController().StartCondenseBuild(
            new EmailController.CondenseRequest
            {
                Files = files,
                CondenseMode = "count",
                IssuesPerBook = 2,
                BookIndex = 3,
                DeviceId = 4,
                SkipAlreadyDelivered = false,
                DeliveryFormat = "azw3"
            },
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var json = ToJson(accepted.Value);
        Assert.Equal(buildId.ToString(), json.GetProperty("build_id").GetString());
        Assert.Equal("queued", json.GetProperty("status").GetString());
        _builds.Verify(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), UserId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal(files, captured!.FilePaths);
        Assert.Equal("count", captured.CondenseMode);
        Assert.Equal(2, captured.IssuesPerBook);
        Assert.Equal(3, captured.BookIndex);
        Assert.False(captured.PreserveIssueOrder);
        Assert.Equal(4, captured.DeviceId);
        Assert.False(captured.SkipAlreadyDelivered);
        Assert.Equal("azw3", captured.DeliveryFormat);
    }

    [Fact]
    public async Task StartCondenseBuild_ForASeriesPreservesIssueOrder()
    {
        SetupSeries(Series("/lib/Saga/Saga 001.cbz"));
        CondensedBookBuildRequest? captured = null;
        _builds.Setup(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<CondensedBookBuildRequest, string?, CancellationToken>((request, _, _) => captured = request)
            .ReturnsAsync(Build(Guid.NewGuid()));

        var result = await CreateController(Anonymous()).StartCondenseBuild(
            new EmailController.CondenseRequest { SeriesId = SeriesId, CondenseMode = "all" },
            CancellationToken.None);

        Assert.IsType<AcceptedResult>(result.Result);
        Assert.True(captured!.PreserveIssueOrder);
        Assert.Equal(new[] { "/lib/Saga/Saga 001.cbz" }, captured.FilePaths);
        // A caller without a user id still gets a build, owned by nobody.
        _builds.Verify(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartCondenseBuild_RejectedRequestsAreBadRequests()
    {
        var controller = CreateController();
        var request = new EmailController.CondenseRequest { Files = new List<string> { "/lib/a.cbz" }, CondenseMode = "count" };

        _builds.Setup(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Condensing by count requires at least 2 issues per book."));
        Assert.Equal("Condensing by count requires at least 2 issues per book.", ErrorOf<BadRequestObjectResult>((await controller.StartCondenseBuild(request, CancellationToken.None)).Result));

        _builds.Setup(b => b.StartAsync(It.IsAny<CondensedBookBuildRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Too many builds are running."));
        Assert.Equal("Too many builds are running.", ErrorOf<BadRequestObjectResult>((await controller.StartCondenseBuild(request, CancellationToken.None)).Result));
    }

    [Fact]
    public void GetCondenseBuilds_ListsOnlyTheCallersBuildsWithTheirProgress()
    {
        var running = Build(Guid.NewGuid());
        var failed = Build(Guid.NewGuid(), CondensedBookBuildStatus.Failed) with
        {
            CompletedAt = CreatedAt.AddMinutes(2),
            Error = "Archive is corrupt",
            FileSizeBytes = 2048,
            ExpiresAt = CreatedAt.AddHours(1)
        };
        _builds.Setup(b => b.List(UserId)).Returns(new[] { running, failed });

        var json = OkJson(CreateController().GetCondenseBuilds());

        var builds = json.GetProperty("builds").EnumerateArray().ToList();
        Assert.Equal(2, builds.Count);
        var first = builds[0];
        Assert.Equal(running.BuildId.ToString(), first.GetProperty("build_id").GetString());
        Assert.Equal("running", first.GetProperty("status").GetString());
        Assert.Equal("Saga #1-6", first.GetProperty("display_name").GetString());
        Assert.Equal(6, first.GetProperty("issue_count").GetInt32());
        Assert.Equal(CreatedAt, first.GetProperty("created_at").GetDateTime());
        Assert.Equal(CreatedAt.AddSeconds(5), first.GetProperty("started_at").GetDateTime());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("completed_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
        Assert.Equal("azw3", first.GetProperty("delivery_format").GetString());
        var progress = first.GetProperty("progress");
        Assert.Equal("encoding", progress.GetProperty("phase").GetString());
        Assert.Equal(30, progress.GetProperty("completed_pages").GetInt32());
        Assert.Equal(120, progress.GetProperty("total_pages").GetInt32());
        Assert.Equal(2, progress.GetProperty("completed_issues").GetInt32());
        Assert.Equal(6, progress.GetProperty("total_issues").GetInt32());
        Assert.Equal("Saga 003", progress.GetProperty("current_issue").GetString());
        Assert.Equal(1, progress.GetProperty("pass").GetInt32());
        Assert.Equal(2, progress.GetProperty("total_passes").GetInt32());
        Assert.Equal(25, progress.GetProperty("percentage").GetInt32());

        var second = builds[1];
        Assert.Equal("Archive is corrupt", second.GetProperty("error").GetString());
        Assert.Equal(2048, second.GetProperty("file_size_bytes").GetInt64());
        Assert.Equal(CreatedAt.AddHours(1), second.GetProperty("expires_at").GetDateTime());
    }

    [Fact]
    public void GetCondenseBuild_ReturnsTheBuildOrNotFound()
    {
        var buildId = Guid.NewGuid();
        _builds.Setup(b => b.Get(buildId, UserId)).Returns(Build(buildId));
        var controller = CreateController();

        Assert.Equal(buildId.ToString(), OkJson(controller.GetCondenseBuild(buildId)).GetProperty("build_id").GetString());
        Assert.Equal("Build not found", ErrorOf<NotFoundObjectResult>(controller.GetCondenseBuild(Guid.NewGuid()).Result));
    }

    [Fact]
    public void GetCondenseBuild_IsScopedToTheCaller()
    {
        var buildId = Guid.NewGuid();
        _builds.Setup(b => b.Get(buildId, UserId)).Returns(Build(buildId));

        // Another user (or an anonymous caller) asking for the same id gets a 404.
        var result = CreateController(Anonymous()).GetCondenseBuild(buildId);

        Assert.Equal("Build not found", ErrorOf<NotFoundObjectResult>(result.Result));
        _builds.Verify(b => b.Get(buildId, null), Times.Once);
    }

    [Fact]
    public void DownloadCondenseBuild_UnknownBuildIsNotFound()
    {
        var result = CreateController().DownloadCondenseBuild(Guid.NewGuid());

        Assert.Equal("Build not found", ErrorOf<NotFoundObjectResult>(result));
        _builds.Verify(b => b.GetCompletedFile(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void DownloadCondenseBuild_BuildThatIsNotFinishedIsConflict()
    {
        var buildId = Guid.NewGuid();
        _builds.Setup(b => b.Get(buildId, UserId)).Returns(Build(buildId, CondensedBookBuildStatus.Running));
        _builds.Setup(b => b.GetCompletedFile(buildId, UserId)).Returns((CondensedBookFile?)null);

        var result = CreateController().DownloadCondenseBuild(buildId);

        Assert.Equal("The book is not ready to download (status: running).", ErrorOf<ConflictObjectResult>(result));
        VerifyLogged(LogLevel.Error, Times.Never());
    }

    [Fact]
    public void DownloadCondenseBuild_CompletedBuildWhoseFileIsGoneIsConflictAndLoggedAsADefect()
    {
        var buildId = Guid.NewGuid();
        _builds.Setup(b => b.Get(buildId, UserId)).Returns(Build(buildId, CondensedBookBuildStatus.Completed));
        _builds.Setup(b => b.GetCompletedFile(buildId, UserId)).Returns((CondensedBookFile?)null);

        var result = CreateController().DownloadCondenseBuild(buildId);

        Assert.Equal("The built book is no longer available; build it again.", ErrorOf<ConflictObjectResult>(result));
        VerifyLogged(LogLevel.Error, Times.Once());
    }

    [Theory]
    [InlineData("Saga.epub", "application/epub+zip")]
    [InlineData("Saga.azw3", "application/x-mobi8-ebook")]
    public async Task DownloadCondenseBuild_StreamsTheFinishedBookAndKeepsItForRetries(string fileName, string expectedContentType)
    {
        var buildId = Guid.NewGuid();
        var path = Path.Combine(_tempDir, fileName);
        await File.WriteAllTextAsync(path, "book-bytes");
        _builds.Setup(b => b.Get(buildId, UserId)).Returns(Build(buildId, CondensedBookBuildStatus.Completed));
        _builds.Setup(b => b.GetCompletedFile(buildId, UserId)).Returns(new CondensedBookFile(path, fileName));

        var file = Assert.IsType<FileStreamResult>(CreateController().DownloadCondenseBuild(buildId));

        Assert.Equal(expectedContentType, file.ContentType);
        Assert.Equal(fileName, file.FileDownloadName);
        using (var reader = new StreamReader(file.FileStream))
        {
            Assert.Equal("book-bytes", await reader.ReadToEndAsync());
        }

        // Unlike the one-shot download, the build's file survives the response
        // so an interrupted download can be retried.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CancelCondenseBuild_ReportsTheOutcome()
    {
        var running = Guid.NewGuid();
        var finished = Guid.NewGuid();
        _builds.Setup(b => b.Get(running, UserId)).Returns(Build(running));
        _builds.Setup(b => b.Get(finished, UserId)).Returns(Build(finished, CondensedBookBuildStatus.Completed));
        _builds.Setup(b => b.Cancel(running, UserId)).Returns(true);
        _builds.Setup(b => b.Cancel(finished, UserId)).Returns(false);
        var controller = CreateController();

        Assert.True(OkJson(controller.CancelCondenseBuild(running)).GetProperty("cancelled").GetBoolean());
        Assert.Equal("The build has already finished.", ErrorOf<ConflictObjectResult>(controller.CancelCondenseBuild(finished).Result));
    }

    [Fact]
    public void CancelCondenseBuild_UnknownBuildIsNotFound()
    {
        var result = CreateController().CancelCondenseBuild(Guid.NewGuid());

        Assert.Equal("Build not found", ErrorOf<NotFoundObjectResult>(result.Result));
        _builds.Verify(b => b.Cancel(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void DeleteCondenseBuild_DiscardsTheBuildOrReportsNotFound()
    {
        var known = Guid.NewGuid();
        _builds.Setup(b => b.Discard(known, UserId)).Returns(true);
        var controller = CreateController();

        Assert.IsType<NoContentResult>(controller.DeleteCondenseBuild(known));
        Assert.Equal("Build not found", ErrorOf<NotFoundObjectResult>(controller.DeleteCondenseBuild(Guid.NewGuid())));
    }

    // ─── Subscriptions and deliveries ───────────────────────────────────────

    [Fact]
    public async Task GetSubscriptions_FiltersBySeries()
    {
        var subscription = Subscription();
        _devices.Setup(d => d.GetSubscriptionsAsync("Saga", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { subscription });

        var json = OkJson(await CreateController().GetSubscriptions("Saga", CancellationToken.None));

        Assert.Equal(11, Assert.Single(json.GetProperty("subscriptions").EnumerateArray()).GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task UpsertSubscription_RequiresASeriesTitle()
    {
        var controller = CreateController();

        Assert.Equal("Series title is required", ErrorOf<BadRequestObjectResult>((await controller.UpsertSubscription(null!, CancellationToken.None)).Result));
        Assert.Equal("Series title is required", ErrorOf<BadRequestObjectResult>(
            (await controller.UpsertSubscription(new EmailController.SubscriptionRequest { SeriesTitle = " " }, CancellationToken.None)).Result));
        _devices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpsertSubscription_SavesTheSubscription()
    {
        var subscription = Subscription();
        _devices.Setup(d => d.UpsertSubscriptionAsync("Saga", 2, "device", false, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);

        var result = await CreateController().UpsertSubscription(
            new EmailController.SubscriptionRequest { SeriesTitle = "Saga", DeviceId = 2, DeliveryFormat = "device", Enabled = false },
            CancellationToken.None);

        Assert.Same(subscription, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UpsertSubscription_UnknownDeviceIsNotFoundAndInvalidFormatIsBadRequest()
    {
        var controller = CreateController();
        var request = new EmailController.SubscriptionRequest { SeriesTitle = "Saga", DeviceId = 404 };

        _devices.Setup(d => d.UpsertSubscriptionAsync("Saga", 404, null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesEmailSubscriptionDto?)null);
        Assert.Equal("Device not found", ErrorOf<NotFoundObjectResult>((await controller.UpsertSubscription(request, CancellationToken.None)).Result));

        _devices.Setup(d => d.UpsertSubscriptionAsync("Saga", 404, null, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unsupported delivery format 'pdf'."));
        Assert.Equal("Unsupported delivery format 'pdf'.", ErrorOf<BadRequestObjectResult>((await controller.UpsertSubscription(request, CancellationToken.None)).Result));
    }

    [Fact]
    public async Task DeleteSubscription_ReportsWhetherTheSubscriptionExisted()
    {
        _devices.Setup(d => d.DeleteSubscriptionAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = CreateController();

        var ok = Assert.IsType<OkObjectResult>(await controller.DeleteSubscription(1, CancellationToken.None));
        Assert.Equal("Subscription removed", ToJson(ok.Value).GetProperty("message").GetString());
        Assert.Equal("Subscription not found", ErrorOf<NotFoundObjectResult>(await controller.DeleteSubscription(2, CancellationToken.None)));
    }

    [Fact]
    public async Task GetDeliveries_ReturnsTheRecentDeliveries()
    {
        _email.Setup(e => e.GetRecentDeliveriesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Delivery(1, "/lib/a.cbz"), Delivery(2, "/lib/b.cbz") });

        var json = OkJson(await CreateController().GetDeliveries(10, CancellationToken.None));

        Assert.Equal(new[] { 1, 2 }, json.GetProperty("deliveries").EnumerateArray().Select(d => d.GetProperty("Id").GetInt32()));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private EmailController CreateController(ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, UserId) }, "Test"))
        };
        httpContext.Features.Set<IHttpResponseFeature>(_responseFeature);

        return new EmailController(
            _devices.Object,
            _email.Object,
            _builds.Object,
            _seriesLibrary.Object,
            Mock.Of<IOptionsMonitor<AppSettings>>(m => m.CurrentValue == _settings),
            _logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement OkJson<T>(ActionResult<T> result) =>
        ToJson(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static string? ErrorOf<TResult>(IActionResult? result)
        where TResult : ObjectResult =>
        ToJson(Assert.IsType<TResult>(result).Value).GetProperty("error").GetString();

    private void VerifyLogged(LogLevel level, Times times) =>
        _logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), times);

    private void SetupSeries(SeriesIssuesResult? series) =>
        _seriesLibrary
            .Setup(s => s.GetSeriesIssuesAsync(SeriesId, null, 1, MaxSeriesIssues, It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

    private void SetupQueueFiles(EmailQueueResult result) =>
        _email
            .Setup(e => e.QueueFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void SetupQueueCondensed(EmailQueueResult result) =>
        _email
            .Setup(e => e.QueueCondensedFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void VerifyNoPlainQueue() =>
        _email.Verify(e => e.QueueFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNoCondensedQueue() =>
        _email.Verify(e => e.QueueCondensedFilesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    private void SetupCreateBook(CondensedBookFile book) =>
        _email
            .Setup(e => e.CreateCondensedBookAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<IProgress<EpubConversionProgress>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(book);

    private void SetupCreateBookThrows(Exception exception) =>
        _email
            .Setup(e => e.CreateCondensedBookAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<IProgress<EpubConversionProgress>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

    private static SeriesIssuesResult Series(params string[] files) => new()
    {
        Id = SeriesId,
        Title = "Saga",
        IssueCount = files.Length,
        TotalPages = 1,
        Issues = files.Select(f => new SeriesIssueDto { FilePath = f, FileName = Path.GetFileName(f) }).ToList()
    };

    private static EreaderDeviceDto Device(int id) =>
        new(id, $"Kindle {id}", $"device{id}@kindle.test", EmailDeliveryFormat.Epub, CreatedAt, CreatedAt);

    private static SeriesEmailSubscriptionDto Subscription() =>
        new(11, "saga", "Saga", 2, "Kindle", "me@kindle.test", EmailDeliveryFormat.Device, true, null);

    private static ComicEmailDeliveryDto Delivery(int id, string path) =>
        new(id, path, Path.GetFileName(path), 1, "Kindle", "me@kindle.test", EmailDeliveryFormat.Epub,
            EmailDeliveryStatus.Pending, EmailDeliverySource.Manual, null, CreatedAt, null);

    private static EmailQueueResult QueueResult(string[] queued, Dictionary<string, string>? skipped = null) =>
        new(queued.Select((path, i) => Delivery(i + 1, path)).ToList(), skipped ?? new Dictionary<string, string>());

    private static CondensedBookBuildDto Build(Guid id, string status = CondensedBookBuildStatus.Running) => new(
        id,
        status,
        "Saga #1-6",
        6,
        new CondensedBookBuildProgress("encoding", 30, 120, 2, 6, "Saga 003", 1, 2),
        CreatedAt,
        CreatedAt.AddSeconds(5),
        CompletedAt: null,
        Error: null,
        FileSizeBytes: null,
        ExpiresAt: null,
        DeliveryFormat: EmailDeliveryFormat.Azw3);

    /// <summary>
    /// Records <c>Response.OnCompleted</c> callbacks (the default feature drops
    /// them) so a test can play the end of the response.
    /// </summary>
    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        public List<(Func<object, Task> Callback, object State)> OnCompletedCallbacks { get; } = new();

        public override void OnCompleted(Func<object, Task> callback, object state) =>
            OnCompletedCallbacks.Add((callback, state));

        public async Task RunOnCompletedAsync()
        {
            foreach (var (callback, state) in OnCompletedCallbacks)
            {
                await callback(state);
            }
        }
    }

    /// <summary>
    /// Builds a file that exists but cannot be reached by path on Linux: its
    /// absolute path is longer than PATH_MAX (4096 bytes), so every path-based
    /// call on it fails with ENAMETOOLONG, even for root, which ignores
    /// permission bits. Its directory can still be listed. The file name uses
    /// three-byte UTF-8 characters so the path overflows the kernel's limit in
    /// bytes while staying under 4096 characters, which .NET needs to build the
    /// path at all. The file is written under a short directory name that is
    /// lengthened afterwards, because it cannot be created at its final path;
    /// <see cref="Release"/> shortens the name again so the tree can be deleted.
    /// </summary>
    private static class OverlongPath
    {
        private const int PathMax = 4096;
        private const string ShortLeafName = "s";
        private static readonly string NestedName = new('d', 100);
        private static readonly string LeafName = new('l', 250);
        private static readonly string FileStem = new('\u65E5', 80);

        public static string CreateDirectoryHoldingAnUnreachableFile(string parent, string extension)
        {
            var fileName = FileStem + extension;
            var nested = parent;
            while (Utf8Length(nested) + 1 + LeafName.Length + 1 + Utf8Length(fileName) < PathMax)
            {
                nested = Path.Combine(nested, NestedName);
            }

            var shortLeaf = Path.Combine(nested, ShortLeafName);
            Directory.CreateDirectory(shortLeaf);
            File.WriteAllText(Path.Combine(shortLeaf, fileName), "unreachable");

            var leaf = Path.Combine(nested, LeafName);
            Directory.Move(shortLeaf, leaf);
            return leaf;
        }

        public static void Release(string leaf)
        {
            if (Directory.Exists(leaf))
            {
                Directory.Move(leaf, Path.Combine(Path.GetDirectoryName(leaf)!, ShortLeafName));
            }
        }

        private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
    }
}
