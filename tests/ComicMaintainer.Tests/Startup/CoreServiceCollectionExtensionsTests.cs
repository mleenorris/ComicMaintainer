using System.Reflection;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the persistence, data-protection and hosted-service registrations made by
/// <c>CoreServiceCollectionExtensions</c>, exercised on a bare <see cref="ServiceCollection"/>.
/// </summary>
public sealed class CoreServiceCollectionExtensionsTests : IDisposable
{
    // The extension class is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Type ExtensionsType = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!;

    private static readonly Func<string?, string?> ResolveSqliteDatabasePath = ExtensionsType
        .GetMethod("ResolveSqliteDatabasePath", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<string?, string?>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerData = ExtensionsType
        .GetMethod("AddComicMaintainerData", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<IServiceCollection, IServiceCollection> AddComicMaintainerHostedServices = ExtensionsType
        .GetMethod("AddComicMaintainerHostedServices", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IServiceCollection>>();

    private static readonly Type[] AlwaysRegisteredJobHandlers =
    [
        typeof(MetadataAuditJobHandler),
        typeof(FileNamingAuditJobHandler),
        typeof(SeriesLanguageAuditJobHandler),
        typeof(LibraryScanJobHandler),
        typeof(MetadataBackfillJobHandler),
        typeof(MissingIssuesCheckJobHandler),
        typeof(SuwayomiDownloadImportJobHandler),
    ];

    private readonly string _root = Path.Join(Path.GetTempPath(), "cm-coreservices-" + Guid.NewGuid().ToString("N"));

    public CoreServiceCollectionExtensionsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Data Source=")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:MEMORY:;Pooling=True")]
    [InlineData("Data Source=library;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source='file:library?mode=memory&cache=shared'")]
    [InlineData("Data Source=file::memory:")]
    [InlineData("Data Source='file::memory:?cache=shared'")]
    [InlineData("Data Source=file:/config/library.db;Mode=Memory")]
    [InlineData("Data Source='file:/config/library.db?mode=rwc&mode=memory'")]
    [InlineData("Data Source='file:/config/library.db?vfs=memdb'")]
    // Parameters are percent-decoded, and SQLite ignores the rest of a value after %00.
    [InlineData("Data Source='file:/config/library.db?%6Dode=%6Demory'")]
    [InlineData("Data Source='file:/config/library.db?mode=memory%00x'")]
    // An empty file name is a temporary database that SQLite deletes when it is closed.
    [InlineData("Data Source=file:")]
    [InlineData("Data Source='file:?cache=shared'")]
    [InlineData("Data Source=file://localhost")]
    // SQLite refuses to open a URI naming another host.
    [InlineData("Data Source=file://server/config/library.db")]
    public void ResolveSqliteDatabasePath_ReturnsNullWhenThereIsNoDatabaseFile(string? connectionString)
    {
        Assert.Null(ResolveSqliteDatabasePath(connectionString));
    }

    [Theory]
    [InlineData("Data Source=file:/config/library.db?cache=shared", "/config/library.db")]
    [InlineData("Data Source=file:///config/library.db", "/config/library.db")]
    [InlineData("Data Source='file://localhost/config/library.db?mode=rwc#fragment';Pooling=True", "/config/library.db")]
    [InlineData("Data Source=file:/config/my%20library%23%3F.db", "/config/my library#?.db")]
    [InlineData("Data Source=file:data/library.db?cache=private", "data/library.db")]
    [InlineData("Data Source=file:/config/library.db%00.bak", "/config/library.db")]
    [InlineData("Data Source='file:/config/library.db?psow&cache=shared'", "/config/library.db")]
    // As in SQLite, parameter names and values are case-sensitive and the last mode wins.
    [InlineData("Data Source=file:/config/library.db?MODE=memory", "/config/library.db")]
    [InlineData("Data Source='file:/config/library.db?mode=memory&mode=rwc'", "/config/library.db")]
    // SQLite only reads a lower-case "file:" prefix as a URI.
    [InlineData("Data Source=FILE:library.db", "FILE:library.db")]
    public void ResolveSqliteDatabasePath_ResolvesAFileUriToTheFileItNames(string connectionString, string expectedPath)
    {
        Assert.Equal(Path.GetFullPath(expectedPath), ResolveSqliteDatabasePath(connectionString));
    }

    [Fact]
    public void ResolveSqliteDatabasePath_ReadsAFileUriPathAsSqliteDoesOnThisSystem()
    {
        // SQLite on Windows drops the "/" in front of a drive letter; elsewhere "C:" is a directory name.
        Assert.Equal(
            OperatingSystem.IsWindows() ? @"C:\config\library.db" : "/C:/config/library.db",
            ResolveSqliteDatabasePath("Data Source=file:///C:/config/library.db"));

        // An empty authority followed by "//": the same file on Unix, a UNC path on Windows.
        Assert.Equal(
            Path.GetFullPath("//config/library.db"),
            ResolveSqliteDatabasePath("Data Source=file:////config/library.db"));
    }

    [Fact]
    public void ResolveSqliteDatabasePath_ReturnsTheAbsolutePathOfAFileDatabase()
    {
        var databasePath = Path.Join(_root, "library.db");

        var resolved = ResolveSqliteDatabasePath($"Data Source={databasePath};Pooling=True;Default Timeout=30");

        Assert.Equal(databasePath, resolved);
    }

    [Fact]
    public void ResolveSqliteDatabasePath_ResolvesARelativePathAgainstTheCurrentDirectory()
    {
        var relative = Path.Join("data", "library.db");

        var resolved = ResolveSqliteDatabasePath($"Data Source={relative}");

        Assert.Equal(Path.GetFullPath(relative), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved!));
    }

