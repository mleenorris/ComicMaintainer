namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// JWT configuration settings
/// </summary>
public class JwtSettings
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "ComicMaintainer";
    public string Audience { get; set; } = "ComicMaintainerAPI";
    public int ExpirationMinutes { get; set; } = 1440; // 24 hours
}
