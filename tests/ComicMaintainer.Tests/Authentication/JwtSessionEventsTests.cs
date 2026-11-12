using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// The JwtBearer events of the session-token and stream-token schemes: the JSON answers to a challenge and a refused
/// request, that only the first scheme may write them, and that a valid token still has to belong to a live session.
/// </summary>
public sealed class JwtSessionEventsTests
{
    // JwtSessionEvents is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Type Events = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Authentication.JwtSessionEvents", throwOnError: true)!;

    private readonly Mock<IAuthSessionValidator> _validator = new();

    [Theory]
    [InlineData("SessionTokens")]
    [InlineData("StreamTokens")]
    public async Task Challenge_AnswersUnauthorizedAsJson(string scheme)
    {
        var (http, body) = CreateContext();
        var context = new JwtBearerChallengeContext(http, Scheme(), new JwtBearerOptions(), new AuthenticationProperties());

        await Create(scheme).OnChallenge(context);

        Assert.True(context.Handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", http.Response.ContentType);
        var json = ReadJson(body);
        Assert.Equal("Unauthorized", json.GetProperty("error").GetString());
        Assert.Equal("Authentication required", json.GetProperty("message").GetString());
    }

    // An endpoint accepting several schemes challenges each of them: only the first may write the response.
    [Theory]
    [InlineData("SessionTokens")]
    [InlineData("StreamTokens")]
    public async Task Challenge_OnAStartedResponse_LeavesItAlone(string scheme)
    {
        var (http, body) = CreateContext(started: true);
        var context = new JwtBearerChallengeContext(http, Scheme(), new JwtBearerOptions(), new AuthenticationProperties());

        await Create(scheme).OnChallenge(context);

        Assert.True(context.Handled);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData("SessionTokens")]
    [InlineData("StreamTokens")]
    public async Task Forbidden_AnswersForbiddenAsJson(string scheme)
    {
        var (http, body) = CreateContext();

        await Create(scheme).OnForbidden(new ForbiddenContext(http, Scheme(), new JwtBearerOptions()));

        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", http.Response.ContentType);
        var json = ReadJson(body);
        Assert.Equal("Forbidden", json.GetProperty("error").GetString());
        Assert.Equal("Insufficient permissions", json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("SessionTokens")]
    [InlineData("StreamTokens")]
    public async Task Forbidden_OnAStartedResponse_LeavesItAlone(string scheme)
    {
        var (http, body) = CreateContext(started: true);

        await Create(scheme).OnForbidden(new ForbiddenContext(http, Scheme(), new JwtBearerOptions()));

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task SessionToken_AValidSession_IsAccepted()
    {
        _validator.Setup(v => v.IsSessionValidAsync("user-1", "stamp", "jti-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var context = Validated(SessionPrincipal());

        await Create("SessionTokens").OnTokenValidated(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task SessionToken_AnEndedSession_FailsWithTheReason()
    {
        _validator.Setup(v => v.IsSessionValidAsync("user-1", "stamp", "jti-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var context = Validated(SessionPrincipal());

        await Create("SessionTokens").OnTokenValidated(context);

        Assert.Equal("The session has ended", context.Result!.Failure!.Message);
    }

    [Fact]
    public async Task SessionToken_APrincipalMadeForAnotherUse_FailsBeforeTheSessionIsChecked()
    {
        var context = Validated(SessionPrincipal(new Claim(AuthTokens.TokenUseClaim, AuthTokens.StreamTokenUse)));

        await Create("SessionTokens").OnTokenValidated(context);

        Assert.Equal("Not a session token", context.Result!.Failure!.Message);
        _validator.Verify(
            v => v.IsSessionValidAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StreamToken_ASessionThatEnded_FailsWithoutConsumingTheToken()
    {
        _validator.Setup(v => v.IsSessionValidAsync("user-1", "stamp", "jti-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var context = Validated(SessionPrincipal(new Claim(AuthTokens.TokenUseClaim, AuthTokens.StreamTokenUse)));

        await Create("StreamTokens").OnTokenValidated(context);

        Assert.Equal("The session has ended", context.Result!.Failure!.Message);
        _validator.Verify(v => v.TryConsumeSingleUseToken(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task StreamToken_MustBeAStreamToken()
    {
        var context = Validated(SessionPrincipal());

        await Create("StreamTokens").OnTokenValidated(context);

        Assert.Equal("Not a stream token", context.Result!.Failure!.Message);
    }

    [Fact]
    public async Task StreamToken_CanBeUsedOnlyOnce()
    {
        _validator.Setup(v => v.IsSessionValidAsync("user-1", "stamp", "jti-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _validator.Setup(v => v.TryConsumeSingleUseToken("jti-1", It.IsAny<DateTime>())).Returns(false);
        var context = Validated(SessionPrincipal(new Claim(AuthTokens.TokenUseClaim, AuthTokens.StreamTokenUse)));

        await Create("StreamTokens").OnTokenValidated(context);

        Assert.Equal("The stream token was already used", context.Result!.Failure!.Message);
    }

    private TokenValidatedContext Validated(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_validator.Object);
        var (http, _) = CreateContext();
        http.RequestServices = services.BuildServiceProvider();
        return new TokenValidatedContext(http, Scheme(), new JwtBearerOptions())
        {
            Principal = principal,
            SecurityToken = new JwtSecurityToken(),
        };
    }

    private static ClaimsPrincipal SessionPrincipal(params Claim[] extra) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(AuthTokens.SecurityStampClaim, "stamp"),
                new Claim(JwtRegisteredClaimNames.Jti, "jti-1"),
            }.Concat(extra),
            "Test"));

    private static AuthenticationScheme Scheme() => new("Bearer", displayName: null, typeof(JwtBearerHandler));

    private static JwtBearerEvents Create(string method) =>
        (JwtBearerEvents)Events.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null)!;

    private static (DefaultHttpContext Context, MemoryStream Body) CreateContext(bool started = false)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        if (started)
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature { StatusCode = StatusCodes.Status200OK });
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        }

        return (context, body);
    }

    private static JsonElement ReadJson(MemoryStream body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
