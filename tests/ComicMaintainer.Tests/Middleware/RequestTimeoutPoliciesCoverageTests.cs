using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ComicMaintainer.Tests.Middleware;

public class RequestTimeoutPoliciesCoverageTests
{
    private static async Task<LogEvent> TimeOutAsync(Endpoint? endpoint, Action<IServiceCollection>? configureServices = null)
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(logger));
        configureServices?.Invoke(services);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = "/api/files/export";
        context.Items[RequestCorrelationMiddleware.ItemsKey] = "corr-timeout";
        if (endpoint is not null)
        {
            context.SetEndpoint(endpoint);
        }

        await RequestTimeoutPolicies.WriteTimedOutResponseAsync(context);

        return Assert.Single(sink.Events);
    }

    private static RouteEndpoint RoutedEndpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), order: 0, new EndpointMetadataCollection(metadata), pattern);

    private static double ReportedSeconds(LogEvent logEvent) =>
        Convert.ToDouble(((ScalarValue)logEvent.Properties["TimeoutSeconds"]).Value);

    [Fact]
    public async Task ExplicitTimeoutOnTheEndpoint_IsReported()
    {
        var logEvent = await TimeOutAsync(
            RoutedEndpoint("/api/files/export", new RequestTimeoutAttribute(milliseconds: 90_000)),
            services => services.AddRequestTimeouts(options =>
                options.DefaultPolicy = RequestTimeoutPolicies.Create(RequestTimeoutPolicies.DefaultTimeout)));

        Assert.Equal(90, ReportedSeconds(logEvent));
        Assert.Equal(LogEventLevel.Error, logEvent.Level);
    }

    [Fact]
    public async Task WithoutRequestTimeoutServices_ReportsTheDefaultTimeout()
    {
        var logEvent = await TimeOutAsync(RoutedEndpoint("/api/files/export"));

        Assert.Equal(RequestTimeoutPolicies.DefaultTimeout.TotalSeconds, ReportedSeconds(logEvent));
    }

    [Fact]
    public async Task WithoutADefaultPolicy_ReportsTheDefaultTimeout()
    {
        var logEvent = await TimeOutAsync(
            RoutedEndpoint("/api/files/export"),
            services => services.AddRequestTimeouts(_ => { }));

        Assert.Equal(RequestTimeoutPolicies.DefaultTimeout.TotalSeconds, ReportedSeconds(logEvent));
    }

    [Fact]
    public async Task DefaultPolicyWithoutATimeout_ReportsTheDefaultTimeout()
    {
        var logEvent = await TimeOutAsync(
            RoutedEndpoint("/api/files/export"),
            services => services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy()));

        Assert.Equal(RequestTimeoutPolicies.DefaultTimeout.TotalSeconds, ReportedSeconds(logEvent));
    }

    [Fact]
    public async Task UnknownPolicyName_FallsBackToTheDefaultPolicy()
    {
        var logEvent = await TimeOutAsync(
            RoutedEndpoint("/api/files/export", new RequestTimeoutAttribute("NoSuchPolicy")),
            services => services.AddRequestTimeouts(options =>
                options.DefaultPolicy = RequestTimeoutPolicies.Create(TimeSpan.FromSeconds(45))));

        Assert.Equal(45, ReportedSeconds(logEvent));
    }

    [Fact]
    public async Task NonRouteEndpoint_IsReportedAsUnrouted()
    {
        var logEvent = await TimeOutAsync(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "fallback"));

        Assert.Contains("(unrouted)", logEvent.MessageTemplate.Text);
        Assert.DoesNotContain("/api/files/export", logEvent.MessageTemplate.Text);
    }

    [Fact]
    public async Task RoutePatternWithoutRawText_IsReportedAsUnrouted()
    {
        var pattern = RoutePatternFactory.Pattern(
            RoutePatternFactory.Segment(RoutePatternFactory.LiteralPart("api")),
            RoutePatternFactory.Segment(RoutePatternFactory.LiteralPart("files")));
        Assert.Null(pattern.RawText);

        var logEvent = await TimeOutAsync(new RouteEndpoint(_ => Task.CompletedTask, pattern, 0, EndpointMetadataCollection.Empty, "generated"));

        Assert.Contains("(unrouted)", logEvent.MessageTemplate.Text);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