    [Fact]
    public void AddComicMaintainerData_WithoutAConnectionString_UsesADatabaseInTheConfigDirectory()
    {
        var expectedDatabase = Path.Join(_root, "comicmaintainer.db");

        using var provider = BuildDataServices(("AppSettings:ConfigDirectory", _root));

        using (var context = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>().CreateDbContext())
        {
            AssertNormalizedConnectionString(context, expectedDatabase);
        }

        using (var scope = provider.CreateScope())
        {
            AssertNormalizedConnectionString(scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>(), expectedDatabase);
        }

        // Backups are enabled for a file database and operate on that same file.
        var backups = provider.GetRequiredService<IDatabaseBackupService>();
        Assert.False(backups.IsRestorePending);
        File.WriteAllText(DatabaseBackupService.GetPendingRestorePath(expectedDatabase), string.Empty);
        Assert.True(backups.IsRestorePending);
    }

    [Fact]
    public void AddComicMaintainerData_UsesTheConfiguredConnectionString()
    {
        var customDatabase = Path.Join(_root, "custom", "library.db");

        using var provider = BuildDataServices(
            ("AppSettings:ConfigDirectory", _root),
            ("ConnectionStrings:DefaultConnection", $"Data Source={customDatabase}"));

        using (var context = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>().CreateDbContext())
        {
            AssertNormalizedConnectionString(context, customDatabase);
        }

        var backups = provider.GetRequiredService<IDatabaseBackupService>();
        Directory.CreateDirectory(Path.GetDirectoryName(customDatabase)!);
        File.WriteAllText(DatabaseBackupService.GetPendingRestorePath(customDatabase), string.Empty);
        Assert.True(backups.IsRestorePending);
        Assert.False(File.Exists(Path.Join(_root, "comicmaintainer.db")));
    }

    [Fact]
    public void AddComicMaintainerData_WithAnInMemoryDatabase_DoesNotRegisterBackups()
    {
        var services = new ServiceCollection();

        AddComicMaintainerData(services, BuildConfiguration(
            ("AppSettings:ConfigDirectory", _root),
            ("ConnectionStrings:DefaultConnection", "Data Source=:memory:")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDatabaseBackupService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDbContextFactory<ComicMaintainerDbContext>));
    }

