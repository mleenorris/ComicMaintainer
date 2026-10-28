using System.Reflection;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for <c>WebApplicationExtensions.InitializeComicMaintainerDatabaseAsync</c> (migrations,
/// role seeding and the optional environment-seeded admin user). The app is built with the
/// production data and auth registrations on a throw-away SQLite file. The admin seeding reads
/// ADMIN_* environment variables (ADMIN_PASSWORD_FILE included), so the tests run in the non-parallel
/// environment collection.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class WebApplicationExtensionsTests : IDisposable
{
    private const string AdminPassword = "Passw0rdOk";

    private static readonly Assembly WebApi = typeof(Program).Assembly;

    // The startup extension classes are internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<WebApplication, ILogger, Task> InitializeDatabase = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.WebApplicationExtensions", throwOnError: true)!
        .GetMethod("InitializeComicMaintainerDatabaseAsync", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<WebApplication, ILogger, Task>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerData = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerData", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerAuth = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.AuthServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerAuth", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cm-dbinit-" + Guid.NewGuid().ToString("N"));

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("ADMIN_USERNAME", "ADMIN_PASSWORD", "ADMIN_PASSWORD_FILE", "ADMIN_EMAIL", "JWT_SECRET", "JWT_SECRET_FILE", "AUTHELIA_ENABLED", "EFCORE_QUERY_LOGGING");

    public WebApplicationExtensionsTests()
    {
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        _environment.Dispose();
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task MigratesTheDatabaseAndSeedsTheRoles()
    {
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var roles = await db.Roles.OrderBy(r => r.Name).ToListAsync();
        Assert.Equal(new[] { "Admin", "ReadOnly", "User" }, roles.Select(r => r.Name));
        Assert.Equal(new[] { "Admin role", "ReadOnly role", "User role" }, roles.Select(r => r.Description));
        // No admin account is created unless the environment asks for one.
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task SeedsTheAdminUserDescribedByTheEnvironment()
    {
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD", AdminPassword);
        _environment.Set("ADMIN_EMAIL", "ops@example.com");
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByNameAsync("root-admin");
        Assert.NotNull(admin);
        Assert.Equal("ops@example.com", admin.Email);
        Assert.Equal("Administrator", admin.FullName);
        Assert.True(admin.IsActive);
        Assert.True(admin.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(admin, AdminPassword));
        Assert.Equal(new[] { "Admin" }, await users.GetRolesAsync(admin));
    }

    [Fact]
    public async Task DerivesTheAdminEmailFromTheUsernameWhenAdminEmailIsNotSet()
    {
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD", AdminPassword);
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        var admin = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("root-admin");
        Assert.Equal("root-admin@comicmaintainer.local", admin?.Email);
    }

    [Fact]
    public async Task LeavesAnExistingAdminUserUntouchedOnLaterStarts()
    {
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD", AdminPassword);
        _environment.Set("ADMIN_EMAIL", "ops@example.com");
        await using (var firstStart = BuildApp())
        {
            await InitializeDatabase(firstStart, NullLogger.Instance);
        }

        _environment.Set("ADMIN_PASSWORD", "Chang3dPassword");
        _environment.Set("ADMIN_EMAIL", "changed@example.com");
        await using var secondStart = BuildApp();

        await InitializeDatabase(secondStart, NullLogger.Instance);

        using var scope = secondStart.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = Assert.Single(await users.Users.ToListAsync());
        Assert.Equal("ops@example.com", admin.Email);
        Assert.True(await users.CheckPasswordAsync(admin, AdminPassword));
        Assert.False(await users.CheckPasswordAsync(admin, "Chang3dPassword"));
    }

    [Fact]
    public async Task SkipsAnAdminUserWhosePasswordDoesNotMeetThePolicy()
    {
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD", "weak");
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Equal(3, await db.Roles.CountAsync());
    }

    [Theory]
    [InlineData("root-admin", null)]
    [InlineData(null, AdminPassword)]
    public async Task SeedsNoAdminUnlessBothUsernameAndPasswordAreSet(string? username, string? password)
    {
        _environment.Set("ADMIN_USERNAME", username);
        _environment.Set("ADMIN_PASSWORD", password);
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>().Users.ToListAsync());
    }

    // The Docker-secrets setup of docs/DOCKER_DEPLOYMENT_DOTNET.md. Files made with `echo` end with a line break that
    // is not part of the password.
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public async Task SeedsTheAdminUserWithThePasswordInAdminPasswordFile(string lineBreak)
    {
        var passwordFile = Path.Join(_configDirectory, "admin_password");
        await File.WriteAllTextAsync(passwordFile, AdminPassword + lineBreak);
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD_FILE", passwordFile);
        await using var app = BuildApp();

        await InitializeDatabase(app, NullLogger.Instance);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByNameAsync("root-admin");
        Assert.NotNull(admin);
        Assert.True(await users.CheckPasswordAsync(admin, AdminPassword));
        Assert.Equal(new[] { "Admin" }, await users.GetRolesAsync(admin));
    }

    [Fact]
    public async Task RefusesToSeedFromBothAdminPasswordAndAdminPasswordFile()
    {
        var passwordFile = Path.Join(_configDirectory, "admin_password");
        await File.WriteAllTextAsync(passwordFile, AdminPassword);
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD", "Oth3rPassword");
        _environment.Set("ADMIN_PASSWORD_FILE", passwordFile);
        await using var app = BuildApp();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeDatabase(app, NullLogger.Instance));

        Assert.StartsWith("Both ADMIN_PASSWORD and ADMIN_PASSWORD_FILE are set. Set only one of them", exception.Message);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task RefusesToSeedFromAnAdminPasswordFileThatCannotBeRead()
    {
        var passwordFile = Path.Join(_configDirectory, "missing", "admin_password");
        _environment.Set("ADMIN_USERNAME", "root-admin");
        _environment.Set("ADMIN_PASSWORD_FILE", passwordFile);
        await using var app = BuildApp();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeDatabase(app, NullLogger.Instance));

        Assert.StartsWith($"ADMIN_PASSWORD_FILE is set to '{passwordFile}', which cannot be read: ", exception.Message);
    }

    [Fact]
    public async Task ToleratesARoleThatAnotherInstanceInsertedConcurrently()
    {
        // "Admin" already exists, but the first existence check misses it - as if another
        // instance inserted it between the check and our insert.
        var race = new RoleRace("Admin", reportMissingTimes: 1);
        await using var app = BuildApp(services => UseRacingRoleManager(services, race));
        await SeedRoleAsync(app, "Admin");

        await InitializeDatabase(app, NullLogger.Instance);

        Assert.Equal(1, race.FailedInserts);
        using var scope = app.Services.CreateScope();
        var roleNames = await scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>()
            .Roles.Select(r => r.Name).ToListAsync();
        // The failed insert is detached, so the remaining roles are still created.
        Assert.Equal(new[] { "Admin", "ReadOnly", "User" }, roleNames.Order());
    }

    [Fact]
    public async Task RethrowsAFailedRoleInsertWhenTheRoleStillDoesNotExist()
    {
        var race = new RoleRace("Admin", reportMissingTimes: int.MaxValue);
        await using var app = BuildApp(services => UseRacingRoleManager(services, race));
        await SeedRoleAsync(app, "Admin");

        await Assert.ThrowsAsync<DbUpdateException>(() => InitializeDatabase(app, NullLogger.Instance));

        Assert.Equal(1, race.FailedInserts);
    }

    private WebApplication BuildApp(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = _configDirectory,
        });
        // WebApplication needs an IServer to be built; the app is never started.
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppSettings:ConfigDirectory"] = _configDirectory,
            ["JwtSettings:Secret"] = "database-init-tests-secret-0123456789abcdef",
        });
        builder.Services.AddLogging();
        AddComicMaintainerData(builder.Services, builder.Configuration);
        AddComicMaintainerAuth(builder.Services, builder.Configuration);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    private static void UseRacingRoleManager(IServiceCollection services, RoleRace race)
    {
        services.AddSingleton(race);
        services.AddScoped<RoleManager<ApplicationRole>, RacingRoleManager>();
    }

    private static async Task SeedRoleAsync(WebApplication app, string roleName)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>().Database.MigrateAsync();
        var result = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>()
            .CreateAsync(new ApplicationRole { Name = roleName, Description = $"{roleName} role" });
        Assert.True(result.Succeeded);
    }

    /// <summary>Shared state for <see cref="RacingRoleManager"/>.</summary>
    public sealed class RoleRace(string missingRole, int reportMissingTimes)
    {
        public string MissingRole { get; } = missingRole;

        public int ReportMissingTimes { get; } = reportMissingTimes;

        public int MissingReports { get; set; }

        public int FailedInserts { get; set; }
    }

    /// <summary>
    /// A real <see cref="RoleManager{TRole}"/> whose existence check reports <see cref="RoleRace.MissingRole"/>
    /// as missing, and which skips the duplicate-name validator (that check already "passed" before the other
    /// instance inserted the row), so the insert itself fails on the unique index like a real race.
    /// </summary>
    public sealed class RacingRoleManager(
        IRoleStore<ApplicationRole> store,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        ILogger<RoleManager<ApplicationRole>> logger,
        RoleRace race)
        : RoleManager<ApplicationRole>(store, [], keyNormalizer, errors, logger)
    {
        public override Task<bool> RoleExistsAsync(string roleName)
        {
            if (roleName == race.MissingRole && race.MissingReports < race.ReportMissingTimes)
            {
                race.MissingReports++;
                return Task.FromResult(false);
            }

            return base.RoleExistsAsync(roleName);
        }

        public override async Task<IdentityResult> CreateAsync(ApplicationRole role)
        {
            try
            {
                return await base.CreateAsync(role);
            }
            catch (DbUpdateException)
            {
                race.FailedInserts++;
                throw;
            }
        }
    }
}
