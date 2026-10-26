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
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-coreservices-" + Guid.NewGuid().ToString("N"));

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
        }
        catch (UnauthorizedAccessException)
        {
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
    public void ResolveSqliteDatabasePath_ReturnsNullWhenThereIsNoDatabaseFile(string? connectionString)
    {
        Assert.Null(ResolveSqliteDatabasePath(connectionString));
    }

    [Fact]
    public void ResolveSqliteDatabasePath_ReturnsTheAbsolutePathOfAFileDatabase()
    {
        var databasePath = Path.Combine(_root, "library.db");

        var resolved = ResolveSqliteDatabasePath($"Data Source={databasePath};Pooling=True;Default Timeout=30");

        Assert.Equal(databasePath, resolved);
    }

    [Fact]
    public void ResolveSqliteDatabasePath_ResolvesARelativePathAgainstTheCurrentDirectory()
    {
        var relative = Path.Combine("data", "library.db");

        var resolved = ResolveSqliteDatabasePath($"Data Source={relative}");

        Assert.Equal(Path.GetFullPath(relative), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved!));
    }

    [Fact]
    public void AddComicMaintainerData_WithoutAConnectionString_UsesADatabaseInTheConfigDirectory()
    {
        var expectedDatabase = Path.Combine(_root, "comicmaintainer.db");

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
        var customDatabase = Path.Combine(_root, "custom", "library.db");

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
        Assert.False(File.Exists(Path.Combine(_root, "comicmaintainer.db")));
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
    public void AddComicMaintainerData_PersistsDataProtectionKeysInTheConfigDirectory()
    {
        var keysDirectory = Path.Combine(_root, "DataProtection-Keys");

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
        var blockingFile = Path.Combine(_root, "DataProtection-Keys");
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
