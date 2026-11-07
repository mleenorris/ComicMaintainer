using System.Reflection;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// The production Identity, data and auth registrations (<c>AddComicMaintainerData</c>,
/// <c>AddComicMaintainerAuth</c>) on a throw-away, migrated SQLite database, for tests of the auth services against
/// the real <see cref="UserManager{TUser}"/>: lockout, security stamps, password validators and the database.
/// </summary>
internal sealed class AuthTestHost : IAsyncDisposable
{
    public const string JwtSecret = "auth-test-host-secret-0123456789abcdefghijklmnop";

    private static readonly Assembly WebApi = typeof(Program).Assembly;

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerSettings = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerSettings", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerData = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerData", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerAuth = WebApi
        .GetType("ComicMaintainer.WebApi.Startup.AuthServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerAuth", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private readonly string _directory = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "Auth-" + Guid.NewGuid().ToString("N"));

    private AuthTestHost(Dictionary<string, string?>? settings, Action<IServiceCollection>? configureServices)
    {
        Directory.CreateDirectory(_directory);
        var values = new Dictionary<string, string?>
        {
            ["AppSettings:ConfigDirectory"] = _directory,
            ["JwtSettings:Secret"] = JwtSecret,
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddMemoryCache();
        AddComicMaintainerSettings(services, Configuration);
        AddComicMaintainerData(services, Configuration);
        AddComicMaintainerAuth(services, Configuration);
        services.AddScoped<IAuthService, AuthService>();
        configureServices?.Invoke(services);
        Services = services.BuildServiceProvider();
    }

    public IConfiguration Configuration { get; }

    public ServiceProvider Services { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Builds the host, migrates its database and seeds the Admin, User and ReadOnly roles.</summary>
    public static async Task<AuthTestHost> StartAsync(
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new AuthTestHost(settings, configureServices);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>().Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in new[] { "Admin", "User", "ReadOnly" })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new ApplicationRole { Name = role, Description = $"{role} role" });
            }
        }

        return host;
    }

    /// <summary>Creates an active account with <paramref name="password"/> in <paramref name="role"/>.</summary>
    public async Task<ApplicationUser> CreateUserAsync(string username, string password, string role = "User")
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = username, Email = $"{username}@example.com", IsActive = true };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    /// <summary>Runs <paramref name="action"/> with services from a new scope, like one request.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithAuthServiceAsync<T>(Func<IAuthService, Task<T>> action) =>
        InScopeAsync(services => action(services.GetRequiredService<IAuthService>()));

    public Task<ApplicationUser?> FindUserAsync(string username) =>
        InScopeAsync(services => services.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(username));

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_directory);
    }

    /// <summary>Records every log entry with its category.</summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider._entries)
                {
                    provider._entries.Add((category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
