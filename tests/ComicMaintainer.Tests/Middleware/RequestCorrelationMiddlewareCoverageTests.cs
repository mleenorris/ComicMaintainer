using ComicMaintainer.WebApi.Middleware;
using Microsoft.AspNetCore.Http;

namespace ComicMaintainer.Tests.Middleware;

public class RequestCorrelationMiddlewareCoverageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Sanitize_BlankInput_ReturnsEmpty(string? value)
    {
        Assert.Equal(string.Empty, RequestCorrelationMiddleware.Sanitize(value));
    }

    [Fact]
    public void Sanitize_KeepsOnlyAsciiLettersDigitsAndSeparators()
    {
        // Each rejected character sits immediately outside one of the allowed ranges.
        const string input = "a`z{A@Z[0/9;-_:~ é\u00FF\u0000";

        Assert.Equal("azAZ09-_:", RequestCorrelationMiddleware.Sanitize(input));
    }

    [Fact]
    public void Sanitize_StopsAtMaxLengthEvenWhenLaterCharactersAreRejected()
    {
        var input = new string('x', RequestCorrelationMiddleware.MaxLength) + "!!!yyy";

        Assert.Equal(new string('x', RequestCorrelationMiddleware.MaxLength), RequestCorrelationMiddleware.Sanitize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public async Task UnusableHeaderAndTraceIdentifier_FallBackToAGeneratedId(string traceIdentifier)
    {
        var context = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
        context.Request.Headers[RequestCorrelationMiddleware.HeaderName] = "   ";
        string? seenByNext = null;
        var middleware = new RequestCorrelationMiddleware(ctx =>
        {
            seenByNext = ctx.GetCorrelationId();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        var id = Assert.IsType<string>(context.Items[RequestCorrelationMiddleware.ItemsKey]);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, seenByNext);
    }

    [Fact]
    public async Task WhitespaceHeader_FallsBackToTheTraceIdentifier()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "0HN8:00000002" };
        context.Request.Headers[RequestCorrelationMiddleware.HeaderName] = " \t ";

        await new RequestCorrelationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal("0HN8:00000002", context.GetCorrelationId());
    }

    [Fact]
    public async Task TraceIdentifierFallback_IsSanitized()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace id\r\n42" };

        await new RequestCorrelationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal("traceid42", context.GetCorrelationId());
    }

    [Fact]
    public void GetCorrelationId_WithoutMiddleware_ReturnsTraceIdentifier()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "raw-trace" };

        Assert.Equal("raw-trace", context.GetCorrelationId());
    }

    [Theory]
    [InlineData(42)]
    [InlineData("")]
    [InlineData(null)]
    public void GetCorrelationId_WithUnusableItemsValue_ReturnsTraceIdentifier(object? stored)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "raw-trace" };
        context.Items[RequestCorrelationMiddleware.ItemsKey] = stored;

        Assert.Equal("raw-trace", context.GetCorrelationId());
    }

    [Fact]
    public void GetCorrelationId_ReturnsStoredId()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "raw-trace" };
        context.Items[RequestCorrelationMiddleware.ItemsKey] = "stored-id";

        Assert.Equal("stored-id", context.GetCorrelationId());
    }
}
