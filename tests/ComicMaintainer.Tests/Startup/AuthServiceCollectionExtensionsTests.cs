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
/// Tests for the Identity/JWT/Authelia registrations made by <c>AuthServiceCollectionExtensions</c>, including which
/// secret signs and validates the JWTs (<c>JwtSecretResolver</c>). The method also reads JWT_SECRET, JWT_SECRET_FILE
/// and the AUTHELIA_* variables (AUTHELIA_ENABLED, AUTHELIA_TRUSTED_PROXIES and AUTHELIA_ADMIN_GROUPS affect its
/// registrations) from the process environment, so the tests run in the non-parallel environment collection with
/// those variables cleared. A key generated for want of a secret is saved in a temporary config directory.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class AuthServiceCollectionExtensionsTests : IDisposable
{
    private const string ConfiguredSecret = "configured-secret-configured-secret-0123456789";
    private const string EnvironmentSecret = "environment-secret-environment-secret-0123456789";
    private const string FileSecret = "file-secret-file-secret-file-secret-0123456789";

    // Published with ComicMaintainer, so anyone can sign tokens with them (#841): the "Secret" that appsettings.json
    // shipped with, and the fallback of docker-compose.dotnet.yml and .env.example.
    private const string PublishedAppSettingsSecret = "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!";
    private const string PublishedComposeSecret = "ChangeThisToASecureRandomString-MustBeAtLeast32CharactersLong!!";

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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-auth-extensions-" + Guid.NewGuid().ToString("N"));

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("JWT_SECRET", "JWT_SECRET_FILE", "AUTHELIA_ENABLED", "AUTHELIA_TRUSTED_PROXIES", "AUTHELIA_ADMIN_GROUPS");

    // Created by the app when it generates a key.
    private string ConfigDirectory => Path.Combine(_root, "config");

    private string KeyFile => Path.Combine(ConfigDirectory, "jwt-secret.key");

    public void Dispose()
    {
        _environment.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // Out of the box no secret is configured. Rather than refuse to start, or fall back to a published secret, the
    // app generates a random key and keeps it in the config directory, next to the database.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutASecret_SignsAndValidatesWithAKeyGeneratedInTheConfigDirectory(string? configuredSecret)
    {
        using var provider = BuildProvider(("JwtSettings:Secret", configuredSecret), ("JwtSettings:Issuer", "issuer"));

        var key = File.ReadAllText(KeyFile);
        Assert.Equal(48, Convert.FromBase64String(key).Length);
        AssertSignsAndValidatesWith(provider, key);
    }

    [Fact]
    public void WithoutASecret_KeepsTheKeyGeneratedOnTheFirstStart()
    {
        string firstKey;
        using (var firstStart = BuildProvider())
        {
            firstKey = GetValidationSecret(firstStart);
        }

        using var secondStart = BuildProvider();

        AssertSignsAndValidatesWith(secondStart, firstKey);
        Assert.Equal(firstKey, File.ReadAllText(KeyFile));
    }

    [Theory]
    [InlineData(FileSecret + "\n", FileSecret)]
    [InlineData("\n", null)]
    [InlineData("", null)]
    public void WithoutASecret_UsesTheKeyFileUnlessItIsEmpty(string contents, string? expectedKey)
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(KeyFile, contents);

        using var provider = BuildProvider();

        var key = File.ReadAllText(KeyFile);
        if (expectedKey is null)
        {
            Assert.Equal(48, Convert.FromBase64String(key).Length);
            expectedKey = key;
        }

        AssertSignsAndValidatesWith(provider, expectedKey);
    }

    [Fact]
    public void WithoutASecret_SavesTheKeyForTheAppUserOnly()
    {
        using var provider = BuildProvider();

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        }
    }

    // Instances that share a config directory may start together; they must all use the key that was saved.
    [Fact]
    public async Task WithoutASecret_ConcurrentStartsAgreeOnOneKey()
    {
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var provider = BuildProvider();
            return GetValidationSecret(provider);
        })));

        Assert.Equal(File.ReadAllText(KeyFile), Assert.Single(keys.Distinct()));
        Assert.Equal([KeyFile], Directory.GetFiles(ConfigDirectory));
    }

    [Fact]
    public void WithoutASecret_RefusesToStartWhenTheKeyCannotBeSaved()
    {
        // The config directory cannot be created: a file is in the way.
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var configDirectory = Path.Combine(blocker, "config");

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(("AppSettings:ConfigDirectory", configDirectory)));

        Assert.StartsWith(
            $"No JWT secret is configured, and the signing key generated in its place cannot be saved to or read from '{Path.Combine(configDirectory, "jwt-secret.key")}'",
            exception.Message);
        Assert.Contains("set JWT_SECRET (or JWT_SECRET_FILE) to a random secret", exception.Message);
    }

    // Wherever a published secret is set, it counts as not set.
    [Theory]
    [InlineData("JWT_SECRET", PublishedComposeSecret)]
    [InlineData("JWT_SECRET", PublishedAppSettingsSecret)]
    [InlineData("JWT_SECRET", " " + PublishedComposeSecret + " ")]
    [InlineData("JWT_SECRET_FILE", PublishedComposeSecret)]
    [InlineData("JwtSettings:Secret", PublishedAppSettingsSecret)]
    [InlineData("JwtSettings:Secret", PublishedComposeSecret)]
    public void PublishedSecrets_AreNeverUsed(string setIn, string publishedSecret)
    {
        (string, string?)[] settings = [];
        switch (setIn)
        {
            case "JwtSettings:Secret":
                settings = [(setIn, publishedSecret)];
                break;
            case "JWT_SECRET_FILE":
                _environment.Set(setIn, WriteSecretFile(publishedSecret + "\n"));
                break;
            default:
                _environment.Set(setIn, publishedSecret);
                break;
        }

        using var provider = BuildProvider(settings);

        var key = File.ReadAllText(KeyFile);
        Assert.Equal(48, Convert.FromBase64String(key).Length);
        AssertSignsAndValidatesWith(provider, key);
    }

    [Fact]
    public void APublishedJwtSecret_GivesWayToTheConfiguredSecret()
    {
        _environment.Set("JWT_SECRET", PublishedComposeSecret);

        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret));

        AssertSignsAndValidatesWith(provider, ConfiguredSecret);
        Assert.False(File.Exists(KeyFile));
    }

    // A blank variable (`JWT_SECRET=` in an env file, or docker-compose.dotnet.yml with JWT_SECRET unset) counts as not
    // set, so it neither replaces the configured secret nor clashes with the other variable.
    [Theory]
    [InlineData("JWT_SECRET")]
    [InlineData("JWT_SECRET_FILE")]
    public void BlankJwtSecretVariables_CountAsNotSet(string variable)
    {
        _environment.Set(variable, "  ");

        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret));

        AssertSignsAndValidatesWith(provider, ConfiguredSecret);
    }

    [Fact]
    public void JwtSecretFile_WorksNextToABlankJwtSecret()
    {
        _environment.Set("JWT_SECRET", " ");
        _environment.Set("JWT_SECRET_FILE", WriteSecretFile(FileSecret));

        using var provider = BuildProvider();

        AssertSignsAndValidatesWith(provider, FileSecret);
    }

    // The Docker-secrets setup of docs/DOCKER_DEPLOYMENT_DOTNET.md. Files made with `echo` or `openssl rand` end with
    // a line break that is not part of the secret.
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void JwtSecretFile_SignsAndValidatesWithTheSecretInTheFile(string lineBreak)
    {
        _environment.Set("JWT_SECRET_FILE", WriteSecretFile(FileSecret + lineBreak));

        using var provider = BuildProvider(("JwtSettings:Secret", ConfiguredSecret));

        AssertSignsAndValidatesWith(provider, FileSecret);
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void JwtSecretAndJwtSecretFile_CannotBothBeSet()
    {
        _environment.Set("JWT_SECRET", EnvironmentSecret);
        _environment.Set("JWT_SECRET_FILE", WriteSecretFile(FileSecret));

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider());

        Assert.StartsWith("Both JWT_SECRET and JWT_SECRET_FILE are set. Set only one of them", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JwtSecretFile_ThatCannotBeRead_StopsTheStartup(bool isADirectory)
    {
        var path = Path.Combine(_root, "secrets", "jwt_secret");
        if (isADirectory)
        {
            Directory.CreateDirectory(path);
        }

        _environment.Set("JWT_SECRET_FILE", path);

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(("JwtSettings:Secret", ConfiguredSecret)));

        Assert.StartsWith($"JWT_SECRET_FILE is set to '{path}', which cannot be read: ", exception.Message);
        Assert.False(File.Exists(KeyFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("  \r\n")]
    public void JwtSecretFile_ThatIsEmpty_StopsTheStartup(string contents)
    {
        var path = WriteSecretFile(contents);
        _environment.Set("JWT_SECRET_FILE", path);

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(("JwtSettings:Secret", ConfiguredSecret)));

        Assert.Equal($"JWT_SECRET_FILE is set to '{path}', which is empty.", exception.Message);
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

        AssertSignsAndValidatesWith(provider, EnvironmentSecret);
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

    private static string GetValidationSecret(ServiceProvider provider)
    {
        var key = Assert.IsType<SymmetricSecurityKey>(GetBearerOptions(provider).TokenValidationParameters.IssuerSigningKey);
        return Encoding.UTF8.GetString(key.Key);
    }

    // Tokens are validated with the JwtBearer key, and signed by AuthService with IOptions<JwtSettings>.Secret.
    private static void AssertSignsAndValidatesWith(ServiceProvider provider, string secret)
    {
        Assert.Equal(secret, GetValidationSecret(provider));
        Assert.Equal(secret, provider.GetRequiredService<IOptions<JwtSettings>>().Value.Secret);
    }

    private string WriteSecretFile(string contents)
    {
        var path = Path.Combine(_root, "secrets", "jwt_secret");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    // Unless a test names another, the config directory (where a generated key is saved) is a temporary one.
    private IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings)
    {
        var values = settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)).ToList();
        if (!values.Exists(v => v.Key == "AppSettings:ConfigDirectory"))
        {
            values.Add(new("AppSettings:ConfigDirectory", ConfigDirectory));
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private ServiceProvider BuildProvider(params (string Key, string? Value)[] settings)
    {
        var configuration = BuildConfiguration(settings);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        // As in Program: the settings (which bind the JwtSettings section) are registered before the auth services.
        AddComicMaintainerSettings(services, configuration);
        AddComicMaintainerAuth(services, configuration);
        return services.BuildServiceProvider();
    }
}
