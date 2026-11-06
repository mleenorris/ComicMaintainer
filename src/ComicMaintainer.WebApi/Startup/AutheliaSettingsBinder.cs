using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Authentication;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// The only reader of <see cref="AutheliaSettings"/>: the "AutheliaSettings" configuration section, overridden by
/// the <c>AUTHELIA_*</c> environment variables documented in docs/AUTHELIA.md. The options registration (used by the
/// Authelia handler) and the startup registrations (trusted-proxy validation, scheme selection and the
/// <c>CanAdminister</c> fallback) both go through it, so they always see the same effective settings.
/// </summary>
internal static class AutheliaSettingsBinder
{
    private static readonly (string Variable, Action<AutheliaSettings, string> Apply)[] EnvironmentOverrides =
    [
        ("AUTHELIA_ENABLED", (settings, value) => settings.Enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase)),
        (TrustedProxyAllowlist.EnvironmentVariable, (settings, value) => settings.TrustedProxies = value),
        ("AUTHELIA_URL", (settings, value) => settings.Url = value),
        ("AUTHELIA_USER_HEADER", (settings, value) => settings.UserHeader = value),
        ("AUTHELIA_EMAIL_HEADER", (settings, value) => settings.EmailHeader = value),
        ("AUTHELIA_NAME_HEADER", (settings, value) => settings.NameHeader = value),
        ("AUTHELIA_GROUPS_HEADER", (settings, value) => settings.GroupsHeader = value),
        ("AUTHELIA_DEFAULT_ROLE", (settings, value) => settings.DefaultRole = value),
        ("AUTHELIA_ADMIN_GROUPS", (settings, value) => settings.AdminGroups = value),
    ];

    public static AutheliaSettings Load(IConfiguration configuration)
    {
        var settings = new AutheliaSettings();
        Bind(configuration, settings);
        return settings;
    }

    /// <summary>
    /// Binds the configuration section onto <paramref name="settings"/>, then applies the environment variables;
    /// an unset or empty variable keeps the configured value.
    /// </summary>
    public static void Bind(IConfiguration configuration, AutheliaSettings settings)
    {
        configuration.GetSection("AutheliaSettings").Bind(settings);

        foreach (var (variable, apply) in EnvironmentOverrides)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                apply(settings, value);
            }
        }
    }
}
