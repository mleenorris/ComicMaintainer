using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Startup;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Authentication;

/// <summary>
/// Tests for <c>AUTHELIA_TRUSTED_PROXIES</c>: parsing the allowlist, and the Authelia handler only
/// honouring the forward-auth headers when the direct peer is a trusted reverse proxy.
/// </summary>
/// <remarks>
/// The handler reports each untrusted address at Warning level only once per process, so every test
/// that asserts on that warning uses its own peer address.
/// </remarks>
public sealed class AutheliaTrustedProxyTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManager = new(
        Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

    private readonly Mock<RoleManager<ApplicationRole>> _roleManager = new(
        Mock.Of<IRoleStore<ApplicationRole>>(), null!, null!, null!, null!);

    public AutheliaTrustedProxyTests()
    {
        _userManager
            .Setup(u => u.FindByNameAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) => new ApplicationUser { Id = "id-" + name, UserName = name, Email = name + "@example.test" });
        _userManager
            .Setup(u => u.UpdateAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);
        _userManager
            .Setup(u => u.GetRolesAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(new List<string> { "User" });
        _roleManager
            .Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5/32")]
    [InlineData("172.18.0.0/16", "172.18.0.0/16")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:db8::7", "2001:db8::7/128")]
    [InlineData("fd00::/8", "fd00::/8")]
    // IPv4-mapped IPv6 entries are stored as the IPv4 address or range they stand for.
    [InlineData("::ffff:172.18.0.5", "172.18.0.5/32")]
    [InlineData("::ffff:172.18.0.0/112", "172.18.0.0/16")]
    public void TryParse_AcceptsIpv4AndIpv6AddressesAndRanges(string value, string expected)
    {
        Assert.True(TrustedProxyAllowlist.TryParse(value, out var allowlist, out var error));

        Assert.Null(error);
        Assert.Equal([expected], allowlist.Networks.Select(network => network.ToString()));
    }

    [Fact]
    public void TryParse_SplitsOnCommasAndIgnoresWhitespaceAndEmptyEntries()
    {
        Assert.True(TrustedProxyAllowlist.TryParse(" 172.18.0.0/16 ,10.0.0.5,\tfd00::/8 , ,", out var allowlist, out _));

        Assert.Equal(["172.18.0.0/16", "10.0.0.5/32", "fd00::/8"], allowlist.Networks.Select(network => network.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,")]
    public void TryParse_RejectsAMissingOrEmptyList(string? value)
    {
        Assert.False(TrustedProxyAllowlist.TryParse(value, out var allowlist, out var error));

        Assert.Null(allowlist);
        Assert.StartsWith("AUTHELIA_TRUSTED_PROXIES is required when Authelia is enabled", error);
    }

    [Theory]
    [InlineData("swag")]                  // hostnames are not resolved
    [InlineData("proxy.example.com")]
    [InlineData("172.18.0.1/16")]         // a range must be written with its network address
    [InlineData("fd00::1/8")]
    [InlineData("::ffff:172.18.0.1/112")]
    [InlineData("172.18.0.0/33")]
    [InlineData("172.18.0.0/")]
    [InlineData("172.18.0.0/-1")]
    [InlineData("fd00::/129")]
    [InlineData("10.0.0.256")]
    [InlineData("10.0.0.5 10.0.0.6")]     // entries are separated by commas
    // Legacy IPv4 forms IPAddress would parse as a different address (0.0.0.10, 172.0.0.18, octal 8.0.0.0/8, ...).
    [InlineData("10")]
    [InlineData("172.18")]
    [InlineData("010.0.0.0/8")]
    [InlineData("0x0a.0.0.1")]
    public void TryParse_RejectsAnInvalidEntryAndNamesIt(string entry)
    {
        Assert.False(TrustedProxyAllowlist.TryParse("10.0.0.5, " + entry, out var allowlist, out var error));

        Assert.Null(allowlist);
        Assert.StartsWith($"Invalid AUTHELIA_TRUSTED_PROXIES entry '{entry}'", error);
    }

    [Theory]
    [InlineData("172.18.0.0/16", "172.18.4.2", true)]
    [InlineData("172.18.0.0/16", "172.19.0.1", false)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.6", false)]
    [InlineData("fd00::/8", "fd12:3456::1", true)]
    [InlineData("fd00::/8", "2001:db8::1", false)]
    // A dual-mode socket reports IPv4 peers as IPv4-mapped IPv6 addresses.
    [InlineData("172.18.0.0/16", "::ffff:172.18.4.2", true)]
    [InlineData("172.18.0.0/16", "::ffff:172.19.0.1", false)]
    [InlineData("::ffff:172.18.0.0/112", "172.18.4.2", true)]
    [InlineData("::ffff:10.0.0.5", "::ffff:10.0.0.5", true)]
    // IPv4 and IPv6 ranges never match an address of the other family.
    [InlineData("0.0.0.0/0", "::1", false)]
    [InlineData("::/0", "127.0.0.1", false)]
    public void Contains_MatchesThePeerAgainstEveryEntry(string value, string peer, bool expected)
    {
        Assert.True(TrustedProxyAllowlist.TryParse("2001:db8:ffff::1, " + value, out var allowlist, out _));

        Assert.Equal(expected, allowlist.Contains(IPAddress.Parse(peer)));
    }

    [Fact]
    public void Contains_NeverTrustsAnUnknownPeer()
    {
        Assert.True(TrustedProxyAllowlist.TryParse("0.0.0.0/0, ::/0", out var allowlist, out _));

        Assert.False(allowlist.Contains(null));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3")]
    [InlineData("172.18.0.0/16", "::ffff:172.18.0.9")]   // IPv4 proxy reaching a dual-mode socket
    [InlineData("10.0.0.5, fd00::/8", "fd00::5")]
    public async Task TrustedPeer_IsAuthenticatedFromTheHeaders(string trustedProxies, string peer)
    {
        var logs = new CapturingLoggerFactory();

        var result = await AuthenticateAsync(trustedProxies, IPAddress.Parse(peer), logs, ("Remote-User", "alice"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Principal!.Identity!.Name);
        Assert.Equal("authelia", result.Principal.FindFirst("auth_method")?.Value);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.8", "203.0.113.8")]    // reported as the IPv4 address to add to the list
    [InlineData("2001:db8::9", "2001:db8::9")]
    public async Task UntrustedPeer_HeadersAreIgnoredAndTheAddressIsReported(string peer, string reported)
    {
        var logs = new CapturingLoggerFactory();

        var result = await AuthenticateAsync(
            "10.0.0.0/8, fd00::/8", IPAddress.Parse(peer), logs, ("Remote-User", "admin"), ("Remote-Groups", "admins"));

        Assert.True(result.None);
        Assert.Null(result.Principal);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
        _userManager.Verify(u => u.CreateAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>()), Times.Never);
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains($"from {reported}", warning.Message);
        Assert.Contains("AUTHELIA_TRUSTED_PROXIES", warning.Message);
    }

    [Fact]
    public async Task UntrustedPeer_IsReportedAsAWarningOnlyOnce()
    {
        var first = new CapturingLoggerFactory();
        var again = new CapturingLoggerFactory();
        var viaDualModeSocket = new CapturingLoggerFactory();

        await AuthenticateAsync("10.0.0.0/8", IPAddress.Parse("198.51.100.20"), first, ("Remote-User", "alice"));
        await AuthenticateAsync("10.0.0.0/8", IPAddress.Parse("198.51.100.20"), again, ("Remote-User", "alice"));
        var result = await AuthenticateAsync("10.0.0.0/8", IPAddress.Parse("::ffff:198.51.100.20"), viaDualModeSocket, ("Remote-User", "alice"));

        Assert.True(result.None);
        Assert.Single(first.Entries, entry => entry.Level == LogLevel.Warning);
        foreach (var logs in new[] { again, viaDualModeSocket })
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("from 198.51.100.20"));
        }
    }

    [Fact]
    public async Task UnknownPeer_HeadersAreIgnored()
    {
        var logs = new CapturingLoggerFactory();

        var result = await AuthenticateAsync("0.0.0.0/0, ::/0", peer: null, logs, ("Remote-User", "alice"));

        Assert.True(result.None);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
        // Whether this is the first report of "unknown" in the process (Warning) or not (Debug) depends on test order.
        Assert.Single(logs.Entries, entry => entry.Level is LogLevel.Warning or LogLevel.Debug && entry.Message.Contains("from unknown"));
    }

    [Fact]
    public async Task UntrustedPeer_WithAnEmptyUserHeader_IsIgnoredRatherThanFailed()
    {
        var result = await AuthenticateAsync("10.0.0.0/8", IPAddress.Parse("203.0.113.30"), new CapturingLoggerFactory(), ("Remote-User", " "));

        Assert.True(result.None);
    }

    [Fact]
    public async Task UntrustedPeer_WithoutTheUserHeader_IsNotReported()
    {
        var logs = new CapturingLoggerFactory();

        var result = await AuthenticateAsync(
            "10.0.0.0/8", IPAddress.Parse("203.0.113.40"), logs, ("Remote-Groups", "admins"), ("Remote-Email", "admin@example.test"));

        Assert.True(result.None);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("203.0.113.40"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("swag")]
    public async Task WithoutAValidAllowlist_NoPeerIsTrusted(string? trustedProxies)
    {
        var result = await AuthenticateAsync(trustedProxies, IPAddress.Parse("192.0.2.60"), new CapturingLoggerFactory(), ("Remote-User", "alice"));

        Assert.True(result.None);
        _userManager.Verify(u => u.FindByNameAsync(It.IsAny<string>()), Times.Never);
    }

    private async Task<AuthenticateResult> AuthenticateAsync(
        string? trustedProxies, IPAddress? peer, ILoggerFactory loggerFactory, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var handler = new AutheliaAuthenticationHandler(
            new TestOptionsMonitor<AutheliaAuthenticationOptions>(new AutheliaAuthenticationOptions()),
            loggerFactory,
            UrlEncoder.Default,
            Options.Create(new AutheliaSettings { Enabled = true, TrustedProxies = trustedProxies }),
            _userManager.Object,
            _roleManager.Object,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>());
        await handler.InitializeAsync(new AuthenticationScheme("Authelia", displayName: null, typeof(AutheliaAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }
}

/// <summary>
/// The startup requirement: with Authelia enabled the app refuses to start unless
/// <c>AUTHELIA_TRUSTED_PROXIES</c> is a valid allowlist. The registrations read the process environment,
/// so these tests run in the non-parallel environment collection with the variables cleared.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class AutheliaTrustedProxyStartupTests : IDisposable
{
    private const string Secret = "configured-secret-configured-secret-0123456789";

    // The extension classes are internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerAuth =
        StartupMethod("AuthServiceCollectionExtensions", "AddComicMaintainerAuth");

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerSettings =
        StartupMethod("CoreServiceCollectionExtensions", "AddComicMaintainerSettings");

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("JWT_SECRET", "AUTHELIA_ENABLED", "AUTHELIA_TRUSTED_PROXIES");

    public void Dispose() => _environment.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void AutheliaEnabled_WithoutTrustedProxies_RefusesToStart(string? trustedProxies)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", trustedProxies)));

        Assert.StartsWith("AUTHELIA_TRUSTED_PROXIES is required when Authelia is enabled", exception.Message);
    }

    [Theory]
    [InlineData("swag")]
    [InlineData("172.18.0.1/16")]
    public void AutheliaEnabled_WithAnInvalidEntry_RefusesToStartAndNamesTheEntry(string entry)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", "10.0.0.5, " + entry)));

        Assert.StartsWith($"Invalid AUTHELIA_TRUSTED_PROXIES entry '{entry}'", exception.Message);
    }

    [Fact]
    public void AutheliaEnabledByTheEnvironment_RequiresTrustedProxies()
    {
        _environment.Set("AUTHELIA_ENABLED", "true");

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider());

        Assert.Contains("AUTHELIA_TRUSTED_PROXIES is required", exception.Message);
    }

    [Fact]
    public void TrustedProxiesEnvironmentVariable_IsValidatedInsteadOfTheConfiguration()
    {
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", "172.18.0.0/16, swag");

        var exception = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", "10.0.0.5")));

        Assert.Contains("'swag'", exception.Message);
    }

    [Fact]
    public async Task TrustedProxiesEnvironmentVariable_SatisfiesTheRequirement()
    {
        _environment.Set("AUTHELIA_ENABLED", "true");
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", "172.18.0.0/16, fd00::/8");

        using var provider = BuildProvider(("AutheliaSettings:TrustedProxies", "not-an-address"));

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("Authelia");
        Assert.Equal(typeof(AutheliaAuthenticationHandler), scheme?.HandlerType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("swag")]
    public async Task AutheliaDisabled_IgnoresTheSetting(string? trustedProxies)
    {
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", trustedProxies);

        using var provider = BuildProvider(("AutheliaSettings:Enabled", "false"), ("AutheliaSettings:TrustedProxies", trustedProxies));

        Assert.Null(await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("Authelia"));
    }

    [Fact]
    public void AutheliaSettings_BindTrustedProxiesFromTheConfiguration()
    {
        var settings = BindAutheliaSettings(("AutheliaSettings:TrustedProxies", "10.0.0.0/8"));

        Assert.Equal("10.0.0.0/8", settings.TrustedProxies);
    }

    [Theory]
    [InlineData("172.18.0.0/16, fd00::/8", "172.18.0.0/16, fd00::/8")]
    [InlineData("", "10.0.0.0/8")]   // an empty variable does not override, like the other AUTHELIA_* variables
    public void AutheliaSettings_TrustedProxiesEnvironmentVariableOverridesTheConfiguration(string environmentValue, string expected)
    {
        _environment.Set("AUTHELIA_TRUSTED_PROXIES", environmentValue);

        var settings = BindAutheliaSettings(("AutheliaSettings:TrustedProxies", "10.0.0.0/8"));

        Assert.Equal(expected, settings.TrustedProxies);
    }

    private static Func<IServiceCollection, IConfiguration, IServiceCollection> StartupMethod(string type, string method) =>
        typeof(Program).Assembly
            .GetType("ComicMaintainer.WebApi.Startup." + type, throwOnError: true)!
            .GetMethod(method, BindingFlags.Static | BindingFlags.Public)!
            .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static IConfiguration BuildConfiguration((string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static ServiceProvider BuildProvider(params (string Key, string? Value)[] settings)
    {
        var configuration = BuildConfiguration([("JwtSettings:Secret", Secret), .. settings]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        AddComicMaintainerAuth(services, configuration);
        return services.BuildServiceProvider();
    }

    private static AutheliaSettings BindAutheliaSettings(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        AddComicMaintainerSettings(services, BuildConfiguration(settings));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AutheliaSettings>>().Value;
    }
}

/// <summary>Records every log entry (all levels) of every logger it creates.</summary>
file sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
