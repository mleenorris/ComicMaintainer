namespace ComicMaintainer.MauiApp.Services;

/// <summary>Checks the server address the user entered and puts it in the one form the app works with.</summary>
public static class ServerAddress
{
    /// <summary>Shown wherever the user enters an address that would send their password unencrypted.</summary>
    public const string CleartextWarning =
        "This address uses unencrypted HTTP: anyone on the network between this device and the server can read your " +
        "password and session. Use it only on a network you trust, and HTTPS everywhere else.";

    /// <summary>
    /// Parses <paramref name="input"/>. The result ends in a slash, so that requests stay under a server hosted at a
    /// sub-path such as <c>https://example.com/comics/</c>. The scheme is required rather than assumed, because
    /// assuming <c>http</c> could send a password unencrypted to a server the user believes is secure.
    /// </summary>
    public static bool TryParse(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "Enter the address of your ComicMaintainer server.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter the full address, starting with http:// or https://.";
            return false;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Enter only the server's address, without a user name, query or fragment.";
            return false;
        }

        normalized = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return true;
    }

    /// <summary>The warning to show for <paramref name="input"/>, or an empty string when none is needed.</summary>
    public static string DescribeRisk(string? input) =>
        TryParse(input, out var normalized, out _) && normalized.StartsWith("http://", StringComparison.Ordinal)
            ? CleartextWarning
            : string.Empty;
}
