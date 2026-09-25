using System.Net.Mail;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Minimal email address validation shared by the settings, device and
/// delivery layers so a malformed address is rejected before anything is
/// queued or handed to the SMTP server.
/// </summary>
public static class EmailAddressUtils
{
    /// <summary>
    /// Returns true when <paramref name="value"/> parses as a single
    /// <c>local@domain</c> mailbox with a dotted domain.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // MailAddress accepts display-name forms ("Name <a@b>"); we only want the bare address.
        if (trimmed.Contains('<') || trimmed.Contains('>') || trimmed.Contains(',') || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!MailAddress.TryCreate(trimmed, out var parsed) || parsed is null)
        {
            return false;
        }

        return string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.');
    }

    /// <summary>
    /// Trims and validates an address, throwing <see cref="ArgumentException"/> when invalid.
    /// </summary>
    public static string ValidateOrThrow(string? value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not a valid email address", paramName);
        }

        return value!.Trim();
    }
}
