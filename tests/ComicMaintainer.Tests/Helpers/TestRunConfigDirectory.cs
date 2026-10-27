using System.Runtime.CompilerServices;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// Gives each test run a config directory of its own. <c>Program</c> writes its Serilog logs and a
/// bootstrap user-settings.json to <c>CONFIG_DIR</c>, or to <c>/Config</c> when it is unset (to
/// <c>$TMPDIR/ComicMaintainer</c> if /Config cannot be created), so every host booted through
/// <c>WebApplicationFactory&lt;Program&gt;</c> used to leave its logs there (#876). Before any test
/// code runs, this points CONFIG_DIR at a directory unique to the run, replacing any value inherited
/// from the shell so a real config directory is never written to, and deletes that directory when
/// the test process exits.
/// </summary>
internal static class TestRunConfigDirectory
{
    public const string EnvironmentVariable = "CONFIG_DIR";

    /// <summary>The directory CONFIG_DIR points at for the whole run. Program creates it on first use.</summary>
    public static string Path { get; } = System.IO.Path.Join(
        System.IO.Path.GetTempPath(), "ComicMaintainer.Tests", "Config-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete();
    }

    private static void Delete()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; also covers a run that never started a host, so never created it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
