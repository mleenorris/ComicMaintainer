using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Startup;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Regression test for #876: hosts booted through <c>WebApplicationFactory&lt;Program&gt;</c> used to write
/// their logs and a bootstrap user-settings.json to /Config (or $TMPDIR/ComicMaintainer), where they
/// accumulated across runs. They now write to the per-run <see cref="TestRunConfigDirectory"/>. The app's
/// Serilog factory writes through the process-wide <c>Log.Logger</c>, which each host replaces when it starts,
/// and hosts given a config directory of their own log there, so the test runs in the non-parallel
/// environment collection.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public class TestRunConfigDirectoryTests
{
    [Fact]
    public async Task Program_WritesItsLogsAndUserSettingsToThePerRunConfigDirectory()
    {
        var factory = new WebApplicationFactory<Program>();
        try
        {
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/version");
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            // Stopping the host flushes and closes the Serilog file sinks before the files are inspected.
            await factory.DisposeAsync();
        }

        var configDirectory = TestRunConfigDirectory.DirectoryPath;
        Assert.Equal(configDirectory, Environment.GetEnvironmentVariable("CONFIG_DIR"));
        Assert.StartsWith(Path.GetTempPath(), configDirectory);
        Assert.True(Directory.Exists(configDirectory), $"Expected config directory {configDirectory} to exist.");
        Assert.True(File.Exists(Path.Join(configDirectory, "user-settings.json")));
        Assert.True(
            await WaitForAsync(() => Directory.GetFiles(configDirectory, "debug*.log").Length > 0, TimeSpan.FromSeconds(5)),
            "Expected a debug*.log file in the per-run config directory.");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }
}
