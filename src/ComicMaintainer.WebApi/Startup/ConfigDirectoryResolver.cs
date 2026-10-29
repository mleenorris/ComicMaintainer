namespace ComicMaintainer.WebApi.Startup;

/// <summary>
/// Picks the directory that holds all persistent state: the SQLite database, the DataProtection
/// keys, the logs, user-settings.json, backups and caches. The <c>CONFIG_DIR</c> environment
/// variable wins over <c>AppSettings:ConfigDirectory</c> (appsettings.json,
/// <c>AppSettings__ConfigDirectory</c>, ...), which wins over <c>/Config</c>; blank values are
/// ignored. <c>Program</c> resolves it once and publishes the directory it ends up using as
/// <c>AppSettings:ConfigDirectory</c>, so every consumer agrees on it.
/// </summary>
internal static class ConfigDirectoryResolver
{
    public const string EnvironmentVariable = "CONFIG_DIR";

    public const string ConfigurationKey = "AppSettings:ConfigDirectory";

    public const string ResolvedDirectoryConfigurationKey = "ComicMaintainer:ResolvedConfigDirectory";

    public const string DefaultDirectory = "/Config";

    public static string Resolve(IConfiguration configuration)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        var fromConfiguration = configuration[ConfigurationKey];
        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(fromConfiguration) ? DefaultDirectory : fromConfiguration);
    }
}
