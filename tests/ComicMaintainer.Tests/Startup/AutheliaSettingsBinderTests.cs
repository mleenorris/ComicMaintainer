using System.Net;
using System.Reflection;
using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Regression tests for <c>AutheliaSettingsBinder</c>, the only reader of the <c>AUTHELIA_*</c> environment
/// variables: every variable documented in docs/AUTHELIA.md overrides its setting, and the startup registrations
/// (in particular the <c>CanAdminister</c> fallback for deployments without admin groups) see the same effective
/// settings as the Authelia handler. The variables are process-wide, so the tests run in the non-parallel
/// environment collection with all of them cleared.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class AutheliaSettingsBinderTests : IDisposable
{
    private const string ProxyAddress = "172.18.0.2";

    private static readonly string[] AutheliaVariables =
    [
        "AUTHELIA_ENABLED", "AUTHELIA_TRUSTED_PROXIES", "AUTHELIA_USER_HEADER", "AUTHELIA_EMAIL_HEADER",
        "AUTHELIA_NAME_HEADER", "AUTHELIA_GROUPS_HEADER", "AUTHELIA_DEFAULT_ROLE", "AUTHELIA_ADMIN_GROUPS",
    ];

    // The startup extension classes are internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerSettings =
        StartupMethod("CoreServiceCollectionExtensions", "AddComicMaintainerSettings");

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerData =
        StartupMethod("CoreServiceCollectionExtensions", "AddComicMaintainerData");

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerAuth =
        StartupMethod("AuthServiceCollectionExtensions", "AddComicMaintainerAuth");

    private static readonly Func<WebApplication, ILogger, Task> InitializeDatabase = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.WebApplicationExtensions", throwOnError: true)!
        .GetMethod("InitializeComicMaintainerDatabaseAsync", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<WebApplication, ILogger, Task>>();

    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "cm-authelia-env-" + Guid.NewGuid().ToString("N"));

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new([.. AutheliaVariables, "JWT_SECRET", "JWT_SECRET_FILE", "ADMIN_USERNAME", "ADMIN_PASSWORD", "ADMIN_PASSWORD_FILE", "ADMIN_EMAIL", "EFCORE_QUERY_LOGGING"]);

    public AutheliaSettingsBinderTests()
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

    [Theory]
    [InlineData("AUTHELIA_TRUSTED_PROXIES", nameof(AutheliaSettings.TrustedProxies))]
    [InlineData("AUTHELIA_USER_HEADER", nameof(AutheliaSettings.UserHeader))]
    [InlineData("AUTHELIA_EMAIL_HEADER", nameof(AutheliaSettings.EmailHeader))]
    [InlineData("AUTHELIA_NAME_HEADER", nameof(AutheliaSettings.NameHeader))]
    [InlineData("AUTHELIA_GROUPS_HEADER", nameof(AutheliaSettings.GroupsHeader))]
    [InlineData("AUTHELIA_DEFAULT_ROLE", nameof(AutheliaSettings.DefaultRole))]
    [InlineData("AUTHELIA_ADMIN_GROUPS", nameof(AutheliaSettings.AdminGroups))]
    public void EachDocumentedVariable_OverridesItsConfiguredSetting(string variable, string setting)
    {
        _environment.Set(variable, "from-environment");

        var settings = GetAutheliaOptions(($"AutheliaSettings:{setting}", "from-configuration"));

        Assert.Equal("from-environment", typeof(AutheliaSettings).GetProperty(setting)!.GetValue(settings));
    }

    [Theory]
    [InlineData("true", "false", true)]
    [InlineData("TRUE", "false", true)]
    [InlineData("false", "true", false)]
    [InlineData("yes", "true", false)]
    public void AutheliaEnabledVariable_OverridesTheConfiguredSetting(string value, string configured, bool expected)
    {
        _environment.Set("AUTHELIA_ENABLED", value);

        Assert.Equal(expected, GetAutheliaOptions(("AutheliaSettings:Enabled", configured)).Enabled);
    }

    [Fact]
    public void UnsetVariables_KeepTheConfiguredSettings()
    {
        var settings = GetAutheliaOptions(
            ("AutheliaSettings:Enabled", "true"),
            ("AutheliaSettings:TrustedProxies", "10.0.0.5"),
            ("AutheliaSettings:UserHeader", "X-User"),
            ("AutheliaSettings:EmailHeader", "X-Email"),
            ("AutheliaSettings:NameHeader", "X-Name"),
            ("AutheliaSettings:GroupsHeader", "X-Groups"),
            ("AutheliaSettings:DefaultRole", "ReadOnly"),
            ("AutheliaSettings:AdminGroups", "admins"));

        Assert.True(settings.Enabled);
        Assert.Equal("10.0.0.5", settings.TrustedProxies);
        Assert.Equal("X-User", settings.UserHeader);
        Assert.Equal("X-Email", settings.EmailHeader);
        Assert.Equal("X-Name", settings.NameHeader);
        Assert.Equal("X-Groups", settings.GroupsHeader);
        Assert.Equal("ReadOnly", settings.DefaultRole);
        Assert.Equal("admins", settings.AdminGroups);
    }

    [Theory]
    [InlineData("users", "User", false)]
    [InlineData("admins", "Admin", true)]
    public async Task AdminGroupsSetOnlyInTheEnvironment_LimitAdministrationToThoseGroups(string groups, string expectedRole, bool expected)
    {
        EnableAutheliaThroughTheEnvironment(("AUTHELIA_ADMIN_GROUPS", "admins"));
        await using var app = BuildApp();
        await InitializeDatabase(app, NullLogger.Instance);
        await CreateAccountAsync(app, "alice");

        var (user, canAdminister) = await SignInAsync(app, AutheliaHeaders("alice", groups));

        Assert.Equal(expectedRole, Assert.Single(user.FindAll(ClaimTypes.Role)).Value);
        Assert.Equal(expected, canAdminister);
    }

    // The documented compatibility fallback: without admin groups nobody can be mapped to the Admin role, so every
    // user who is not ReadOnly may administer.
    [Theory]
    [InlineData(null, "User", true)]
    [InlineData("ReadOnly", "ReadOnly", false)]
    public async Task WithoutAdminGroupsAnywhere_EveryUserButReadOnlyMayAdminister(string? defaultRole, string expectedRole, bool expected)
    {
        EnableAutheliaThroughTheEnvironment(("AUTHELIA_DEFAULT_ROLE", defaultRole));
        await using var app = BuildApp();
        await InitializeDatabase(app, NullLogger.Instance);
        await CreateAccountAsync(app, "alice");

        var (user, canAdminister) = await SignInAsync(app, AutheliaHeaders("alice", "users"));

        Assert.Equal(expectedRole, Assert.Single(user.FindAll(ClaimTypes.Role)).Value);
        Assert.Equal(expected, canAdminister);
    }

    [Fact]
    public async Task HeaderVariables_ChooseTheHeadersTheHandlerReads()
    {
        EnableAutheliaThroughTheEnvironment(
            ("AUTHELIA_USER_HEADER", "X-Auth-User"),
            ("AUTHELIA_EMAIL_HEADER", "X-Auth-Email"),
            ("AUTHELIA_NAME_HEADER", "X-Auth-Name"),
            ("AUTHELIA_GROUPS_HEADER", "X-Auth-Groups"),
            ("AUTHELIA_ADMIN_GROUPS", "admins"));
        await using var app = BuildApp();
        await InitializeDatabase(app, NullLogger.Instance);
        await CreateAccountAsync(app, "carol");

        var (user, canAdminister) = await SignInAsync(
            app,
            [
                ("X-Auth-User", "carol"), ("X-Auth-Email", "carol@example.test"), ("X-Auth-Name", "Carol Reader"), ("X-Auth-Groups", "admins"),
                // The default headers no longer mean anything.
                .. AutheliaHeaders("mallory", "users"),
            ]);

        Assert.Equal("carol", user.Identity?.Name);
        Assert.Equal("carol@example.test", user.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal("admins", Assert.Single(user.FindAll("authelia_group")).Value);
        Assert.Equal("Admin", Assert.Single(user.FindAll(ClaimTypes.Role)).Value);
        Assert.True(canAdminister);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var carol = await users.FindByNameAsync("carol");
        Assert.Equal("carol@example.test", carol?.Email);
        Assert.Equal("Carol Reader", carol?.FullName);
        Assert.Null(await users.FindByNameAsync("mallory"));
    }

    private void EnableAutheliaThroughTheEnvironment(params (string Variable, string? Value)[] variables)
    {
        _environment.Set("AUTHELIA_ENABLED", "true");
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", "172.18.0.0/16");
        foreach (var (variable, value) in variables)
        {
            _environment.Set(variable, value);
        }
    }

    /// <summary>
    /// The production settings, data and auth registrations on a throw-away SQLite database, configured like the
    /// shipped appsettings.json (Authelia disabled, no admin groups), so all of the Authelia setup comes from the
    /// environment.
    /// </summary>
    private WebApplication BuildApp()
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
            ["JwtSettings:Secret"] = "authelia-binder-tests-secret-0123456789abcdef",
            ["AutheliaSettings:Enabled"] = "false",
            ["AutheliaSettings:AdminGroups"] = "",
            ["AutheliaSettings:TrustedProxies"] = "",
        });
        builder.Services.AddLogging();
        AddComicMaintainerSettings(builder.Services, builder.Configuration);
        AddComicMaintainerData(builder.Services, builder.Configuration);
        AddComicMaintainerAuth(builder.Services, builder.Configuration);
        return builder.Build();
    }

    // The account exists up front, as after a first sign-in, so the outcome depends only on the request headers and
    // not on the random password the handler generates when it provisions a new user.
    private static async Task CreateAccountAsync(WebApplication app, string userName)
    {
        using var scope = app.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(
            new ApplicationUser { UserName = userName, Email = $"{userName}@old.example.test", FullName = "Old Name" },
            "Passw0rdOk");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// Authenticates a request from the reverse proxy carrying <paramref name="headers"/> with the Authelia scheme,
    /// then evaluates <c>CanAdminister</c> for the signed-in user.
    /// </summary>
    private static async Task<(ClaimsPrincipal User, bool CanAdminister)> SignInAsync(WebApplication app, (string Name, string Value)[] headers)
    {
        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(ProxyAddress);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var result = await context.AuthenticateAsync("Authelia");
        Assert.True(result.Succeeded, result.Failure?.Message ?? "The Authelia scheme did not authenticate the request");
        var authorization = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(result.Principal!, AuthorizationPolicies.CanAdminister);
        return (result.Principal!, authorization.Succeeded);
    }

    private static (string Name, string Value)[] AutheliaHeaders(string user, string groups) =>
    [
        ("Remote-User", user), ("Remote-Email", $"{user}@example.test"), ("Remote-Name", user), ("Remote-Groups", groups),
    ];

    private static AutheliaSettings GetAutheliaOptions(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        AddComicMaintainerSettings(services, configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AutheliaSettings>>().Value;
    }

    private static Func<IServiceCollection, IConfiguration, IServiceCollection> StartupMethod(string type, string method) =>
        typeof(Program).Assembly
            .GetType("ComicMaintainer.WebApi.Startup." + type, throwOnError: true)!
            .GetMethod(method, BindingFlags.Static | BindingFlags.Public)!
            .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();
}
