using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Applies environment-variable overrides to <see cref="AppSettings"/> so they always win
/// over values from appsettings.json and user-settings.json. Implemented as
/// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> so it runs
/// every time the options instance is (re)built, including after a user-settings.json reload.
/// The variables are listed in <see cref="AppSettingsEnvironmentOverrides"/>.
/// </summary>
internal sealed class AppSettingsEnvironmentPostConfigure : Microsoft.Extensions.Options.IPostConfigureOptions<AppSettings>
{
    public void PostConfigure(string? name, AppSettings options)
    {
        foreach (var environmentOverride in AppSettingsEnvironmentOverrides.All)
        {
            environmentOverride.ApplyTo(options);
        }
    }
}
