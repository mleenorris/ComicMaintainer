using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// The claims and audiences of the JWTs the app issues. There are two kinds:
/// <list type="bullet">
/// <item><description>session tokens, issued at sign-in and sent in the <c>Authorization</c> header, valid for
/// <c>JwtSettings.ExpirationMinutes</c>;</description></item>
/// <item><description>stream tokens, valid for <see cref="StreamTokenLifetime"/> and only for opening the event stream,
/// the one endpoint a browser cannot send headers to (<c>EventSource</c>), so they travel in its URL. They have
/// their own audience (<see cref="StreamAudience"/>), so no other endpoint accepts them.</description></item>
/// </list>
/// Both carry a fingerprint of the account's security stamp (<see cref="SecurityStampClaim"/>), checked on every
/// request, so that changing the password or the roles, disabling or deleting the account, or signing out of every
/// session ends them.
/// </summary>
public static class AuthTokens
{
    /// <summary>The claim holding <see cref="Fingerprint"/> of the account's security stamp when the token was issued.</summary>
    public const string SecurityStampClaim = "cm_ss";

    /// <summary>The claim naming the kind of token: <see cref="StreamTokenUse"/> for stream tokens.</summary>
    public const string TokenUseClaim = "token_use";

    /// <summary>The <see cref="TokenUseClaim"/> of a stream token.</summary>
    public const string StreamTokenUse = "stream";

    /// <summary>How long a stream token can be used to open the event stream.</summary>
    public static readonly TimeSpan StreamTokenLifetime = TimeSpan.FromSeconds(60);

    /// <summary>The audience of stream tokens, derived from the audience of session tokens.</summary>
    public static string StreamAudience(string sessionAudience) => sessionAudience + "/stream";

    /// <summary>
    /// A fingerprint of a security stamp: the first 128 bits of its SHA-256, base64url-encoded. The stamp itself
    /// is not put in tokens, which anyone holding one can read, because Identity also derives other tokens from it.
    /// </summary>
    public static string Fingerprint(string? securityStamp) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp ?? string.Empty)).AsSpan(0, 16).ToArray());
}
