using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for <c>ConfigDirectoryResolver</c>, which picks the directory Program keeps the database,
/// DataProtection keys, logs and settings in. It reads CONFIG_DIR, so the tests run in the
/// non-parallel environment collection.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class ConfigDirectoryResolverTests : IDisposable
{
    // ConfigDirectoryResolver is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IConfiguration, string> Resolve = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.ConfigDirectoryResolver", throwOnError: true)!
        .GetMethod("Resolve", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IConfiguration, string>>();

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment = new("CONFIG_DIR");

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void ConfigDir_WinsOverTheConfiguredDirectory()
    {
        _environment.Set("CONFIG_DIR", "/env/config");

        Assert.Equal(Path.GetFullPath("/env/config"), Resolve(Configuration("/settings/config")));
    }

    [Fact]
    public void ConfigDirConfigurationValueOverridesTheProcessEnvironment()
    {
        _environment.Set("CONFIG_DIR", "/env/config");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONFIG_DIR"] = "/test/config",
                ["AppSettings:ConfigDirectory"] = "/settings/config",
            })
            .Build();

        Assert.Equal(Path.GetFullPath("/test/config"), Resolve(configuration));
    }

    [Fact]
    public void WithoutConfigDir_UsesTheConfiguredDirectory()
    {
        Assert.Equal(Path.GetFullPath("/settings/config"), Resolve(Configuration("/settings/config")));
    }

    [Fact]
    public void WithNeither_DefaultsToSlashConfig()
    {
        Assert.Equal(Path.GetFullPath("/Config"), Resolve(Configuration(null)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RelativePaths_AreResolvedToAbsolutePaths(bool fromEnvironment)
    {
        var relative = Path.Join("relative", "config");
        _environment.Set("CONFIG_DIR", fromEnvironment ? relative : "");

        Assert.Equal(
            Path.GetFullPath(relative),
            Resolve(Configuration(fromEnvironment ? null : relative)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankValues_AreIgnored(string blank)
    {
        _environment.Set("CONFIG_DIR", blank);

        Assert.Equal(Path.GetFullPath("/settings/config"), Resolve(Configuration("/settings/config")));
        Assert.Equal(Path.GetFullPath("/Config"), Resolve(Configuration(blank)));
    }

    private static IConfiguration Configuration(string? configDirectory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:ConfigDirectory"] = configDirectory })
            .Build();
}
