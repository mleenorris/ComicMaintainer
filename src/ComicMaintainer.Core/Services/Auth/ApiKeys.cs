using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// API keys: long-lived credentials for scripts and integrations. A key is sent in the <see cref="HeaderName"/>
/// header, never in a URL, and acts as the account that created it, with that account's current roles, until it
/// is revoked or replaced, the account is disabled, its password is changed or it signs out of all sessions. Each
/// account has at most one key. Only its SHA-256 hash is stored (<see cref="Hash"/>): the key itself is shown once,
/// when it is created.
/// </summary>
public static class ApiKeys
{
    private static readonly SearchValues<char> Base64UrlCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>The request header that carries an API key.</summary>
    public const string HeaderName = "X-API-Key";

    /// <summary>The prefix of every key, so that a leaked key is recognisable (for example by secret scanners).</summary>
    public const string Prefix = "cm_";

    /// <summary>The longest value <see cref="HeaderName"/> may carry; a generated key is 46 characters.</summary>
    public const int MaxLength = 128;

    /// <summary>The length of every key <see cref="Generate"/> makes: the prefix and 43 base64url characters.</summary>
    public const int GeneratedLength = 46;

    /// <summary>The <c>auth_method</c> claim of a request authenticated with an API key.</summary>
    public const string AuthMethod = "api_key";

    /// <summary>A new key: <see cref="Prefix"/> and 256 random bits, base64url-encoded.</summary>
    public static string Generate() => Prefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Whether <paramref name="value"/> has the exact form of a key <see cref="Generate"/> made. Where one field can
    /// hold either a password or a key, a value of this form is treated as a key: it is 256 random bits' worth of
    /// characters behind a fixed prefix, so nobody picks it as a password by chance.
    /// </summary>
    public static bool HasGeneratedForm(string value) =>
        value.Length == GeneratedLength
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlCharacters) < 0;

    /// <summary>
    /// The stored form of <paramref name="apiKey"/>: its SHA-256, as 64 lowercase hex digits. Keys are 256 random
    /// bits, so a fast hash is enough to keep a copy of the database from being usable as keys.
    /// </summary>
    public static string Hash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
}

/// <summary>Whether an account has an API key, and when it was created and last used (to the minute).</summary>
public sealed record ApiKeyStatus(bool HasApiKey, DateTime? CreatedAt, DateTime? LastUsedAt)
{
    /// <summary>The status of an account without a key.</summary>
    public static ApiKeyStatus None { get; } = new(false, null, null);
}
