using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Logging;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ComicMaintainer.Tests.Middleware;

/// <summary>
/// A request timeout used to be the one failure nobody heard about. ASP.NET Core
/// handles it internally, logging "Timeout exception handled." at Warning and
/// returning an empty 504, so a condensed book that took longer than the default
/// policy allowed failed with no log worth reading, no automated issue and
/// nothing on screen but a status code. These tests pin the two properties that
/// close that hole: the timeout is logged at a level the error reporter
/// captures, and each endpoint is distinguishable once it gets there.
/// </summary>
public class RequestTimeoutPolicyTests
{
    [Fact]
    public async Task TimedOutRequestIsLoggedAtErrorSoItReachesAutomatedReporting()
    {
        var (sink, context) = await TimeOutAsync("/api/email/condense-builds/{buildId:guid}/download");

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, logEvent.Level);

        // The whole point: an Error below this bar is never fingerprinted,
        // queued or filed as a GitHub issue.
        Assert.True(ErrorReportingSink.ShouldReport(logEvent, EnabledReporting()));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task TimeoutIsReportedWithoutTheCancellationExceptionThatCausedIt()
    {
        var (sink, _) = await TimeOutAsync("/api/email/condense-plan");

        // ErrorReportingSink drops anything carrying an OperationCanceledException,
        // so attaching the timeout's own cancellation would put this report
        // straight back into the hole it exists to close.
        Assert.Null(Assert.Single(sink.Events).Exception);
    }

    [Fact]
    public async Task EachEndpointFingerprintsSeparatelyThroughItsMessageTemplate()
    {
        var (download, _) = await TimeOutAsync("/api/email/condense-builds/{buildId:guid}/download");
        var (plan, _) = await TimeOutAsync("/api/email/condense-plan");

        var downloadEvent = Assert.Single(download.Events);
        var planEvent = Assert.Single(plan.Events);

        // ErrorReportFingerprint hashes the message template, so a constant one
        // would collapse every timed-out endpoint in the application into a
        // single issue.
        Assert.NotEqual(downloadEvent.MessageTemplate.Text, planEvent.MessageTemplate.Text);
        Assert.Contains("/api/email/condense-builds/{buildId:guid}/download", downloadEvent.RenderMessage());
        Assert.Contains("/api/email/condense-plan", planEvent.RenderMessage());
    }

    [Fact]
    public async Task RouteParametersAreEscapedRatherThanTreatedAsPropertyHoles()
    {
        var (sink, _) = await TimeOutAsync("/api/email/condense-builds/{buildId:guid}/download");

        var logEvent = Assert.Single(sink.Events);

        // A brace in a message template is a property hole. Splicing the route
        // in unescaped gives the logger more holes than arguments, binding
        // fails and the event is dropped — silently un-reporting exactly the
        // parameterised routes (the build download among them) this change
        // exists to surface.
        Assert.False(logEvent.Properties.ContainsKey("buildId"));
        Assert.Equal(
            new[] { "TimeoutSeconds", "Method", "Path", "CorrelationId", "SourceContext" }.OrderBy(n => n),
            logEvent.Properties.Keys.OrderBy(n => n));
    }

    [Fact]
    public async Task TimeoutIsAttributedToItselfRatherThanToTheReporterItWouldSilence()
    {
        var (sink, _) = await TimeOutAsync("/api/email/condense-plan");

        var sourceContext = Assert.Single(sink.Events).Properties["SourceContext"].ToString().Trim('"');

        // ErrorReportingSink drops events whose SourceContext names the
        // reporting machinery, to stop a reporting failure reporting itself.
        Assert.Contains("RequestTimeoutPolicies", sourceContext);
        Assert.DoesNotContain("ErrorReporting", sourceContext);
    }

    [Fact]
    public async Task UnroutedRequestsKeepTheirPathOutOfTheFingerprint()
    {
        var (sink, _) = await TimeOutAsync(routePattern: null, path: "/api/files/Batman%20(2016)%20001.cbz");

        var logEvent = Assert.Single(sink.Events);

        // A path can carry a library file name. It belongs in the rendered
        // message, never in the template that identifies the issue.
        Assert.Contains("(unrouted)", logEvent.MessageTemplate.Text);
        Assert.DoesNotContain("Batman", logEvent.MessageTemplate.Text);
        Assert.Contains("Batman", logEvent.RenderMessage());
    }

    [Fact]
    public async Task TimeoutCarriesTheCorrelationIdTheUserWasShown()
    {
        var (sink, context) = await TimeOutAsync("/api/email/condense-plan", correlationId: "trace-me");

        Assert.Contains("trace-me", Assert.Single(sink.Events).RenderMessage());

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("trace-me", document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task TimedOutRequestExplainsItselfInsteadOfReturningAnEmptyBody()
    {
        var (_, context) = await TimeOutAsync("/api/email/condense-plan");

        Assert.StartsWith("application/json", context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);

        var error = document.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        // Without this the UI could only say "HTTP error! status: 504".
        Assert.Contains("longer than", error);
    }

    [Fact]
    public void DefaultPolicyAnswersTimeoutsItself()
    {
        var policy = RequestTimeoutPolicies.Create(RequestTimeoutPolicies.DefaultTimeout);

        Assert.Equal(RequestTimeoutPolicies.DefaultTimeout, policy.Timeout);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, policy.TimeoutStatusCode);
        Assert.NotNull(policy.WriteTimeoutResponse);
    }

    [Fact]
    public async Task ReportsTheLimitTheEndpointActuallyHadRatherThanTheDefault()
    {
        var (sink, _) = await TimeOutAsync(
            "/api/email/condense-builds",
            metadata: new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutAttribute(
                RequestTimeoutPolicies.LongRunning));

        // An issue saying "timed out after 30s" for an endpoint allowed ten
        // minutes sends whoever reads it looking in the wrong place.
        Assert.Contains(
            $"{RequestTimeoutPolicies.LongRunningTimeout.TotalSeconds}",
            Assert.Single(sink.Events).RenderMessage());
    }

    private static async Task<(CapturingSink Sink, HttpContext Context)> TimeOutAsync(
        string? routePattern,
        string path = "/api/email/condense-plan",
        string correlationId = "corr-1",
        object? metadata = null)
    {
        var sink = new CapturingSink();
        // A private logger rather than the process-wide Log.Logger: xUnit runs
        // test classes in parallel and a shared static logger would mix events
        // between them.
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(logger))
                // Registered exactly as Program.cs does, so the reported limit
                // is resolved from the real policies rather than a stand-in.
                .AddRequestTimeouts(options =>
                {
                    options.DefaultPolicy = RequestTimeoutPolicies.Create(RequestTimeoutPolicies.DefaultTimeout);
                    options.AddPolicy(
                        RequestTimeoutPolicies.LongRunning,
                        RequestTimeoutPolicies.Create(RequestTimeoutPolicies.LongRunningTimeout));
                })
                .BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Items[RequestCorrelationMiddleware.ItemsKey] = correlationId;
        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;

        if (routePattern is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(routePattern),
                order: 0,
                new EndpointMetadataCollection(metadata is null ? Array.Empty<object>() : new[] { metadata }),
                displayName: routePattern));
        }

        await RequestTimeoutPolicies.WriteTimedOutResponseAsync(context);

        return (sink, context);
    }

    private static AppSettings EnabledReporting() => new()
    {
        ErrorReportingEnabled = true,
        ErrorReportingMinimumLevel = "Error"
    };

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
