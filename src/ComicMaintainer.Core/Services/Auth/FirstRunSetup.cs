using System.Security.Cryptography;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services.Auth;

/// <summary>
/// When the anonymous first-run setup (<c>POST /api/auth/setup</c>) may create the first administrator: only on a
/// genuinely fresh instance, with no account at all, and never when Authelia is the identity provider (its users
/// and admin groups decide who administers the instance). Whether some account still holds the Admin role does not
/// matter, so demoting every administrator (for example through Authelia's role sync) cannot reopen it.
/// </summary>
public static class FirstRunSetup
{
    /// <summary>Whether the first-run setup is available.</summary>
    public static async Task<bool> IsAvailableAsync(UserManager<ApplicationUser> userManager, AutheliaSettings autheliaSettings) =>
        !autheliaSettings.Enabled && !await userManager.Users.AnyAsync();
}

/// <summary>
/// The one-time code that first-run setup requires, so that whoever reaches a fresh, exposed instance first cannot
/// claim it: only someone who can read the server's log (or set its environment) knows it.
/// </summary>
public interface ISetupTokenProvider
{
    /// <summary>
    /// Whether the code was set with <c>SETUP_TOKEN</c> (or <c>SETUP_TOKEN_FILE</c>); otherwise it was generated
    /// at startup and is logged while setup is available.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>The code. Only log it while setup is available, and only when it was generated.</summary>
    string Token { get; }

    /// <summary>Whether <paramref name="candidate"/> is the code, compared in constant time.</summary>
    bool Matches(string? candidate);
}

/// <summary>
/// The setup code from <c>SETUP_TOKEN</c>, or a random one generated for this run of the app: 20 characters of
/// Crockford's base32 (100 bits), shown in groups of five. A generated code is matched case-insensitively and
/// without its dashes and spaces, so it can be retyped from the log; a configured one must match exactly.
/// </summary>
public sealed class SetupTokenProvider : ISetupTokenProvider
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 20;
    private const int GroupLength = 5;

    private readonly byte[] _expected;

    public SetupTokenProvider(string? configuredToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            IsConfigured = true;
            Token = configuredToken.Trim();
            _expected = Encoding.UTF8.GetBytes(Token);
            return;
        }

        var characters = RandomNumberGenerator.GetString(Alphabet, Length);
        Token = string.Join('-', Enumerable.Range(0, Length / GroupLength).Select(group => characters.Substring(group * GroupLength, GroupLength)));
        _expected = Encoding.UTF8.GetBytes(characters);
    }

    /// <inheritdoc />
    public bool IsConfigured { get; }

    /// <inheritdoc />
    public string Token { get; }

    /// <inheritdoc />
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(IsConfigured ? candidate.Trim() : NormalizeGenerated(candidate));
        return CryptographicOperations.FixedTimeEquals(actual, _expected);
    }

    // Crockford's base32 reads O as 0 and I and L as 1, and ignores case and separators.
    private static string NormalizeGenerated(string candidate)
    {
        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate.ToUpperInvariant())
        {
            switch (character)
            {
                case '-' or ' ' or '\t':
                    continue;
                case 'O':
                    builder.Append('0');
                    break;
                case 'I' or 'L':
                    builder.Append('1');
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>The outcome of a first-run setup attempt (<c>IAuthService.SetupAdminAsync</c>).</summary>
public enum SetupOutcome
{
    /// <summary>The administrator was created.</summary>
    Created,

    /// <summary>Setup is not available: an account exists, or Authelia is enabled.</summary>
    NotAvailable,

    /// <summary>The setup code was missing or wrong.</summary>
    InvalidSetupToken,

    /// <summary>Identity rejected the username, email or password.</summary>
    Rejected,
}
