using System.Reflection;
using Serilog;
using Serilog.Core;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the log sinks and levels set up by <c>LoggingExtensions.ConfigureSinks</c>.
/// </summary>
public sealed class LoggingExtensionsTests : IDisposable
{
    // LoggingExtensions is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<LoggerConfiguration, string, long, LoggerConfiguration> ConfigureSinks = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.LoggingExtensions", throwOnError: true)!
        .GetMethod("ConfigureSinks", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<LoggerConfiguration, string, long, LoggerConfiguration>>();

    private readonly string _configDir = Directory.CreateTempSubdirectory("logging-tests-").FullName;

    public void Dispose() => Directory.Delete(_configDir, recursive: true);

    [Fact]
    public void EntityFrameworkCoreDebugEventsAreLeftOutAndItsWarningsKept()
    {
        using (var logger = CreateLogger())
        {
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.EntityFrameworkCore.Database.Connection")
                .Debug("Opening connection to database");
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.EntityFrameworkCore.ChangeTracking")
                .Debug("Context started tracking an entity");
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.EntityFrameworkCore.Query")
                .Warning("A query warning");
            logger.ForContext(Constants.SourceContextPropertyName, "ComicMaintainer.Core.Services.FileStoreService")
                .Debug("An app debug message");
        }

        var debugLog = ReadLog("debug");
        Assert.DoesNotContain("Opening connection to database", debugLog);
        Assert.DoesNotContain("Context started tracking an entity", debugLog);
        Assert.Contains("A query warning", debugLog);
        Assert.Contains("An app debug message", debugLog);
    }

    // The file sinks are buffered; nothing written before the app stops is lost.
    [Fact]
    public void BufferedLogLinesReachTheirFilesWhenTheLoggerIsDisposed()
    {
        using (var logger = CreateLogger())
        {
            logger.ForContext(Constants.SourceContextPropertyName, "ComicMaintainer.WebApi.Authentication.AutheliaAuthenticationHandler")
                .Information("An auth message");
            logger.ForContext(Constants.SourceContextPropertyName, "ComicMaintainer.Core.Services.FileWatcherService")
                .Information("A watcher message");
            logger.ForContext(Constants.SourceContextPropertyName, "ComicMaintainer.Core.Services.SeriesMetadataCacheService")
                .Information("A metadata message");
            logger.ForContext(Constants.SourceContextPropertyName, "ComicMaintainer.Core.Services.ComicFileProcessor")
                .Information("An app message");
        }

        Assert.Contains("An auth message", ReadLog("auth"));
        Assert.Contains("A watcher message", ReadLog("watcher"));
        Assert.Contains("A metadata message", ReadLog("metadata"));
        var appLog = ReadLog("app");
        Assert.Contains("An app message", appLog);
        Assert.DoesNotContain("An auth message", appLog);
    }

    private Logger CreateLogger() => ConfigureSinks(new LoggerConfiguration(), _configDir, 10 * 1024 * 1024).CreateLogger();

    // Files roll daily, so the date is part of the name (debug20261113.log).
    private string ReadLog(string name) =>
        string.Concat(Directory.GetFiles(_configDir, $"{name}*.log").Select(File.ReadAllText));
}
