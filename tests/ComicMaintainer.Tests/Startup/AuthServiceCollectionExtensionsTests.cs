using System.Reflection;
using System.Security.Claims;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the Identity/JWT/Authelia registrations made by <c>AuthServiceCollectionExtensions</c>.
/// The method also reads JWT_SECRET and the AUTHELIA_* variables (AUTHELIA_ENABLED, AUTHELIA_TRUSTED_PROXIES and
/// AUTHELIA_ADMIN_GROUPS affect its registrations) from the process environment, so the tests run in the
/// non-parallel environment collection with those variables cleared.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class AuthServiceCollectionExtensionsTests : IDisposable
{
    private const string ConfiguredSecret = "configured-secret-configured-secret-0123456789";
    private const string EnvironmentSecret = "environment-secret-environment-secret-0123456789";

    // Authelia refuses to start without an allowlist of reverse proxies (see AutheliaTrustedProxyStartupTests).
    private const string TrustedProxies = "172.18.0.0/16";

    // The extension classes are internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerAuth = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.AuthServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerAuth", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private static readonly Func<IServiceCollection, IConfiguration, IServiceCollection> AddComicMaintainerSettings = typeof(Program).Assembly
        .GetType("ComicMaintainer.WebApi.Startup.CoreServiceCollectionExtensions", throwOnError: true)!
        .GetMethod("AddComicMaintainerSettings", BindingFlags.Static | BindingFlags.Public)!
        .CreateDelegate<Func<IServiceCollection, IConfiguration, IServiceCollection>>();

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("JWT_SECRET", "AUTHELIA_ENABLED", "AUTHELIA_TRUSTED_PROXIES", "AUTHELIA_ADMIN_GROUPS");

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void WithoutAnyJwtConfiguration_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AddComicMaintainerAuth(new ServiceCollection(), BuildConfiguration()));

        Assert.Contains("JWT_SECRET must be configured", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void WithAJwtSectionButNoSecret_Throws(string? secret)
    {
        var configuration = BuildConfiguration(("JwtSettings:Secret", secret), ("JwtSettings:Issuer", "issuer"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddComicMaintainerAuth(new ServiceCollection(), configuration));

        Assert.Contains("JWT_SECRET must be configured", exception.Message);
    }

    [Fact]
    public void ConfiguresJwtBearerValidationFromTheJwtSettingsSection()
    {
        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret),
            ("JwtSettings:Issuer", "test-issuer"),
            ("JwtSettings:Audience", "test-audience"));

        var parameters = GetBearerOptions(provider).TokenValidationParameters;

        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal("test-issuer", parameters.ValidIssuer);
        Assert.Equal("test-audience", parameters.ValidAudience);
        Assert.Equal(Encoding.UTF8.GetBytes(ConfiguredSecret), Assert.IsType<SymmetricSecurityKey>(parameters.IssuerSigningKey).Key);
    }

    [Fact]
    public void JwtSecretEnvironmentVariable_TakesPrecedenceOverTheConfiguredSecret()
    {
        _environment.Set("JWT_SECRET", EnvironmentSecret);

        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret));

        var key = Assert.IsType<SymmetricSecurityKey>(GetBearerOptions(provider).TokenValidationParameters.IssuerSigningKey);
        Assert.Equal(Encoding.UTF8.GetBytes(EnvironmentSecret), key.Key);
    }

    [Fact]
    public async Task JwtSecretEnvironmentVariable_IsEnoughWithoutAnyConfigurationSections()
    {
        _environment.Set("JWT_SECRET", EnvironmentSecret);

        using var provider = BuildProvider();

        var parameters = GetBearerOptions(provider).TokenValidationParameters;
        Assert.Equal(Encoding.UTF8.GetBytes(EnvironmentSecret), Assert.IsType<SymmetricSecurityKey>(parameters.IssuerSigningKey).Key);
        // JwtSettings and AutheliaSettings fall back to their defaults.
        Assert.Equal("ComicMaintainer", parameters.ValidIssuer);
        Assert.Equal("ComicMaintainerAPI", parameters.ValidAudience);
        await AssertJwtOnlyAsync(provider);
    }

    // AuthService signs tokens with IOptions<JwtSettings> and the bearer scheme validates them with the key read at
    // startup, so both must resolve JWT_SECRET the same way. Regression test for #875: a blank JWT_SECRET (as passed by
    // `docker run -e JWT_SECRET=` or an empty env-file entry) counts as unset in both, so the configured secret is used.
    // Environment.SetEnvironmentVariable removes a variable set to "", so whitespace stands in for the empty value.
    [Theory]
    [InlineData(null, ConfiguredSecret)]
    [InlineData(" ", ConfiguredSecret)]
    [InlineData(" \t ", ConfiguredSecret)]
    [InlineData(EnvironmentSecret, EnvironmentSecret)]
    public void JwtSecretEnvironmentVariable_ResolvesTheSameSecretForSigningAndValidation(string? variable, string expectedSecret)
    {
        _environment.Set("JWT_SECRET", variable);
        var configuration = BuildConfiguration(("JwtSettings:Secret", ConfiguredSecret));
        var services = new ServiceCollection();
        services.AddLogging();
        AddComicMaintainerSettings(services, configuration);
        AddComicMaintainerAuth(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(expectedSecret, provider.GetRequiredService<IOptions<JwtSettings>>().Value.Secret);
        var key = Assert.IsType<SymmetricSecurityKey>(GetBearerOptions(provider).TokenValidationParameters.IssuerSigningKey);
        Assert.Equal(Encoding.UTF8.GetBytes(expectedSecret), key.Key);
    }

    // Regression test for #875: the error names the blank variable instead of claiming that no secret is configured.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankJwtSecretEnvironmentVariable_WithoutAConfiguredSecret_ThrowsNamingTheBlankVariable(string? configuredSecret)
    {
        _environment.Set("JWT_SECRET", " ");

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddComicMaintainerAuth(new ServiceCollection(), BuildConfiguration(("JwtSettings:Secret", configuredSecret))));

        Assert.StartsWith("JWT_SECRET is set but blank", exception.Message);
    }

    [Fact]
    public async Task WithAutheliaDisabled_AuthenticatesWithJwtBearerOnly()
    {
        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret), ("AutheliaSettings:Enabled", "false"));

        await AssertJwtOnlyAsync(provider);
    }

    [Fact]
    public async Task WithAutheliaEnabled_MakesAutheliaTheDefaultSchemeAndKeepsAcceptingJwt()
    {
        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret), ("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", TrustedProxies));

        await AssertAutheliaAndJwtAsync(provider);
    }

    [Theory]
    [InlineData("TRUE", "false", true)]
    [InlineData("true", null, true)]
    [InlineData("false", "true", false)]
    [InlineData("yes", "true", false)]
    public async Task AutheliaEnabledEnvironmentVariable_OverridesTheConfiguration(string environmentValue, string? configuredValue, bool autheliaExpected)
    {
        _environment.Set("AUTHELIA_ENABLED", environmentValue);

        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret), ("AutheliaSettings:Enabled", configuredValue), ("AutheliaSettings:TrustedProxies", TrustedProxies));

        if (autheliaExpected)
        {
            await AssertAutheliaAndJwtAsync(provider);
        }
        else
        {
            await AssertJwtOnlyAsync(provider);
        }
    }

    [Theory]
    [InlineData(false, null, "Admin", true)]
    [InlineData(false, null, "User", false)]
    [InlineData(true, "admins", "Admin", true)]
    [InlineData(true, "admins", "User", false)]
    // Authelia without admin groups cannot identify administrators, so any non-ReadOnly user is one.
    [InlineData(true, null, "User", true)]
    [InlineData(true, "  ", "User", true)]
    [InlineData(true, null, "ReadOnly", false)]
    public async Task CanAdministerPolicy_RequiresTheAdminRoleUnlessAutheliaHasNoAdminGroups(
        bool autheliaEnabled, string? adminGroups, string role, bool expected)
    {
        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret),
            ("AutheliaSettings:Enabled", autheliaEnabled.ToString()),
            ("AutheliaSettings:AdminGroups", adminGroups),
            ("AutheliaSettings:TrustedProxies", TrustedProxies));

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(CreateUser(role), AuthorizationPolicies.CanAdminister);

        Assert.Equal(expected, result.Succeeded);
    }

    // AUTHELIA_ADMIN_GROUPS is the documented way to configure the admin groups: it must turn the fallback off too.
    [Theory]
    [InlineData("Admin", true)]
    [InlineData("User", false)]
    [InlineData("ReadOnly", false)]
    public async Task CanAdministerPolicy_HonoursAdminGroupsSetOnlyInTheEnvironment(string role, bool expected)
    {
        _environment.Set("AUTHELIA_ADMIN_GROUPS", "admins");

        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret), ("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", TrustedProxies));

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(CreateUser(role), AuthorizationPolicies.CanAdminister);

        Assert.Equal(expected, result.Succeeded);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("User", true)]
    [InlineData(null, true)]
    [InlineData("ReadOnly", false)]
    public async Task CanModifyLibraryPolicy_DeniesOnlyReadOnlyUsers(string? role, bool expected)
    {
        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret));

        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(CreateUser(role), AuthorizationPolicies.CanModifyLibrary);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task Policies_RejectAnonymousUsers()
    {
        using var provider = BuildProvider(
            ("JwtSettings:Secret", ConfiguredSecret), ("AutheliaSettings:Enabled", "true"), ("AutheliaSettings:TrustedProxies", TrustedProxies));
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False((await authorization.AuthorizeAsync(anonymous, AuthorizationPolicies.CanModifyLibrary)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(anonymous, AuthorizationPolicies.CanAdminister)).Succeeded);
    }

    private static async Task AssertJwtOnlyAsync(ServiceProvider provider)
    {
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Null(await schemes.GetSchemeAsync("Authelia"));

        var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        string[] expectedSchemes = [JwtBearerDefaults.AuthenticationScheme];
        Assert.Equal(expectedSchemes, authorization.DefaultPolicy.AuthenticationSchemes);
        Assert.Equal(expectedSchemes, authorization.GetPolicy(AuthorizationPolicies.CanModifyLibrary)!.AuthenticationSchemes);
        Assert.Equal(expectedSchemes, authorization.GetPolicy(AuthorizationPolicies.CanAdminister)!.AuthenticationSchemes);
    }

    private static async Task AssertAutheliaAndJwtAsync(ServiceProvider provider)
    {
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal("Authelia", (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal("Authelia", (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal(typeof(AutheliaAuthenticationHandler), (await schemes.GetSchemeAsync("Authelia"))?.HandlerType);
        Assert.NotNull(await schemes.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme));

        var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        string[] expectedSchemes = ["Authelia", JwtBearerDefaults.AuthenticationScheme];
        Assert.Equal(expectedSchemes, authorization.DefaultPolicy.AuthenticationSchemes);
        Assert.Equal(expectedSchemes, authorization.GetPolicy(AuthorizationPolicies.CanModifyLibrary)!.AuthenticationSchemes);
        Assert.Equal(expectedSchemes, authorization.GetPolicy(AuthorizationPolicies.CanAdminister)!.AuthenticationSchemes);
    }

    private static ClaimsPrincipal CreateUser(string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "reader") };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static JwtBearerOptions GetBearerOptions(ServiceProvider provider)
    {
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
    }

    private static ServiceProvider BuildProvider(params (string Key, string? Value)[] settings)
    {
        var configuration = BuildConfiguration(settings);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        AddComicMaintainerAuth(services, configuration);
        return services.BuildServiceProvider();
    }
}
