using System.Collections.Concurrent;
using System.Globalization;
using ComicMaintainer.Core.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Boots the real <c>Program</c> and checks the startup summary its top-level statements log once the
/// pipeline is configured. Every host gets its own temp directories (so its own SQLite database) and a
/// capturing <see cref="ILoggerFactory"/>: the Serilog factory the app registers writes through the
/// process-wide <c>Log.Logger</c>, which every host started in parallel replaces, so its output cannot be
/// attributed to one host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProgramTests : IDisposable
{
    private const string WatcherStatusTemplate = "Watcher Status: {Status} (Rename: {Rename}, Normalize: {Normalize})";
    private const string PaddingOutOfRangeTemplate =
        "IssueNumberPadding {Padding} is outside the supported range 0-{MaxPadding}; using {EffectivePadding} instead";

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "Program-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider _logs = new();

    public ProgramTests()
    {
        Directory.CreateDirectory(WatchedDirectory);
        Directory.CreateDirectory(DuplicateDirectory);
        Directory.CreateDirectory(ConfigDirectory);
    }

    private string WatchedDirectory => Path.Join(_root, "watched");

    private string DuplicateDirectory => Path.Join(_root, "duplicates");

    private string ConfigDirectory => Path.Join(_root, "config");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData(false, false, "Disabled")]
    [InlineData(false, true, "Enabled")]
    [InlineData(true, false, "Enabled")]
    public async Task Startup_ReportsTheWatcherAsEnabledWhenRenamingOrNormalizingIsOn(bool rename, bool normalize, string expectedStatus)
    {
        await StartAsync(
            settings =>
            {
                Assert.Equal(rename, settings.WatcherEnableRename);
                Assert.Equal(normalize, settings.WatcherEnableNormalize);
            },
            ("AppSettings:WatcherEnableRename", rename.ToString()),
            ("AppSettings:WatcherEnableNormalize", normalize.ToString()));

        var status = Assert.Single(_logs.ProgramEntries(WatcherStatusTemplate));
        Assert.Equal(LogLevel.Information, status.Level);
        Assert.Equal(expectedStatus, status.Values["Status"]);
        Assert.Equal(rename, status.Values["Rename"]);
        Assert.Equal(normalize, status.Values["Normalize"]);
        Assert.Equal($"Watcher Status: {expectedStatus} (Rename: {rename}, Normalize: {normalize})", status.Message);
    }

    [Fact]
    public async Task Startup_LogsTheBannerAndTheWatchedDirectory()
    {
        await StartAsync(settings => Assert.Equal(WatchedDirectory, settings.WatchedDirectory));

        var version = Assert.Single(_logs.ProgramEntries("║                  Version {Version}║"));
        Assert.Equal(typeof(Program).Assembly.GetName().Version!.ToString().PadRight(24), version.Values["Version"]);
        Assert.Single(_logs.ProgramEntries("Server started successfully"));
        var watched = Assert.Single(_logs.ProgramEntries("Watched Directory: {WatchedDir}"));
        Assert.Equal(WatchedDirectory, watched.Values["WatchedDir"]);
    }

    // user-settings.json and the environment bypass the settings API's range check.
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1_000_000_000, 10)]
    public async Task Startup_WarnsWhenTheConfiguredIssueNumberPaddingIsOutOfRange(int padding, int effectivePadding)
    {
        await StartAsync(
            settings => Assert.Equal(effectivePadding, settings.IssueNumberPadding),
            ("AppSettings:IssueNumberPadding", padding.ToString(CultureInfo.InvariantCulture)));

        var warning = Assert.Single(_logs.ProgramEntries(PaddingOutOfRangeTemplate));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(padding, warning.Values["Padding"]);
        Assert.Equal(10, warning.Values["MaxPadding"]);
        Assert.Equal(effectivePadding, warning.Values["EffectivePadding"]);
    }

    [Fact]
    public async Task Startup_DoesNotWarnAboutAnIssueNumberPaddingInRange()
    {
        await StartAsync(
            settings => Assert.Equal(10, settings.IssueNumberPadding),
            ("AppSettings:IssueNumberPadding", "10"));

        Assert.Single(_logs.ProgramEntries("Server started successfully"));
        Assert.Empty(_logs.ProgramEntries(PaddingOutOfRangeTemplate));
    }

    private async Task StartAsync(Action<AppSettings> assertSettings, params (string Key, string Value)[] settings)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = DuplicateDirectory,
            ["AppSettings:ConfigDirectory"] = ConfigDirectory,
            ["CONFIG_DIR"] = ConfigDirectory,
        };
        foreach (var (key, value) in settings)
        {
            overrides[key] = value;
        }

        await using var factory = new WebApplicationFactory<Program>();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // UseSetting values are visible while Program registers its services (the database lives in
            // ConfigDirectory). The in-memory source is added last, at Build, so it also wins over the
            // machine's user-settings.json, which Program registers after the UseSetting values.
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(
                _ => new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information })));
        });

        // Resolving the services starts the host, which completes only once Program has reached app.Run().
        assertSettings(host.Services.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue);
    }

    /// <summary>Records every log entry with its structured values.</summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IEnumerable<Entry> ProgramEntries(string template) =>
            _entries.Where(e => e.Category == "Program" && e.Template == template).ToList();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        public sealed record Entry(string Category, LogLevel Level, string? Template, IReadOnlyDictionary<string, object?> Values, string Message);

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        values[key] = value;
                    }
                }

                owner._entries.Enqueue(new Entry(
                    category,
                    logLevel,
                    values.GetValueOrDefault("{OriginalFormat}") as string,
                    values,
                    formatter(state, exception)));
            }
        }
    }
}
