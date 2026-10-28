using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Tests.Startup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Boots the real <c>Program</c>, with the shipped appsettings.json, to check which secret signs and validates the
/// app's JWTs (#841): the secret in <c>JWT_SECRET_FILE</c>, as the Docker-secrets deployment docs describe, or else a
/// random key generated in the config directory, but never a secret published with ComicMaintainer. JWT_SECRET,
/// JWT_SECRET_FILE and the ADMIN_* variables are read from the process environment, so the tests run in the
/// non-parallel environment collection with those variables cleared.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class JwtSigningSecretIntegrationTests : IAsyncLifetime
{
    private const string AdminUsername = "secrets-admin";
    private const string AdminPassword = "Passw0rdOk";
    private const string FileSecret = "docker-secret-docker-secret-docker-secret-0123456789";

    private const string GeneratedKeyTemplate = "Generated a random JWT signing key and saved it to {KeyFile}";
    private const string SavedKeyTemplate = "Using the JWT signing key saved in {KeyFile}";
    private const string SecretSourceTemplate = "Using the JWT secret from {JwtSecretSource}";
    private const string IgnoredPlaceholderTemplate =
        "Ignoring the JWT secret set in {IgnoredSources}: it is a placeholder published with ComicMaintainer, so " +
        "anyone could use it to sign in as any user. Remove it, or replace it with a random secret of your own, " +
        "such as the output of `openssl rand -base64 48`.";

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "JwtSecret-" + Guid.NewGuid().ToString("N"));
    private readonly ProgramTests.CapturingLoggerProvider _logs = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new("JWT_SECRET", "JWT_SECRET_FILE", "CONFIG_DIR", "ADMIN_USERNAME", "ADMIN_PASSWORD", "ADMIN_PASSWORD_FILE", "ADMIN_EMAIL");

    // The "Secret" that appsettings.json shipped with, and the fallback of docker-compose.dotnet.yml and .env.example.
    public static TheoryData<string, bool> PublishedSecrets => new()
    {
        { "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!", false },
        { "YourSecretKeyHere-ChangeInProduction-MustBeAtLeast32CharactersLong!", true },
        { "ChangeThisToASecureRandomString-MustBeAtLeast32CharactersLong!!", false },
        { "ChangeThisToASecureRandomString-MustBeAtLeast32CharactersLong!!", true },
    };

    private string ConfigDirectory => Path.Join(_root, "config");

    private string KeyFile => Path.Join(ConfigDirectory, "jwt-secret.key");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Join(_root, "watched"));
        Directory.CreateDirectory(Path.Join(_root, "duplicates"));
        Directory.CreateDirectory(Path.Join(_root, "secrets"));
        // CONFIG_DIR, where the generated key is kept, otherwise points at the directory shared by the whole test run.
        _environment.Set("CONFIG_DIR", ConfigDirectory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        _environment.Dispose();
        SqliteConnection.ClearAllPools();
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
    [MemberData(nameof(PublishedSecrets))]
    public async Task TokensSignedWithAPublishedSecret_AreRejected(string publishedSecret, bool setAsJwtSecret)
    {
        if (setAsJwtSecret)
        {
            _environment.Set("JWT_SECRET", publishedSecret);
        }

        var client = StartApp();
        var forgedAdminToken = ForgeAdminToken(publishedSecret);

        Assert.Equal(HttpStatusCode.Unauthorized, await ListBackupsAsync(client, forgedAdminToken));
        Assert.False(await IsAuthenticatedAsync(client, forgedAdminToken));
        Assert.NotEqual(publishedSecret, File.ReadAllText(KeyFile));
        var ignored = _logs.ProgramEntries(IgnoredPlaceholderTemplate).ToList();
        if (setAsJwtSecret)
        {
            var warning = Assert.Single(ignored);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal("JWT_SECRET", warning.Values["IgnoredSources"]);
        }
        else
        {
            Assert.Empty(ignored);
        }
    }

    [Fact]
    public async Task WithoutASecret_SignsWithAKeyGeneratedOnTheFirstStartAndKeptForTheNext()
    {
        _environment.Set("ADMIN_USERNAME", AdminUsername);
        _environment.Set("ADMIN_PASSWORD", AdminPassword);

        var firstStart = StartApp();
        var token = await SignInAsync(firstStart);

        var generated = Assert.Single(_logs.ProgramEntries(GeneratedKeyTemplate));
        Assert.Equal(LogLevel.Information, generated.Level);
        Assert.Equal(KeyFile, generated.Values["KeyFile"]);
        var key = File.ReadAllText(KeyFile);
        Assert.Equal(48, Convert.FromBase64String(key).Length);
        AssertSignedWith(token, key);
        Assert.Equal(HttpStatusCode.OK, await ListBackupsAsync(firstStart, token));

        // After a restart the saved key is used again, so the user stays signed in.
        await DisposeAppsAsync();
        var secondStart = StartApp();

        Assert.Equal(HttpStatusCode.OK, await ListBackupsAsync(secondStart, token));
        var saved = Assert.Single(_logs.ProgramEntries(SavedKeyTemplate));
        Assert.Equal(KeyFile, saved.Values["KeyFile"]);
        Assert.Single(_logs.ProgramEntries(GeneratedKeyTemplate));
        Assert.Equal(key, File.ReadAllText(KeyFile));
    }

    // The "Use Docker Secrets" setup of docs/DOCKER_DEPLOYMENT_DOTNET.md.
    [Fact]
    public async Task DockerSecrets_SignWithTheJwtSecretFileAndSeedTheAdminFromTheAdminPasswordFile()
    {
        var jwtSecretFile = Path.Join(_root, "secrets", "jwt_secret");
        var adminPasswordFile = Path.Join(_root, "secrets", "admin_password");
        // Secrets created with `echo …` or `openssl rand …` end with a line break.
        await File.WriteAllTextAsync(jwtSecretFile, FileSecret + "\n");
        await File.WriteAllTextAsync(adminPasswordFile, AdminPassword + "\n");
        _environment.Set("JWT_SECRET_FILE", jwtSecretFile);
        _environment.Set("ADMIN_USERNAME", AdminUsername);
        _environment.Set("ADMIN_PASSWORD_FILE", adminPasswordFile);

        var client = StartApp();
        var token = await SignInAsync(client);

        AssertSignedWith(token, FileSecret);
        Assert.Equal(HttpStatusCode.OK, await ListBackupsAsync(client, token));
        Assert.Equal(HttpStatusCode.OK, await ListBackupsAsync(client, ForgeAdminToken(FileSecret)));
        var source = Assert.Single(_logs.ProgramEntries(SecretSourceTemplate));
        Assert.Equal($"JWT_SECRET_FILE ({jwtSecretFile})", source.Values["JwtSecretSource"]);
        Assert.False(File.Exists(KeyFile));
    }

    private HttpClient StartApp()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = Path.Join(_root, "watched"),
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = ConfigDirectory,
        };

        var factory = new WebApplicationFactory<Program>();
        _factories.Add(factory);
        return factory.WithWebHostBuilder(builder =>
        {
            // The shipped appsettings.json, as in the container.
            builder.UseEnvironment("Production");
            // UseSetting values are visible while Program registers its services, which is when the JWT secret is
            // resolved (and the generated key saved in the config directory). The in-memory source is added last, at
            // Build, so it also wins over the machine's user-settings.json.
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(
                _ => new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information })));
        }).CreateClient();
    }

    private async Task DisposeAppsAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        _factories.Clear();
    }

    private static async Task<string> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUsername, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    // The backups API is for administrators only.
    private static async Task<HttpStatusCode> ListBackupsAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/backups");
        request.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<bool> IsAuthenticatedAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        request.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, token);
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("isAuthenticated").GetBoolean();
    }

    // What anyone who knows the secret can sign: an administrator's token, as AuthService issues them.
    private static string ForgeAdminToken(string secret)
    {
        var token = new JwtSecurityToken(
            issuer: "ComicMaintainer",
            audience: "ComicMaintainerAPI",
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "intruder"),
                new Claim(ClaimTypes.Role, "Admin"),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static void AssertSignedWith(string token, string secret)
    {
        // Throws unless the token's signature was made with the secret.
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = "ComicMaintainer",
            ValidAudience = "ComicMaintainerAPI",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        }, out _);
    }
}
