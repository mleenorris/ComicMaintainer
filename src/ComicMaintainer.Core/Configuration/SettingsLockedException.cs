namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// A save would change settings an environment variable pins, so the new value would never
/// take effect. Nothing was saved.
/// </summary>
public sealed class SettingsLockedException : Exception
{
    public SettingsLockedException(IReadOnlyList<EnvironmentOverride> overrides)
        : base(
            $"Set by the environment and cannot be changed here: {string.Join(", ", overrides.Select(o => o.Variable))}. " +
            "Remove the variable and restart to edit the setting in the app.")
    {
        Overrides = overrides;
    }

    /// <summary>The overrides the save collided with.</summary>
    public IReadOnlyList<EnvironmentOverride> Overrides { get; }
}
