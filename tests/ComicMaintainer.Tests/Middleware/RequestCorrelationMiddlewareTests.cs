using System.Text.Json;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Core;

namespace ComicMaintainer.Tests.Middleware;

/// <summary>
/// The correlation id is the only thread tying a failure a user sees on screen
/// to the log lines that explain it, so its three delivery mechanisms — the
/// response header, the Serilog log context and the problem-details body — are
/// each covered here.
/// </summary>
public class RequestCorrelationMiddlewareTests
{
    private static DefaultHttpContext CreateContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        // DefaultHttpContext's response feature drops OnStarting callbacks on
        // the floor, so swap in one that records them the way Kestrel does.
        ctx.Features.Set<IHttpResponseFeature>(new RecordingResponseFeature());
        return ctx;
    }

    [Fact]
    public async Task EchoesGeneratedCorrelationIdOnTheResponse()
    {
        var ctx = CreateContext();
        ctx.TraceIdentifier = "0HN7ABCDEF:00000001";

        var middleware = new RequestCorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);
        await StartResponseAsync(ctx);

        Assert.Equal("0HN7ABCDEF:00000001", ctx.Response.Headers[RequestCorrelationMiddleware.HeaderName]);
        Assert.Equal("0HN7ABCDEF:00000001", ctx.GetCorrelationId());
    }

    [Fact]
    public async Task HonoursACleanClientSuppliedCorrelationId()
    {
        var ctx = CreateContext();
        ctx.Request.Headers[RequestCorrelationMiddleware.HeaderName] = "browser-42";

        var middleware = new RequestCorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);
        await StartResponseAsync(ctx);

        Assert.Equal("browser-42", ctx.Response.Headers[RequestCorrelationMiddleware.HeaderName]);
    }

    [Fact]
    public async Task StripsInjectionCharactersFromAClientSuppliedCorrelationId()
    {
        var ctx = CreateContext();
        // The value is echoed into a response header and written to log files,
        // so CR/LF and whitespace must not survive.
        ctx.Request.Headers[RequestCorrelationMiddleware.HeaderName] = "abc\r\nX-Injected: 1";

        var middleware = new RequestCorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);
        await StartResponseAsync(ctx);

        var echoed = ctx.Response.Headers[RequestCorrelationMiddleware.HeaderName].ToString();
        Assert.DoesNotContain("\r", echoed);
        Assert.DoesNotContain("\n", echoed);
        Assert.DoesNotContain(" ", echoed);
        Assert.False(ctx.Response.Headers.ContainsKey("X-Injected"));
    }

    [Fact]
    public async Task FallsBackToTheTraceIdentifierWhenTheSuppliedIdIsUnusable()
    {
        var ctx = CreateContext();
        ctx.TraceIdentifier = "fallback-id";
        ctx.Request.Headers[RequestCorrelationMiddleware.HeaderName] = "!!!";

        var middleware = new RequestCorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx);

        Assert.Equal("fallback-id", ctx.GetCorrelationId());
    }

    [Fact]
    public void SanitizeCapsTheLengthOfAClientSuppliedId()
    {
        var sanitized = RequestCorrelationMiddleware.Sanitize(new string('a', 500));

        Assert.Equal(RequestCorrelationMiddleware.MaxLength, sanitized.Length);
    }

    [Fact]
    public async Task PushesTheCorrelationIdIntoTheSerilogLogContext()
    {
        var sink = new CapturingSink();
        // A private logger rather than Serilog's process-wide Log.Logger: xUnit
        // runs test classes in parallel, so swapping the static logger would let
        // another test's events reach this sink and would close that test's
        // logger when this one restored the previous instance.
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var ctx = CreateContext();
        ctx.Request.Headers[RequestCorrelationMiddleware.HeaderName] = "trace-me";

        var middleware = new RequestCorrelationMiddleware(_ =>
        {
            logger.Information("inside the request");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx);

        var logEvent = Assert.Single(sink.Events);
        Assert.True(logEvent.Properties.TryGetValue("CorrelationId", out var value));
        Assert.Equal("\"trace-me\"", value!.ToString());

        // The property must not leak past the request.
        sink.Events.Clear();
        logger.Information("outside the request");
        Assert.False(Assert.Single(sink.Events).Properties.ContainsKey("CorrelationId"));
    }

    [Fact]
    public async Task GlobalExceptionHandlerWritesATraceableProblemDocument()
    {
        var ctx = CreateContext();
        ctx.Items[RequestCorrelationMiddleware.ItemsKey] = "problem-ref";
        ctx.Request.Path = "/api/files";
        ctx.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        var handler = new GlobalExceptionHandler(
            ctx.RequestServices.GetRequiredService<IProblemDetailsService>(),
            environment.Object,
            NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        var root = document.RootElement;

        Assert.Equal("problem-ref", root.GetProperty("correlationId").GetString());
        // The exception message can contain paths, SQL and connection strings:
        // it must never reach a production client.
        Assert.DoesNotContain("boom", root.GetProperty("detail").GetString());
        Assert.DoesNotContain("InvalidOperationException", root.ToString());
    }

    [Fact]
    public async Task GlobalExceptionHandlerIgnoresClientAborts()
    {
        var ctx = CreateContext();
        var aborted = new CancellationTokenSource();
        aborted.Cancel();
        ctx.RequestAborted = aborted.Token;

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        var handler = new GlobalExceptionHandler(
            Mock.Of<IProblemDetailsService>(),
            environment.Object,
            NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(ctx, new OperationCanceledException(), CancellationToken.None);

        Assert.True(handled);
        // No problem document, and crucially no 500 recorded for what is just a
        // user navigating away.
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    private static Task StartResponseAsync(HttpContext context)
        => ((RecordingResponseFeature)context.Features.Get<IHttpResponseFeature>()!).FireOnStartingAsync();

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>
    /// Minimal response feature that actually keeps <c>OnStarting</c>
    /// callbacks, which the in-memory default silently discards.
    /// </summary>
    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = new();

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public async Task FireOnStartingAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in _onStarting)
            {
                await callback(state);
            }
        }
    }
}
