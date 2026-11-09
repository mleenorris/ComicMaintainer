using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComicMaintainer.Tests.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The settings API through the real app: a save is visible to the very next read, every failure
/// is a problem document with an <c>error</c> string, and the POST aliases are gone.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class SettingsEndpointTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "settings-endpoint-tests", Guid.NewGuid().ToString("N"));
    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new(ComicMaintainer.Core.Configuration.AppSettingsEnvironmentOverrides.All.Select(o => o.Variable).ToArray());

    public void Dispose()
    {
        _environment.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{_root}' due to IO error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Failed to delete test directory '{_root}' due to access error: {ex.Message}");
        }
    }

    [Fact]
    public async Task PutSettings_IsVisibleToTheNextRead()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings", new
        {
            // The body the Settings page sends, group names and field names included.
            filenameFormat = "{series} saved {issue}",
            logMaxMB = 7.5,
            issueNumberPadding = 6,
            databaseCleanup = new { intervalHours = 5, staleCleanupSentinelFile = ".mounted" },
            externalSeriesMetadata = new { enableMangaDex = false, comicVineApiKey = "cv-key" },
            seriesImageLimits = new { maxMB = 2.5, maxDownloadMB = 3, maxDimension = 900 },
            email = new { smtpHost = "mail.example", smtpPort = 2525, smtpPassword = "pw", emailFromName = "" },
            emailRateLimit = new { maxSendsPerHour = 7 },
            suwayomi = new { enabled = true, baseUrl = "http://suwayomi.example:4567" },
            errorReporting = new { maxIssuesPerDay = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // No waiting: the read below would see the old values if the save relied on the file watcher.
        using var read = await client.GetAsync("/api/settings");
        using var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var body = settings.RootElement;
        Assert.Equal("{series} saved {issue}", body.GetProperty("filename_format").GetString());
        Assert.Equal(7864320, body.GetProperty("log_max_bytes").GetInt32());
        Assert.Equal(6, body.GetProperty("issue_number_padding").GetInt32());
        Assert.Equal(5, body.GetProperty("database_cleanup_interval_hours").GetInt32());
        Assert.Equal(".mounted", body.GetProperty("stale_cleanup_sentinel_file").GetString());
        Assert.False(body.GetProperty("enable_mangadex_metadata").GetBoolean());
        Assert.True(body.GetProperty("comicvine_api_key_set").GetBoolean());
        Assert.Equal(2621440, body.GetProperty("series_image_max_bytes").GetInt32());
        Assert.Equal(900, body.GetProperty("series_image_max_dimension").GetInt32());
        Assert.Equal("mail.example", body.GetProperty("smtp_host").GetString());
        Assert.Equal(2525, body.GetProperty("smtp_port").GetInt32());
        Assert.True(body.GetProperty("smtp_password_set").GetBoolean());
        Assert.Equal("ComicMaintainer", body.GetProperty("email_from_name").GetString());
        Assert.Equal(7, body.GetProperty("email_max_sends_per_hour").GetInt32());
        Assert.True(body.GetProperty("enable_suwayomi").GetBoolean());
        Assert.Equal("http://suwayomi.example:4567", body.GetProperty("suwayomi_base_url").GetString());
        Assert.Equal(1, body.GetProperty("error_reporting_max_issues_per_day").GetInt32());
        Assert.Empty(body.GetProperty("env_locked").EnumerateArray());
    }

    [Fact]
    public async Task PutSettings_NotificationTargets_AreSavedAndTheirSecretsNeverReturned()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings", new
        {
            notifications = new
            {
                ntfy = new { enabled = true, url = "https://ntfy.example/", topic = "comics", token = "tk_secret", events = "job-failed" },
                discord = new { enabled = true, url = "https://discord.com/api/webhooks/1/hook-secret" },
                kavita = new { url = "http://kavita.example", token = "kavita-secret", libraryId = "7" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var read = await client.GetAsync("/api/settings");
        var text = await read.Content.ReadAsStringAsync();
        using var settings = JsonDocument.Parse(text);
        var body = settings.RootElement;
        Assert.True(body.GetProperty("notify_ntfy_enabled").GetBoolean());
        Assert.Equal("https://ntfy.example", body.GetProperty("notify_ntfy_url").GetString());
        Assert.Equal("comics", body.GetProperty("notify_ntfy_topic").GetString());
        Assert.True(body.GetProperty("notify_ntfy_token_set").GetBoolean());
        Assert.Equal("job-failed", body.GetProperty("notify_ntfy_events").GetString());
        Assert.True(body.GetProperty("notify_discord_webhook_url_set").GetBoolean());
        Assert.Equal("7", body.GetProperty("notify_kavita_library_id").GetString());
        Assert.DoesNotContain("tk_secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hook-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kavita-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutSettings_TheOpdsProviderFillAndEveryNotificationField_ReachTheNextRead()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings", new
        {
            opds = new { enabled = true },
            externalSeriesMetadata = new { fillComicInfoFromProviders = false },
            notifications = new
            {
                ntfy = new { enabled = true, url = "https://ntfy.example", topic = "comics", token = "ntfy-token", events = "job-failed" },
                gotify = new { enabled = true, url = "https://gotify.example", token = "gotify-token", events = "new-chapters" },
                discord = new { enabled = true, url = "https://discord.com/api/webhooks/1/discord-secret", events = "job-findings" },
                webhook = new { enabled = true, url = "https://hooks.example/webhook-secret", events = "suwayomi-import-failed" },
                komga = new { enabled = true, url = "http://komga.example", token = "komga-key", libraryId = "lib-1", events = "library-changed" },
                kavita = new { enabled = true, url = "http://kavita.example", token = "kavita-key", libraryId = "7", events = "new-chapters" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var read = await client.GetAsync("/api/settings");
        using var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var body = settings.RootElement;
        Assert.True(body.GetProperty("opds_enabled").GetBoolean());
        Assert.False(body.GetProperty("fill_comicinfo_from_providers").GetBoolean());

        foreach (var flag in new[]
        {
            "notify_ntfy_enabled", "notify_ntfy_token_set", "notify_gotify_enabled", "notify_gotify_token_set",
            "notify_discord_enabled", "notify_discord_webhook_url_set", "notify_webhook_enabled", "notify_webhook_url_set",
            "notify_komga_enabled", "notify_komga_api_key_set", "notify_kavita_enabled", "notify_kavita_api_key_set",
        })
        {
            Assert.True(body.GetProperty(flag).GetBoolean(), flag);
        }

        foreach (var (field, value) in new[]
        {
            ("notify_ntfy_url", "https://ntfy.example"), ("notify_ntfy_topic", "comics"), ("notify_ntfy_events", "job-failed"),
            ("notify_gotify_url", "https://gotify.example"), ("notify_gotify_events", "new-chapters"),
            ("notify_discord_events", "job-findings"), ("notify_webhook_events", "suwayomi-import-failed"),
            ("notify_komga_url", "http://komga.example"), ("notify_komga_library_id", "lib-1"), ("notify_komga_events", "library-changed"),
            ("notify_kavita_url", "http://kavita.example"), ("notify_kavita_library_id", "7"), ("notify_kavita_events", "new-chapters"),
        })
        {
            Assert.Equal(value, body.GetProperty(field).GetString());
        }
    }

    [Fact]
    public async Task PutSettings_ANotificationUrlThatIsNotHttp_IsRefused()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings", new { notifications = new { webhook = new { url = "ftp://example.com/hook" } } });

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        using var problem = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        Assert.Contains("http(s)", problem.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutSettings_ARefusedSave_IsAProblemDocumentWithAnErrorAndChangesNothing()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings", new { filenameFormat = "{series} never saved", issueNumberPadding = 11 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("between 0 and", problem.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        using var read = await client.GetAsync("/api/settings");
        using var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.NotEqual("{series} never saved", settings.RootElement.GetProperty("filename_format").GetString());
    }

    [Fact]
    public async Task APinnedSetting_IsListedAndAChangeIsRefusedWithAConflict()
    {
        _environment.Set("SMTP_HOST", "pinned.example");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var read = await client.GetAsync("/api/settings");
        using var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var locked = Assert.Single(settings.RootElement.GetProperty("env_locked").EnumerateArray());
        Assert.Equal("smtp_host", locked.GetProperty("field").GetString());
        Assert.Equal("SMTP_HOST", locked.GetProperty("variable").GetString());
        Assert.Equal("pinned.example", settings.RootElement.GetProperty("smtp_host").GetString());

        using var save = await client.PutAsJsonAsync("/api/settings", new { email = new { smtpHost = "other.example" } });

        Assert.Equal(HttpStatusCode.Conflict, save.StatusCode);
        Assert.Equal("application/problem+json", save.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        Assert.Contains("SMTP_HOST", problem.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("smtp_host", problem.RootElement.GetProperty("env_locked")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task AFieldLeftOutOfAGroupRequest_IsNotRefusedForTheSettingAVariablePins()
    {
        _environment.Set("SUWAYOMI_BASE_URL", "http://suwayomi.example:4567");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings/suwayomi", new { enabled = false });

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var read = await client.GetAsync("/api/settings");
        using var settings = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.False(settings.RootElement.GetProperty("enable_suwayomi").GetBoolean());
        Assert.Equal("http://suwayomi.example:4567", settings.RootElement.GetProperty("suwayomi_base_url").GetString());
    }

    [Fact]
    public async Task TheLegacyPostAliases_AreGone()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var setting in new[]
        {
            "filename-format", "issue-number-padding", "log-max-bytes", "watcher-enable-rename",
            "watcher-enable-normalize", "convert-rar-to-cbz-on-write", "write-cover-to-first-archive",
            "write-cover-to-series-folder", "download-external-series-images", "series-image-limits",
            "database-cleanup-interval-hours", "default-library-view", "allow-registration", "external-series-metadata",
        })
        {
            using var response = await client.PostAsJsonAsync($"/api/settings/{setting}", new { });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = Path.Join(_root, "watched"),
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = Path.Join(_root, "config"),
            ["CONFIG_DIR"] = Path.Join(_root, "config"),
        };
        foreach (var directory in overrides.Values)
        {
            Directory.CreateDirectory(directory!);
        }

        return new ConfiguredWebApplicationFactory(overrides);
    }

    private sealed class ConfiguredWebApplicationFactory(IReadOnlyDictionary<string, string?> overrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>();
                services.AddSingleton(ComicMaintainer.Tests.Helpers.TestAuthorization.Administrator);
            });
        }
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
