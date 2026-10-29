using System.Text.RegularExpressions;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Validates GitHub owner/repository names before they are used to build an
/// API URL.
/// </summary>
/// <remarks>
/// These values are operator-supplied and are interpolated into the request
/// path. Restricting them to GitHub's own character set means a value like
/// <c>../../user/repos</c> is rejected at the point it is saved rather than
/// relying on URL escaping downstream.
/// </remarks>
public static class GitHubNameValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// GitHub logins: alphanumerics and single hyphens, max 39 characters.
    /// </summary>
    private static readonly Regex OwnerPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Repository names: alphanumerics, hyphen, underscore and dot, max 100
    /// characters. <see cref="IsValidRepository"/> also rejects names made only
    /// of dots.
    /// </summary>
    private static readonly Regex RepositoryPattern = new(
        @"^[A-Za-z0-9._-]{1,100}$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public static bool IsValidOwner(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return OwnerPattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool IsValidRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Never just dots: "." and ".." would be read as path segments.
        if (value.All(c => c == '.'))
        {
            return false;
        }

        try
        {
            return RepositoryPattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
