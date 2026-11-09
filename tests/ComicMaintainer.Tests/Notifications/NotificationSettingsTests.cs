using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Startup;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Notifications;

/// <summary>
/// Saving the notification targets: what is validated, how secrets are kept or cleared, and what
/// the environment pins. These tests change environment variables, so they run in the
/// non-parallel environment collection.
/// </summary>
[Collection(AppSettingsEnvironmentPostConfigureTests.EnvironmentCollection.Name)]
public sealed class NotificationSettingsTests : IDisposable
{
    private readonly AppSettingsEnvironmentPostConfigureTests.EnvironmentScope _environment =
        new(AppSettingsEnvironmentOverrides.All.Select(o => o.Variable).ToArray());

    private readonly string _configDir = Path.Join(Path.GetTempPath(), $"NotifySettings_{Guid.NewGuid():N}");
    private readonly AppSettings _settings;
    private readonly SettingsService _service;

    public NotificationSettingsTests()
    {
        _settings = new AppSettings { ConfigDirectory = _configDir };
        _service = new SettingsService(
            NullLogger<SettingsService>.Instance,
            new TestOptionsMonitor<AppSettings>(_settings),
            new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        _environment.Dispose();
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    private Task SaveAsync(NotificationsUpdate notifications) =>
        _service.UpdateSettingsAsync(new SettingsUpdate { Notifications = notifications });

    private JsonElement Saved()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(_configDir, "user-settings.json")));
        return document.RootElement.GetProperty("AppSettings").Clone();
    }

    [Fact]
    public async Task Save_StoresATarget_TrimmingTheUrlAndOrderingTheEvents()
    {
        await SaveAsync(new NotificationsUpdate(Ntfy: new NotificationTargetUpdate(
            Enabled: true,
            Url: " https://ntfy.example.com/ ",
            Token: "tk_secret",
            Topic: " comics ",
            Events: "job-failed, NEW-CHAPTERS")));

        var saved = Saved();
        Assert.True(saved.GetProperty("NotifyNtfyEnabled").GetBoolean());
        Assert.Equal("https://ntfy.example.com", saved.GetProperty("NotifyNtfyUrl").GetString());
        Assert.Equal("tk_secret", saved.GetProperty("NotifyNtfyToken").GetString());
        Assert.Equal("comics", saved.GetProperty("NotifyNtfyTopic").GetString());
        Assert.Equal("new-chapters,job-failed", saved.GetProperty("NotifyNtfyEvents").GetString());
    }

    [Fact]
    public async Task Save_WithNoEvents_ClearsTheList()
    {
        await SaveAsync(new NotificationsUpdate(Gotify: new NotificationTargetUpdate(Events: "")));

        Assert.Equal(string.Empty, Saved().GetProperty("NotifyGotifyEvents").GetString());
    }

    [Fact]
    public async Task Save_LeavesOutTheGroupsThatWereNotSent()
    {
        await SaveAsync(new NotificationsUpdate(Discord: new NotificationTargetUpdate(Events: "job-failed")));

        var names = Saved().EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "NotifyDiscordEvents" }, names);
    }

    [Fact]
    public async Task Save_KeepsAStoredSecret_WhenItIsLeftOut_AndClearsItWhenEmpty()
    {
        _settings.NotifyKomgaUrl = "http://komga.lan";
        _settings.NotifyKomgaApiKey = "stored";

        await SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Enabled: true)));
        Assert.False(Saved().TryGetProperty("NotifyKomgaApiKey", out _));

        await SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Enabled: false, Token: "")));
        Assert.Equal(JsonValueKind.Null, Saved().GetProperty("NotifyKomgaApiKey").ValueKind);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@example.com/hook")]
    public async Task Save_RefusesAUrlThatIsNotPlainHttp_WithoutRepeatingASecretOne(string url)
    {
        var plain = await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Ntfy: new NotificationTargetUpdate(Url: url))));
        var secret = await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Discord: new NotificationTargetUpdate(Url: url))));

        Assert.Contains("http(s)", plain.Reason);
        Assert.DoesNotContain(url, secret.Reason);
        Assert.False(File.Exists(Path.Join(_configDir, "user-settings.json")));
    }

    [Fact]
    public async Task Save_AllowsPrivateAddresses()
    {
        await SaveAsync(new NotificationsUpdate(
            Komga: new NotificationTargetUpdate(Url: "http://192.168.1.20:25600"),
            Kavita: new NotificationTargetUpdate(Url: "http://kavita.lan:5000/")));

        var saved = Saved();
        Assert.Equal("http://192.168.1.20:25600", saved.GetProperty("NotifyKomgaUrl").GetString());
        Assert.Equal("http://kavita.lan:5000", saved.GetProperty("NotifyKavitaUrl").GetString());
    }

    [Fact]
    public async Task Save_RefusesEnablingATargetMissingARequiredField()
    {
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Ntfy: new NotificationTargetUpdate(Enabled: true, Url: "https://ntfy.sh"))));
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Gotify: new NotificationTargetUpdate(Enabled: true, Url: "https://g.example"))));
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Discord: new NotificationTargetUpdate(Enabled: true))));
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Enabled: true, Url: "http://k.lan"))));
    }

    [Fact]
    public async Task Save_AcceptsEnablingWhenRequiredFieldsAreAlreadyStored()
    {
        _settings.NotifyGotifyUrl = "https://g.example";
        _settings.NotifyGotifyToken = "stored";

        await SaveAsync(new NotificationsUpdate(Gotify: new NotificationTargetUpdate(Enabled: true)));

        Assert.True(Saved().GetProperty("NotifyGotifyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Save_RefusesClearingARequiredFieldOfAnEnabledTarget()
    {
        _settings.NotifyDiscordEnabled = true;
        _settings.NotifyDiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc";

        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Discord: new NotificationTargetUpdate(Url: ""))));
    }

    [Theory]
    [InlineData("library-changed")]
    [InlineData("something-else")]
    public async Task Save_RefusesAnEventAPushTargetCannotReceive(string events)
    {
        var refused = await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Webhook: new NotificationTargetUpdate(Events: events))));

        Assert.Contains(events, refused.Reason);
    }

    [Fact]
    public async Task Save_RefusesAPushEventForARescanTarget()
    {
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Events: "job-failed"))));
    }

    [Theory]
    [InlineData(NotificationTarget.Kavita, "abc", false)]
    [InlineData(NotificationTarget.Kavita, "0", false)]
    [InlineData(NotificationTarget.Kavita, "12", true)]
    [InlineData(NotificationTarget.Komga, "../etc", false)]
    [InlineData(NotificationTarget.Komga, "0BQ7WN7WVPX9E", true)]
    public async Task Save_ChecksTheLibraryIdFormat(string target, string libraryId, bool valid)
    {
        var update = new NotificationTargetUpdate(LibraryId: libraryId);
        var notifications = target == NotificationTarget.Kavita
            ? new NotificationsUpdate(Kavita: update)
            : new NotificationsUpdate(Komga: update);

        if (valid)
        {
            await SaveAsync(notifications);
        }
        else
        {
            await Assert.ThrowsAsync<SettingsValidationException>(() => SaveAsync(notifications));
        }
    }

    [Fact]
    public async Task Save_RefusesATopicWithSpaces()
    {
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => SaveAsync(new NotificationsUpdate(Ntfy: new NotificationTargetUpdate(Topic: "two words"))));
    }

    [Fact]
    public async Task Save_NeverWritesASecretToTheLog()
    {
        var logger = new Mock<ILogger<SettingsService>>();
        var logged = new List<string>();
        logger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => logged.Add(invocation.Arguments[2].ToString()!)));
        var service = new SettingsService(
            logger.Object,
            new TestOptionsMonitor<AppSettings>(_settings),
            new ConfigurationBuilder().Build());

        await service.UpdateSettingsAsync(new SettingsUpdate
        {
            Notifications = new NotificationsUpdate(
                Discord: new NotificationTargetUpdate(Url: "https://discord.com/api/webhooks/1/super-secret"),
                Gotify: new NotificationTargetUpdate(Token: "gotify-secret")),
        });

        Assert.Contains(logged, line => line.Contains("NotifyDiscordWebhookUrl"));
        Assert.DoesNotContain(logged, line => line.Contains("super-secret") || line.Contains("gotify-secret"));
    }

    [Fact]
    public async Task Save_ValueEqualToThePinnedOne_IsDropped_AndADifferentOneIsRefused()
    {
        _environment.Set("NOTIFY_KOMGA_URL", "http://komga.lan");

        await SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Url: "http://komga.lan/")));
        Assert.False(File.Exists(Path.Join(_configDir, "user-settings.json")));

        await Assert.ThrowsAsync<SettingsLockedException>(
            () => SaveAsync(new NotificationsUpdate(Komga: new NotificationTargetUpdate(Url: "http://other.lan"))));
    }

    [Fact]
    public async Task Save_AnyWriteToAPinnedSecretIsRefused_EvenTheSameValue()
    {
        _environment.Set("NOTIFY_WEBHOOK_URL", "https://hook.example/token");

        await Assert.ThrowsAsync<SettingsLockedException>(
            () => SaveAsync(new NotificationsUpdate(Webhook: new NotificationTargetUpdate(Url: "https://hook.example/token"))));
    }

    [Fact]
    public async Task Save_APinnedFieldLeftOut_StillCountsWhenEnabling()
    {
        _environment.Set("NOTIFY_KAVITA_API_KEY", "from-env");
        _settings.NotifyKavitaApiKey = "from-env"; // Applied by the options post-configure in the real host.

        await SaveAsync(new NotificationsUpdate(Kavita: new NotificationTargetUpdate(Enabled: true, Url: "http://kavita.lan")));

        Assert.True(Saved().GetProperty("NotifyKavitaEnabled").GetBoolean());
    }

    [Theory]
    [InlineData(true, "comics")]
    [InlineData(false, "")]
    public async Task GetAllSettings_TheNtfyTopic_GoesOnlyToAdministrators(bool administrator, string expected)
    {
        var controller = new SettingsController(
            new TestOptionsMonitor<AppSettings>(new AppSettings { NotifyNtfyTopic = "comics" }),
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IBatchJobRegistry>(),
            Mock.Of<IFileStoreService>(),
            Mock.Of<ISettingsService>(),
            Mock.Of<IHostApplicationLifetime>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            Mock.Of<IEventBroadcaster>(),
            Mock.Of<IProcessingHistoryService>(),
            administrator ? TestAuthorization.Administrator : TestAuthorization.NotAdministrator);

        var json = JsonSerializer.SerializeToElement(((OkObjectResult)(await controller.GetAllSettings()).Result!).Value);

        Assert.Equal(expected, json.GetProperty("notify_ntfy_topic").GetString());
    }

    [Fact]
    public async Task GetAllSettings_ReturnsWhetherSecretsAreSet_NeverTheSecrets()
    {
        var settings = new AppSettings
        {
            NotifyNtfyToken = "secret-1",
            NotifyGotifyToken = "secret-2",
            NotifyDiscordWebhookUrl = "https://discord.com/api/webhooks/1/secret-3",
            NotifyWebhookUrl = "https://hook.example/secret-4",
            NotifyKomgaApiKey = "secret-5",
            NotifyKavitaApiKey = "secret-6",
            NotifyKomgaUrl = "http://komga.lan",
        };
        var controller = new SettingsController(
            new TestOptionsMonitor<AppSettings>(settings),
            NullLogger<SettingsController>.Instance,
            Mock.Of<IDbContextFactory<ComicMaintainerDbContext>>(),
            Mock.Of<IBatchJobRegistry>(),
            Mock.Of<IFileStoreService>(),
            Mock.Of<ISettingsService>(),
            Mock.Of<IHostApplicationLifetime>(),
            Mock.Of<ISeriesLanguagePreferenceRetagService>(),
            Mock.Of<IEventBroadcaster>(),
            Mock.Of<IProcessingHistoryService>(),
            ComicMaintainer.Tests.Helpers.TestAuthorization.Administrator);

        var json = JsonSerializer.Serialize(((OkObjectResult)(await controller.GetAllSettings()).Result!).Value);

        Assert.DoesNotContain("secret-", json);
        Assert.Contains("\"notify_ntfy_token_set\":true", json);
        Assert.Contains("\"notify_discord_webhook_url_set\":true", json);
        Assert.Contains("\"notify_webhook_url_set\":true", json);
        Assert.Contains("\"notify_komga_url\":\"http://komga.lan\"", json);
    }

    [Theory]
    [InlineData("https://ntfy.sh", true)]
    [InlineData("http://192.168.1.2:8080/sub", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("https://user@example.com", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Url_AcceptsOnlyPlainHttpUrls(string? value, bool valid)
    {
        Assert.Equal(valid, NotificationUrl.TryParse(value, out _));
    }

    [Theory]
    [InlineData("http://komga.lan", "api/v1/libraries", "http://komga.lan/api/v1/libraries")]
    [InlineData("http://example.com/komga/", "/api/v1/libraries", "http://example.com/komga/api/v1/libraries")]
    public void Url_CombineKeepsASubPath(string baseUrl, string path, string expected)
    {
        Assert.True(NotificationUrl.TryParse(baseUrl, out var uri));

        Assert.Equal(expected, NotificationUrl.Combine(uri, path).ToString());
    }

    [Fact]
    public void Options_ResolveEveryTarget_AndUnknownTargetsAreRefused()
    {
        var settings = new AppSettings { NotifyKavitaLibraryId = "3", NotifyNtfyTopic = "t" };

        Assert.All(NotificationTarget.All, target => Assert.Equal(target, NotificationTargetOptions.For(settings, target).Target));
        Assert.Equal("3", NotificationTargetOptions.For(settings, NotificationTarget.Kavita).LibraryId);
        Assert.Equal("t", NotificationTargetOptions.For(settings, NotificationTarget.Ntfy).Topic);
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationTargetOptions.For(settings, "email"));
    }

    [Fact]
    public void Options_ReceiveNeedsTheTargetEnabledAndTheEventChosen()
    {
        var settings = new AppSettings { NotifyNtfyEnabled = true, NotifyNtfyEvents = "job-failed, NEW-CHAPTERS, nonsense" };

        var options = NotificationTargetOptions.For(settings, NotificationTarget.Ntfy);

        Assert.True(options.Receives(NotificationEventType.JobFailed));
        Assert.True(options.Receives(NotificationEventType.NewChapters));
        Assert.False(options.Receives(NotificationEventType.JobFindings));
        Assert.False(NotificationTargetOptions.For(new AppSettings(), NotificationTarget.Ntfy).Receives(NotificationEventType.JobFailed));
    }

    [Fact]
    public void Options_ARescanTargetIgnoresPushEvents()
    {
        var parsed = NotificationEventType.Parse(NotificationTarget.Komga, "job-failed,library-changed");

        Assert.Equal(new[] { NotificationEventType.LibraryChanged }, parsed);
    }

    [Theory]
    [InlineData(NotificationTarget.Komga, true)]
    [InlineData(NotificationTarget.Kavita, true)]
    [InlineData(NotificationTarget.Ntfy, false)]
    [InlineData(NotificationTarget.Webhook, false)]
    public void Catalog_KnowsWhichTargetsRescan(string target, bool rescan)
    {
        Assert.Equal(rescan, NotificationTarget.IsRescan(target));
        Assert.False(string.IsNullOrEmpty(NotificationTarget.DisplayName(target)));
    }

    [Fact]
    public void Catalog_NamesAnUnknownTargetByItsKey() =>
        Assert.Equal("other", NotificationTarget.DisplayName("other"));
}