    [Fact]
    public void AddComicMaintainerData_WithAFileUriConnectionString_BacksUpTheFileItNames()
    {
        var databasePath = Path.Join(_root, "library.db");

        using var provider = BuildDataServices(
            ("AppSettings:ConfigDirectory", _root),
            ("ConnectionStrings:DefaultConnection", $"Data Source=file:{databasePath}?cache=shared"));

        var backups = provider.GetRequiredService<IDatabaseBackupService>();
        Assert.False(backups.IsRestorePending);
        File.WriteAllText(DatabaseBackupService.GetPendingRestorePath(databasePath), string.Empty);
        Assert.True(backups.IsRestorePending);
    }

    [Fact]
    public void AddComicMaintainerData_PersistsDataProtectionKeysInTheConfigDirectory()
    {
        var keysDirectory = Path.Join(_root, "DataProtection-Keys");

        using var provider = BuildDataServices(("AppSettings:ConfigDirectory", _root));

        Assert.True(Directory.Exists(keysDirectory));
        var repository = Assert.IsType<FileSystemXmlRepository>(
            provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Equal(Path.GetFullPath(keysDirectory), Path.GetFullPath(repository.Directory.FullName));
        Assert.Equal("ComicMaintainer", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);

        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("startup-tests");
        var protectedPayload = protector.Protect("payload");

        Assert.Equal("payload", protector.Unprotect(protectedPayload));
        Assert.NotEmpty(Directory.GetFiles(keysDirectory, "*.xml"));
    }

    [Fact]
    public void AddComicMaintainerData_WhenTheKeysDirectoryCannotBeCreated_SkipsPersistentDataProtection()
    {
        // A plain file where the keys directory should be makes Directory.CreateDirectory throw.
        var blockingFile = Path.Join(_root, "DataProtection-Keys");
        File.WriteAllText(blockingFile, "not a directory");
        var services = new ServiceCollection();

        var exception = Record.Exception(() => AddComicMaintainerData(services, BuildConfiguration(("AppSettings:ConfigDirectory", _root))));

        Assert.Null(exception);
        Assert.True(File.Exists(blockingFile));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDataProtectionProvider));
        // The rest of the persistence registrations are unaffected.
        Assert.Contains(services, d => d.ServiceType == typeof(IDbContextFactory<ComicMaintainerDbContext>));
        Assert.Contains(services, d => d.ServiceType == typeof(IDatabaseBackupService));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddComicMaintainerHostedServices_RegistersTheBackupJobOnlyWhenBackupsAreAvailable(bool backupsAvailable)
    {
        var services = new ServiceCollection();
        if (backupsAvailable)
        {
            services.AddSingleton(Mock.Of<IDatabaseBackupService>());
        }

        AddComicMaintainerHostedServices(services);

        var handlerTypes = services
            .Where(d => d.ServiceType == typeof(IScheduledJobHandler))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.All(AlwaysRegisteredJobHandlers, handler => Assert.Contains(handler, handlerTypes));
        Assert.Equal(backupsAvailable, handlerTypes.Contains(typeof(DatabaseBackupJobHandler)));
        Assert.Equal(AlwaysRegisteredJobHandlers.Length + (backupsAvailable ? 1 : 0), handlerTypes.Count);

        Assert.Contains(services, d => d.ServiceType == typeof(IScheduledJobService));
        Assert.Contains(services, d => d.ServiceType == typeof(ScheduledJobsHostedService));
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.Contains(typeof(FileWatcherHostedService), hostedServiceTypes);
        Assert.Contains(typeof(DatabaseCleanupHostedService), hostedServiceTypes);
        Assert.Contains(typeof(JobStateReconciliationHostedService), hostedServiceTypes);
        // The scheduled-jobs runner is registered through a factory so it is a single instance.
        Assert.Equal(6, hostedServiceTypes.Count);
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
    }

    private static ServiceProvider BuildDataServices(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddComicMaintainerData(services, BuildConfiguration(settings));
        return services.BuildServiceProvider();
    }

    private static void AssertNormalizedConnectionString(ComicMaintainerDbContext context, string expectedDatabase)
    {
        var builder = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        Assert.Equal(expectedDatabase, builder.DataSource);
        Assert.True(builder.Pooling);
        Assert.Equal(30, builder.DefaultTimeout);
    }
}
