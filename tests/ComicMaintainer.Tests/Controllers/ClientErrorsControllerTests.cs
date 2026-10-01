using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The endpoint that lets a failure in the browser reach the automated issue
/// tracker. Before it existed, reporting only ever saw server-side failures: a
/// front-end defect showed the user a banner and told nobody.
/// </summary>
public class ClientErrorsControllerTests
{
    [Fact]
    public void ReportClientError_QueuesAReportSoTheDispatcherCanFileAnIssue()
    {
        var queue = new ErrorReportQueue();
        var controller = CreateController(queue, reportingEnabled: true);

        var result = controller.ReportClientError(new ClientErrorRequest
        {
            Name = "SyntaxError",
            Message = "Invalid or unexpected token",
            Stack = "    at HTMLButtonElement.onclick (https://comics.example.com/js/main.js?v=2.0.316:9180:62)",
            Source = "https://comics.example.com/js/main.js?v=2.0.316",
            Url = "https://comics.example.com/index.html",
            Kind = ClientErrorKind.Error
        });

        Assert.IsType<AcceptedResult>(result);

        var report = Assert.Single(Drain(queue));
        Assert.Equal("Error", report.Level);
        Assert.Equal("SyntaxError", report.ExceptionType);
        Assert.Equal(ClientErrorsController.BrowserSourceContext, report.SourceContext);
        Assert.Contains("Invalid or unexpected token", report.RenderedMessage);
        Assert.Contains("at HTMLButtonElement.onclick", report.StackTrace);
    }

