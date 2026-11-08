using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ComicMaintainer.Core.Services.Auth;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// The lifetime of a live SSE connection, which <see cref="EventsControllerTests"/>
/// never opens: a connected client gets heartbeats and broadcasts until it
/// disconnects, and is unregistered afterwards.
/// </summary>
public class EventsControllerCoverageTests
{
    private readonly Mock<ILogger<EventsController>> _logger = new();
    private readonly EventBroadcasterService _broadcaster = new(
        Mock.Of<ILogger<EventBroadcasterService>>());
    private readonly Mock<IAuthSessionValidator> _sessions = new();

    private static ClaimsPrincipal TokenUser(string userId = "user-1", string stamp = "stamp-1", string tokenId = "token-1") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(AuthTokens.SecurityStampClaim, stamp),
            new Claim(JwtRegisteredClaimNames.Jti, tokenId),
        ], "StreamToken"));

    private static ClaimsPrincipal ApiKeyUser(string userId = "user-1") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("auth_method", ApiKeys.AuthMethod),
        ], "ApiKey"));

    [Fact]
    public async Task Stream_OpenedWithAnApiKeyThatHasBeenRevoked_ClosesAtTheNextHeartbeat()
    {
        _sessions.Setup(s => s.IsApiKeyValidAsync("user-1", "cm_key", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { User = ApiKeyUser() };
        httpContext.Request.Headers[ApiKeys.HeaderName] = " cm_key ";
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        await controller.Stream().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(string.Empty, body.Text);
        _sessions.Verify(s => s.IsApiKeyValidAsync("user-1", "cm_key", It.IsAny<CancellationToken>()), Times.Once);
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Stream_OpenedWithAValidApiKey_StaysOpen()
    {
        _sessions.Setup(s => s.IsApiKeyValidAsync("user-1", "cm_key", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var disconnect = new CancellationTokenSource();
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { User = ApiKeyUser(), RequestAborted = disconnect.Token };
        httpContext.Request.Headers[ApiKeys.HeaderName] = "cm_key";
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EventsController.HeartbeatMessage, body.Text);
        Assert.False(streaming.IsCompleted);
        disconnect.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stream_OpenedWithATokenWhoseSessionHasEnded_ClosesAtTheNextHeartbeat()
    {
        _sessions.Setup(s => s.IsSessionValidAsync("user-1", "stamp-1", "token-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { User = TokenUser() };
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        await controller.Stream().WaitAsync(TimeSpan.FromSeconds(10));

        // Signed out (or the password, role or account changed): the stream ends instead of sending events on.
        Assert.Equal(string.Empty, body.Text);
        _sessions.Verify(s => s.IsSessionValidAsync("user-1", "stamp-1", "token-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stream_OpenedWithALiveToken_ChecksTheSessionBeforeEachHeartbeat()
    {
        _sessions.Setup(s => s.IsSessionValidAsync("user-1", "stamp-1", "token-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var disconnect = new CancellationTokenSource();
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { User = TokenUser(), RequestAborted = disconnect.Token };
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EventsController.HeartbeatMessage, body.Text);
        _sessions.Verify(s => s.IsSessionValidAsync("user-1", "stamp-1", "token-1", It.IsAny<CancellationToken>()), Times.Once);
        disconnect.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stream_OfAnAutheliaSession_IsNotCheckedAgainstTokenSessions()
    {
        using var disconnect = new CancellationTokenSource();
        var body = new FlushSignallingStream();
        var autheliaUser = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("auth_method", "authelia")], "Authelia"));
        var httpContext = new DefaultHttpContext { User = autheliaUser, RequestAborted = disconnect.Token };
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EventsController.HeartbeatMessage, body.Text);
        _sessions.VerifyNoOtherCalls();
        disconnect.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stream_SendsHeartbeatsAndBroadcastsUntilTheClientDisconnects()
    {
        using var disconnect = new CancellationTokenSource();
        var body = new FlushSignallingStream();
        var httpContext = new DefaultHttpContext { RequestAborted = disconnect.Token };
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, _broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();

        // The first heartbeat goes out immediately; the next one is 30s away. It is a named event, not a comment,
        // so the page can see that the stream is alive.
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("event: heartbeat\ndata: {}\n\n", body.Text);
        Assert.False(streaming.IsCompleted);

        // While connected, the client receives broadcasts.
        await _broadcaster.BroadcastFileListUpdateAsync();
        Assert.Equal("event: heartbeat\ndata: {}\n\ndata: {\"type\":\"file_list_updated\",\"data\":{}}\n\n", body.Text);

        // Disconnecting ends the stream normally rather than faulting it...
        disconnect.Cancel();
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(streaming.IsCompletedSuccessfully);
        _logger.Verify(l => l.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state!.ToString()!.StartsWith("SSE client disconnected")),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);

        // ...and the client no longer receives anything.
        var textAfterDisconnect = body.Text;
        await _broadcaster.BroadcastFileListUpdateAsync();
        Assert.Equal(textAfterDisconnect, body.Text);
    }

    [Fact]
    public async Task Stream_WhenTheBroadcasterDropsTheClient_EndsTheResponse()
    {
        var broadcaster = new EventBroadcasterService(
            Mock.Of<ILogger<EventBroadcasterService>>(),
            TimeSpan.FromMilliseconds(100));
        var body = new FlushSignallingStream();
        using var lifetime = new RecordingLifetimeFeature();
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        httpContext.Response.Body = body;
        var controller = new EventsController(_logger.Object, broadcaster, _sessions.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var streaming = controller.Stream();
        await body.FirstFlush.WaitAsync(TimeSpan.FromSeconds(10));

        // The client stops accepting data, so the broadcast times out and drops it...
        body.Stall = true;
        await broadcaster.BroadcastFileListUpdateAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // ...which aborts the connection, so the stream ends instead of carrying on with heartbeats only.
        await streaming.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(streaming.IsCompletedSuccessfully);
        Assert.True(lifetime.Aborted);
    }

    /// <summary>Lifetime feature that, like the server, cancels <see cref="RequestAborted"/> on abort.</summary>
    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _aborted = new();

        public CancellationToken RequestAborted
        {
            get => _aborted.Token;
            set { }
        }

        public bool Aborted { get; private set; }

        public void Abort()
        {
            Aborted = true;
            _aborted.Cancel();
        }

        public void Dispose() => _aborted.Dispose();
    }

    /// <summary>Response body that signals when something was first flushed to it.</summary>
    private sealed class FlushSignallingStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstFlush => _firstFlush.Task;

        public string Text => Encoding.UTF8.GetString(ToArray());

        /// <summary>When set, writes never complete until they are cancelled.</summary>
        public bool Stall { get; set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            _firstFlush.TrySetResult();
        }
    }
}
