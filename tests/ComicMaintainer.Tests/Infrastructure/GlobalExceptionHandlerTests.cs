using System.Text.Json;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Infrastructure;

public class GlobalExceptionHandlerTests
{
    private readonly Mock<ILogger<GlobalExceptionHandler>> _logger = new();

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(name);
        return environment.Object;
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider()
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/files/process";
        context.Response.Body = new MemoryStream();
        context.Items[RequestCorrelationMiddleware.ItemsKey] = "corr-123";
        return context;
    }

    private void VerifyErrorLogged(Exception exception) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString() == "Unhandled exception on (unrouted): POST /api/files/process (correlation id corr-123)"),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

    [Fact]
    public async Task InDevelopment_ProblemDetailsIncludeTheExceptionForDebugging()
    {
        var context = CreateContext();
        var exception = new InvalidOperationException("boom in dev");
        var handler = new GlobalExceptionHandler(
            context.RequestServices.GetRequiredService<IProblemDetailsService>(),
            Environment(Environments.Development),
            _logger.Object);

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        var detail = root.GetProperty("detail").GetString();
        Assert.Contains("InvalidOperationException", detail);
        Assert.Contains("boom in dev", detail);
        Assert.Equal(detail, root.GetProperty("error").GetString());
        Assert.Equal("corr-123", root.GetProperty("correlationId").GetString());
        Assert.Equal("/api/files/process", root.GetProperty("instance").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        VerifyErrorLogged(exception);
    }

    [Fact]
    public async Task ResponseAlreadyStarted_LogsAndLeavesTheResponseAlone()
    {
        var context = CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var problemDetails = new Mock<IProblemDetailsService>(MockBehavior.Strict);
        var exception = new InvalidOperationException("late failure");
        var handler = new GlobalExceptionHandler(problemDetails.Object, Environment(Environments.Production), _logger.Object);

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        problemDetails.VerifyNoOtherCalls();
        VerifyErrorLogged(exception);
    }

    [Fact]
    public async Task CancellationNotCausedByTheClient_IsReportedAsAServerError()
    {
        var context = CreateContext();
        ProblemDetailsContext? written = null;
        var problemDetails = new Mock<IProblemDetailsService>();
        problemDetails
            .Setup(p => p.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(c => written = c)
            .Returns(ValueTask.FromResult(true));
        var exception = new OperationCanceledException("internal timeout");
        var handler = new GlobalExceptionHandler(problemDetails.Object, Environment(Environments.Production), _logger.Object);

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.NotNull(written);
        Assert.Same(exception, written.Exception);
        Assert.Same(context, written.HttpContext);
        Assert.Equal("An unexpected error occurred.", written.ProblemDetails.Title);
        Assert.DoesNotContain("internal timeout", written.ProblemDetails.Detail);
        Assert.Equal(written.ProblemDetails.Detail, written.ProblemDetails.Extensions["error"]);
        VerifyErrorLogged(exception);
    }

    [Fact]
    public async Task ProblemDetailsWriterDeclines_ReturnsFalse()
    {
        var context = CreateContext();
        var problemDetails = new Mock<IProblemDetailsService>();
        problemDetails.Setup(p => p.TryWriteAsync(It.IsAny<ProblemDetailsContext>())).Returns(ValueTask.FromResult(false));
        var handler = new GlobalExceptionHandler(problemDetails.Object, Environment(Environments.Production), _logger.Object);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("x"), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_AnActionsFailureMessageReplacesTheGenericText()
    {
        var context = CreateContext();
        SetFailingEndpoint(context, new FailureMessageAttribute("Error reading page"));
        var handler = CreateHandler(context, Environments.Production);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom with /secret/path"), CancellationToken.None);

        Assert.True(handled);
        var root = await ReadProblemAsync(context);
        Assert.Equal("Error reading page", root.GetProperty("detail").GetString());
        Assert.Equal("Error reading page", root.GetProperty("error").GetString());
        Assert.Equal("corr-123", root.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("/secret/path", root.GetRawText());
    }

    [Fact]
    public async Task OutsideDevelopment_AnActionWithoutAFailureMessageGetsTheGenericText()
    {
        var context = CreateContext();
        SetFailingEndpoint(context);
        var handler = CreateHandler(context, Environments.Production);

        await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        var root = await ReadProblemAsync(context);
        var detail = root.GetProperty("detail").GetString();
        Assert.Contains("could not be completed", detail);
        Assert.Equal(detail, root.GetProperty("error").GetString());
        Assert.DoesNotContain("boom", root.GetRawText());
    }

    [Fact]
    public async Task InDevelopment_TheExceptionStillWinsOverAFailureMessage()
    {
        var context = CreateContext();
        SetFailingEndpoint(context, new FailureMessageAttribute("Error reading page"));
        var handler = CreateHandler(context, Environments.Development);

        await handler.TryHandleAsync(context, new InvalidOperationException("boom in dev"), CancellationToken.None);

        var root = await ReadProblemAsync(context);
        Assert.Contains("boom in dev", root.GetProperty("detail").GetString());
    }

    public static TheoryData<Exception> ClientAbortExceptions => new()
    {
        new OperationCanceledException(),
        new TaskCanceledException(),
    };

    [Theory]
    [MemberData(nameof(ClientAbortExceptions))]
    public async Task ClientAbort_IsNeitherAnErrorNorAProblemDocument(Exception exception)
    {
        var context = CreateContext();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;
        var handler = CreateHandler(context, Environments.Production);

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        _logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    public static TheoryData<Exception> AbortedRequestExceptions => new()
    {
        new OperationCanceledException(),
        new ConnectionResetException("reset by peer"),
        new BadHttpRequestException("Unexpected end of request content."),
        new IOException("stream closed"),
    };

    [Theory]
    [MemberData(nameof(AbortedRequestExceptions))]
    public async Task ThroughTheMiddleware_AnAbortedRequestIsNotReportedAsAnError(Exception exception)
    {
        // A dropped connection surfaces as a cancellation or as an IOException. The
        // framework's middleware answers both on an aborted request, before any handler.
        using var host = await StartPipelineAsync(context =>
        {
            context.RequestAborted = new CancellationToken(canceled: true);
            throw exception;
        });

        using var response = await host.GetTestClient().GetAsync("/api/files/process");

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, (int)response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        _logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task ThroughTheMiddleware_AnIOFailureOnALiveRequestIsReported()
    {
        var exception = new IOException("disk failure");
        using var host = await StartPipelineAsync(_ => throw exception);

        using var response = await host.GetTestClient().GetAsync("/api/files/process");

        Assert.Equal(StatusCodes.Status500InternalServerError, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("disk failure", await response.Content.ReadAsStringAsync());
        _logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), exception, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private Task<IHost> StartPipelineAsync(RequestDelegate endpoint) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddProblemDetails();
                    services.AddExceptionHandler<GlobalExceptionHandler>();
                    services.AddSingleton(_logger.Object);
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(endpoint);
                }))
            .StartAsync();

    [Fact]
    public async Task AFilePathInTheQuery_IsLoggedAsTheFileNameOnly()
    {
        var context = CreateContext();
        context.Request.QueryString = new QueryString("?filePath=" + Uri.EscapeDataString("/library/Saga/Saga 001.cbz"));
        var exception = new IOException("corrupt");
        var handler = CreateHandler(context, Environments.Production);

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString() == "Unhandled exception on (unrouted): POST /api/files/process on Saga 001.cbz (correlation id corr-123)"),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ARouteWithParameters_IsLoggedLiterallyInsteadOfBindingAsAProperty()
    {
        var context = CreateContext();
        SetFailingRoute(context, "api/files/{id}/tags");
        var exception = new IOException("disk failure");
        var handler = CreateHandler(context, Environments.Production);

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v!.ToString() == "Unhandled exception on api/files/{id}/tags: POST /api/files/process (correlation id corr-123)"),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task TheSameFrameworkFailureOnTwoRoutes_FingerprintsSeparately()
    {
        // With only framework frames on the stack, the template is all that tells endpoints apart.
        var tags = await TemplateLoggedForRouteAsync("api/files/{id}/tags");
        var series = await TemplateLoggedForRouteAsync("api/series");

        Assert.NotEqual(
            ErrorReportFingerprint.Compute("Microsoft.Data.Sqlite.SqliteException", null, tags, typeof(GlobalExceptionHandler).FullName),
            ErrorReportFingerprint.Compute("Microsoft.Data.Sqlite.SqliteException", null, series, typeof(GlobalExceptionHandler).FullName));
    }

    private async Task<string> TemplateLoggedForRouteAsync(string pattern)
    {
        var context = CreateContext();
        SetFailingRoute(context, pattern);
        _logger.Invocations.Clear();

        await CreateHandler(context, Environments.Production)
            .TryHandleAsync(context, new InvalidOperationException("locked"), CancellationToken.None);

        var state = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(
            Assert.Single(_logger.Invocations, i => i.Method.Name == nameof(ILogger.Log)).Arguments[2]);
        return Assert.IsType<string>(state.Single(pair => pair.Key == "{OriginalFormat}").Value);
    }

    private GlobalExceptionHandler CreateHandler(HttpContext context, string environmentName) =>
        new(
            context.RequestServices.GetRequiredService<IProblemDetailsService>(),
            Environment(environmentName),
            _logger.Object);

    /// <summary>
    /// Records the endpoint that failed the way the exception handling middleware
    /// does: on the feature, because it has already cleared the request's endpoint.
    /// </summary>
    private static void SetFailingEndpoint(HttpContext context, params object[] metadata)
    {
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = new InvalidOperationException("recorded failure"),
            Endpoint = new Endpoint(null, new EndpointMetadataCollection(metadata), "failing action"),
        });
    }

    private static void SetFailingRoute(HttpContext context, string pattern)
    {
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = new InvalidOperationException("recorded failure"),
            Endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), order: 0, EndpointMetadataCollection.Empty, pattern),
        });
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
