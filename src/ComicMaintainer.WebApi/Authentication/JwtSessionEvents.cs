using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ComicMaintainer.Core.Services.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace ComicMaintainer.WebApi.Authentication;

/// <summary>
/// The JwtBearer events of the two JWT schemes: session tokens in the <c>Authorization</c> header (the default
/// <see cref="JwtBearerDefaults.AuthenticationScheme"/>), and stream tokens in the query string of the event stream
/// (<see cref="StreamTokenAuthentication"/>). Both check, on every request, that the session the token belongs to has
/// not ended (<see cref="IAuthSessionValidator"/>).
/// </summary>
internal static class JwtSessionEvents
{
    /// <summary>The events of the session-token scheme: header only, never the query string.</summary>
    public static JwtBearerEvents SessionTokens() => new()
    {
        OnTokenValidated = async context =>
        {
            var principal = context.Principal!;
            if (principal.HasClaim(c => c.Type == AuthTokens.TokenUseClaim))
            {
                context.Fail("Not a session token");
                return;
            }

            if (!await IsSessionValidAsync(context.HttpContext, principal))
            {
                context.Fail("The session has ended");
            }
        },
        OnChallenge = WriteChallengeAsync,
        OnForbidden = WriteForbiddenAsync,
    };

    /// <summary>
    /// The events of the stream-token scheme: the token is read from the <c>access_token</c> query parameter of the
    /// event stream only, must be a stream token, and can be used once.
    /// </summary>
    public static JwtBearerEvents StreamTokens() => new()
    {
        OnMessageReceived = context =>
        {
            var query = context.Request.Query[StreamTokenAuthentication.QueryParameter];
            if (!StreamTokenAuthentication.IsStreamPath(context.Request.Path) || query.Count != 1 || string.IsNullOrEmpty(query[0]))
            {
                // Without a stream token in the URL this scheme has nothing to say; in particular it never falls back
                // to the Authorization header, which the session-token scheme handles.
                context.NoResult();
                return Task.CompletedTask;
            }

            context.Token = query[0];
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var principal = context.Principal!;
            if (principal.FindFirstValue(AuthTokens.TokenUseClaim) != AuthTokens.StreamTokenUse)
            {
                context.Fail("Not a stream token");
                return;
            }

            if (!await IsSessionValidAsync(context.HttpContext, principal))
            {
                context.Fail("The session has ended");
                return;
            }

            var validator = context.HttpContext.RequestServices.GetRequiredService<IAuthSessionValidator>();
            if (!validator.TryConsumeSingleUseToken(principal.FindFirstValue(JwtRegisteredClaimNames.Jti)!, context.SecurityToken.ValidTo))
            {
                context.Fail("The stream token was already used");
            }
        },
        OnChallenge = WriteChallengeAsync,
        OnForbidden = WriteForbiddenAsync,
    };

    private static Task<bool> IsSessionValidAsync(HttpContext context, ClaimsPrincipal principal) =>
        context.RequestServices.GetRequiredService<IAuthSessionValidator>().IsSessionValidAsync(
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            principal.FindFirstValue(AuthTokens.SecurityStampClaim),
            principal.FindFirstValue(JwtRegisteredClaimNames.Jti),
            context.RequestAborted);

    // An endpoint accepting several schemes challenges each of them; only the first may write the response.
    private static Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Unauthorized", message = "Authentication required" }));
    }

    private static Task WriteForbiddenAsync(ForbiddenContext context)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Forbidden", message = "Insufficient permissions" }));
    }
}

/// <summary>
/// The scheme that accepts stream tokens (see <see cref="AuthTokens"/>) in the query string of the event stream.
/// <c>EventSource</c> cannot send an <c>Authorization</c> header, so the browser fetches a stream token
/// (<c>POST /api/auth/stream-token</c>) and opens <c>/api/events/stream?access_token=…</c>. No other endpoint uses this
/// scheme, and no other token is accepted in a URL, so a token copied from a proxy's access log opens one event
/// stream at most, within a minute.
/// </summary>
public static class StreamTokenAuthentication
{
    /// <summary>The scheme name, for <c>[Authorize(AuthenticationSchemes = …)]</c> on the stream endpoint.</summary>
    public const string SchemeName = "StreamToken";

    /// <summary>The query parameter that carries the stream token.</summary>
    public const string QueryParameter = "access_token";

    /// <summary>The path of the event stream, the only one the stream token is read on.</summary>
    public static readonly PathString StreamPath = new("/api/events/stream");

    /// <summary>Whether <paramref name="path"/> is the event stream.</summary>
    public static bool IsStreamPath(PathString path) =>
        path.Equals(StreamPath, StringComparison.OrdinalIgnoreCase);
}
