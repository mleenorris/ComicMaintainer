using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// The only reader of <see cref="JwtSettings"/>: the "JwtSettings" configuration section, with the signing secret
/// overridden by the <c>JWT_SECRET</c> environment variable. The options registration (which <c>AuthService</c> signs
/// tokens with) and the JWT bearer registration (which validates them) both go through it, so they always use the
/// same secret.
/// </summary>
internal static class JwtSettingsBinder
{
    private const string SecretEnvironmentVariable = "JWT_SECRET";

    public static JwtSettings Load(IConfiguration configuration)
    {
        var settings = new JwtSettings();
        Bind(configuration, settings);
        return settings;
    }

    /// <summary>
    /// Binds the configuration section onto <paramref name="settings"/>, then applies <c>JWT_SECRET</c>; an unset or
    /// blank (empty or whitespace-only) variable keeps the configured secret.
    /// </summary>
    public static void Bind(IConfiguration configuration, JwtSettings settings)
    {
        configuration.GetSection("JwtSettings").Bind(settings);

        var secret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            settings.Secret = secret;
        }
    }

    /// <summary>
    /// Whether <c>JWT_SECRET</c> is set but blank (such as <c>docker run -e JWT_SECRET=</c>), which <see cref="Bind"/>
    /// ignores.
    /// </summary>
    public static bool IsSecretVariableBlank() =>
        Environment.GetEnvironmentVariable(SecretEnvironmentVariable) is { } secret && string.IsNullOrWhiteSpace(secret);
}