    [Fact]
    public void ReportClientError_BuildsNothingWhenReportingIsSwitchedOff()
    {
        // Reporting is opt-in and the destination is the project's own
        // repository, so an instance that never opted in must not turn
        // browser-supplied text into a report at all.
        var queue = new ErrorReportQueue();
        var controller = CreateController(queue, reportingEnabled: false);

        var result = controller.ReportClientError(new ClientErrorRequest { Name = "TypeError" });

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void ReportClientError_RejectsAnEmptyBody()
    {
        var controller = CreateController(new ErrorReportQueue(), reportingEnabled: true);

        Assert.IsType<BadRequestObjectResult>(controller.ReportClientError(null!));
    }

    [Fact]
    public void ReportClientError_AcceptsTheReportEvenWhenTheQueueIsFull()
    {
        // The browser is in the middle of handling its own failure; telling it
        // reporting failed would only invite a retry.
        var queue = new ErrorReportQueue(capacity: 1);
        var controller = CreateController(queue, reportingEnabled: true);

        controller.ReportClientError(new ClientErrorRequest { Name = "TypeError", Message = "first" });
        var result = controller.ReportClientError(new ClientErrorRequest { Name = "TypeError", Message = "second" });

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public void ReportClientError_FingerprintsOneDefectIdenticallyAcrossBrowsers()
    {
        // Chromium and Firefox describe the same defect with different stack
        // syntax and a different cache-busting query. Fingerprinting the raw
        // text would file one issue per browser and another on every release.
        var chromium = Report(new ClientErrorRequest
        {
            Name = "TypeError",
            Message = "build is undefined",
            Stack = "TypeError: build is undefined\n"
                + "    at renderEmailCondenseBuilds (https://comics.example.com/js/main.js?v=2.0.316:9180:62)",
            Url = "https://comics.example.com/index.html"
        });

        var firefox = Report(new ClientErrorRequest
        {
            Name = "TypeError",
            Message = "build is undefined",
            Stack = "renderEmailCondenseBuilds@https://comics.example.com/js/main.js?v=2.0.401:9180:62",
            Url = "https://comics.example.com/index.html"
        });

        Assert.Equal(chromium.Fingerprint, firefox.Fingerprint);
    }

    [Fact]
    public void ReportClientError_FingerprintsDistinctDefectsDistinctly()
    {
        var first = Report(new ClientErrorRequest
        {
            Name = "TypeError",
            Message = "build is undefined",
            Stack = "    at renderEmailCondenseBuilds (https://comics.example.com/js/main.js:9180:62)",
            Url = "https://comics.example.com/index.html"
        });

        var second = Report(new ClientErrorRequest
        {
            Name = "SyntaxError",
            Message = "Invalid or unexpected token",
            Stack = "    at HTMLButtonElement.onclick (https://comics.example.com/:1:30)",
            Url = "https://comics.example.com/index.html"
        });

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void ReportClientError_FingerprintIgnoresTheVolatilePartsOfAMessage()
    {
        // One defect reported against two different build ids is still one
        // defect; without this it would open an issue per occurrence.
        var first = Report(new ClientErrorRequest
        {
            Name = "Error",
            Message = "Build 3f2504e0-4f89-11d3-9a0c-0305e82c3301 failed",
            Url = "https://comics.example.com/index.html"
        });

        var second = Report(new ClientErrorRequest
        {
            Name = "Error",
            Message = "Build 0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9 failed",
            Url = "https://comics.example.com/index.html"
        });

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void ReportClientError_SeparatesRejectionsFromThrownErrors()
    {
        var thrown = Report(new ClientErrorRequest
        {
            Name = "TypeError",
            Message = "boom",
            Url = "https://comics.example.com/index.html",
            Kind = ClientErrorKind.Error
        });

        var rejected = Report(new ClientErrorRequest
        {
            Name = "TypeError",
            Message = "boom",
            Url = "https://comics.example.com/index.html",
            Kind = ClientErrorKind.UnhandledRejection
        });

        Assert.NotEqual(thrown.Fingerprint, rejected.Fingerprint);
        Assert.Contains("unhandled promise rejection", rejected.RenderedMessage);
    }

    [Fact]
    public void ReportClientError_KeepsBrowserSuppliedTextOutOfTheIssueTitle()
    {
        // The title is built from the exception type, which comes straight from
        // the browser. Anything that is not a plain identifier is discarded
        // rather than sanitised.
        var report = Report(new ClientErrorRequest
        {
            Name = "<img src=x onerror=alert(1)>",
            Message = "boom",
            Url = "https://comics.example.com/index.html"
        });

        Assert.Equal(ClientErrorNormalizer.UnknownErrorName, report.ExceptionType);
    }

    [Fact]
    public void ReportClientError_BoundsAnOversizedStack()
    {
        var report = Report(new ClientErrorRequest
        {
            Name = "RangeError",
            Message = "Maximum call stack size exceeded",
            Stack = string.Join(
                '\n',
                Enumerable.Range(0, 5000).Select(i => $"    at fn{i} (https://comics.example.com/js/main.js:{i}:1)")),
            Url = "https://comics.example.com/index.html"
        });

        var frames = report.StackTrace!
            .Split('\n')
            .Count(line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));

        Assert.True(frames <= ClientErrorNormalizer.MaxStackFrames);
    }

    private static ErrorReport Report(ClientErrorRequest request)
    {
        var queue = new ErrorReportQueue();
        var controller = CreateController(queue, reportingEnabled: true);

        controller.ReportClientError(request);

        return Assert.Single(Drain(queue));
    }

    private static ClientErrorsController CreateController(IErrorReportQueue queue, bool reportingEnabled)
    {
        var settings = new AppSettings { ErrorReportingEnabled = reportingEnabled };
        var controller = new ClientErrorsController(
            new ErrorReportFactory(new TestOptionsMonitor<AppSettings>(settings)),
            queue,
            new TestOptionsMonitor<AppSettings>(settings),
            NullLogger<ClientErrorsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return controller;
    }

    private static List<ErrorReport> Drain(IErrorReportQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var reports = new List<ErrorReport>();

        try
        {
            // ReadAllAsync never completes on its own, so the cancellation is
            // the stop condition rather than an error.
            var enumerator = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                reports.Add(enumerator.Current);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return reports;
    }
}
