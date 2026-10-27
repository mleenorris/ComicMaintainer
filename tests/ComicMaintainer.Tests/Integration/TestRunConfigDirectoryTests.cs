using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Regression test for #876: hosts booted through <c>WebApplicationFactory&lt;Program&gt;</c> used to write
/// their logs and a bootstrap user-settings.json to /Config (or $TMPDIR/ComicMaintainer), where they
/// accumulated across runs. They now write to the per-run <see cref="TestRunConfigDirectory"/>.
/// </summary>
[Trait("Category", "Integration")]
public class TestRunConfigDirectoryTests
{
    [Fact]
    public async Task Program_WritesItsLogsAndUserSettingsToThePerRunConfigDirectory()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/version");

        response.EnsureSuccessStatusCode();
        Assert.Equal(TestRunConfigDirectory.Path, Environment.GetEnvironmentVariable("CONFIG_DIR"));
        Assert.StartsWith(Path.GetTempPath(), TestRunConfigDirectory.Path);
        Assert.True(File.Exists(Path.Join(TestRunConfigDirectory.Path, "user-settings.json")));
        Assert.NotEmpty(Directory.GetFiles(TestRunConfigDirectory.Path, "debug*.log"));
    }
}
