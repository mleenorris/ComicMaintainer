using System.Text.Json;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
                It.Is<It.IsAnyType>((v, _) => v!.ToString() == "Unhandled exception for POST /api/files/process (correlation id corr-123)"),
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
