using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Tests.Startup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Boots the real <c>Program</c> and checks that all persistent state (the SQLite database, the
/// DataProtection keys, the logs, user-settings.json and the backups) lives in one config directory:
/// CONFIG_DIR, then AppSettings:ConfigDirectory, then /Config. CONFIG_DIR used to move only the logs,
/// settings and backups, leaving the database and keys in /Config (#844). Unlike the other integration
/// tests, nothing overrides AppSettings:ConfigDirectory once Program has registered its sources, so
/// Program alone picks the directory. CONFIG_DIR is process-wide, so the tests run in the non-parallel
/// environment collection.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class ConfigDirectoryStartupTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "ConfigDir-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment = new("CONFIG_DIR");

    public ConfigDirectoryStartupTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "watched"));
        Directory.CreateDirectory(Path.Join(_root, "duplicates"));
    }

    /// <summary>The AppSettings:ConfigDirectory value the host is configured with (as appsettings.json would).</summary>
    private string ConfiguredDirectory => Path.Join(_root, "configured");

    public void Dispose()
    {
        _environment.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task ConfigDir_HoldsTheDatabaseKeysLogsSettingsAndBackups()
    {
        var configDir = Path.Join(_root, "config-dir");
        _environment.Set("CONFIG_DIR", configDir);

        await using var factory = CreateFactory();

        AssertAllStateIsIn(configDir, factory.Services);
        Assert.NotEmpty(Directory.GetFiles(configDir, "app*.log"));
        // CONFIG_DIR wins over AppSettings:ConfigDirectory, so nothing is written there.
        Assert.False(Directory.Exists(ConfiguredDirectory));
    }

    [Fact]
    public async Task WithoutConfigDir_TheConfiguredDirectoryHoldsEverything()
    {
        await using var factory = CreateFactory();

        AssertAllStateIsIn(ConfiguredDirectory, factory.Services);
        Assert.NotEmpty(Directory.GetFiles(ConfiguredDirectory, "app*.log"));
    }

    [Fact]
    public async Task UnwritableConfigDir_KeepsEverythingInOneTemporaryDirectoryAndWarns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var readOnly = Path.Join(_root, "read-only");
        Directory.CreateDirectory(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = Path.Join(readOnly, "probe");
        try
        {
            Directory.CreateDirectory(probe);
            // Root ignores directory permissions, so the fallback can only be exercised as another user.
            Directory.Delete(probe);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }

        var requested = Path.Join(readOnly, "config");
        var temp = Path.Join(_root, "temp");
        Directory.CreateDirectory(temp);
        _environment.Set("CONFIG_DIR", requested);
        _environment.Set("TMPDIR", temp);
        var fallback = Path.Join(temp, "ComicMaintainer");
        var logs = new ProgramTests.CapturingLoggerProvider();

        await using var factory = CreateFactory(logs);

        AssertAllStateIsIn(fallback, factory.Services);
        var warning = Assert.Single(logs.ProgramEntries(
            "Cannot create the config directory {RequestedConfigDirectory}; using {ConfigDirectory} instead, which may not survive a restart. Set CONFIG_DIR to a writable directory."));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(requested, warning.Values["RequestedConfigDirectory"]);
        Assert.Equal(fallback, warning.Values["ConfigDirectory"]);
        Assert.False(Directory.Exists(ConfiguredDirectory));
    }

    private WebApplicationFactory<Program> CreateFactory(ILoggerProvider? logs = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AppSettings:WatchedDirectory", Path.Join(_root, "watched"));
            builder.UseSetting("AppSettings:DuplicateDirectory", Path.Join(_root, "duplicates"));
            builder.UseSetting("AppSettings:ConfigDirectory", ConfiguredDirectory);
            if (logs is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(
                    _ => new LoggerFactory([logs], new LoggerFilterOptions { MinLevel = LogLevel.Information })));
            }
        });
    }

    /// <summary>
    /// Asserts that the started host keeps its database, DataProtection keys, user-settings.json and
    /// backups in <paramref name="directory"/>, and reports it as AppSettings.ConfigDirectory.
    /// </summary>
    private static void AssertAllStateIsIn(string directory, IServiceProvider services)
    {
        // Resolving the services started the host, which migrated (so created) the database.
        var database = Path.Join(directory, "comicmaintainer.db");
        using (var scope = services.CreateScope())
        {
            var connectionString = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>().Database.GetConnectionString();
            Assert.Equal(database, new SqliteConnectionStringBuilder(connectionString).DataSource);
        }

        Assert.True(File.Exists(database), $"{database} should exist");

        var keysDirectory = Path.Join(directory, "DataProtection-Keys");
        var keys = Assert.IsType<FileSystemXmlRepository>(services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Equal(keysDirectory, keys.Directory.FullName);
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(nameof(ConfigDirectoryStartupTests)).Protect("payload");
        Assert.NotEmpty(Directory.GetFiles(keysDirectory, "key-*.xml"));

        Assert.True(File.Exists(Path.Join(directory, "user-settings.json")), $"user-settings.json should be in {directory}");
        Assert.Equal(directory, services.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue.ConfigDirectory);
        Assert.Equal(Path.Join(directory, "backups"), services.GetRequiredService<IDatabaseBackupService>().BackupDirectory);
    }
}
