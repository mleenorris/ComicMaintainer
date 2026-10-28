using System.Collections.Concurrent;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Services;

public sealed class SettingsServiceCoverageTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _settingsPath;
    private readonly CapturingLogger _logger = new();
    private readonly SettingsService _service;

    public SettingsServiceCoverageTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"SettingsServiceCoverage_{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_configDir, "user-settings.json");
        _service = new SettingsService(
            _logger,
            new TestOptionsMonitor<AppSettings>(new AppSettings { ConfigDirectory = _configDir }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_CreatesTheConfigDirectory()
    {
        Assert.True(Directory.Exists(_configDir));
        Assert.Contains(_logger.Messages, m => m.Contains(_settingsPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateIssueNumberPaddingAsync_RejectsNegativePadding()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateIssueNumberPaddingAsync(-1));

        Assert.Equal("padding", ex.ParamName);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task UpdateIssueNumberPaddingAsync_AcceptsZero()
    {
        await _service.UpdateIssueNumberPaddingAsync(0);

        Assert.Equal(0, ReadSection()["IssueNumberPadding"].GetInt32());
    }

    [Theory]
    [InlineData("WatcherEnableNormalize")]
    [InlineData("WriteCoverToFirstArchive")]
    [InlineData("WriteCoverToSeriesFolder")]
    [InlineData("DownloadExternalSeriesImages")]
    [InlineData("AllowRegistration")]
    [InlineData("EnableExternalSeriesMetadata")]
    [InlineData("EnableMangaDexMetadata")]
    [InlineData("EnableAniListMetadata")]
    public async Task BooleanSetters_PersistTheValueUnderTheirSettingName(string settingName)
    {
        Func<bool, Task> update = settingName switch
        {
            "WatcherEnableNormalize" => v => _service.UpdateWatcherEnableNormalizeAsync(v),
            "WriteCoverToFirstArchive" => v => _service.UpdateWriteCoverToFirstArchiveAsync(v),
            "WriteCoverToSeriesFolder" => v => _service.UpdateWriteCoverToSeriesFolderAsync(v),
            "DownloadExternalSeriesImages" => v => _service.UpdateDownloadExternalSeriesImagesAsync(v),
            "AllowRegistration" => v => _service.UpdateAllowRegistrationAsync(v),
            "EnableExternalSeriesMetadata" => v => _service.UpdateExternalSeriesMetadataEnabledAsync(v),
            "EnableMangaDexMetadata" => v => _service.UpdateMangaDexEnabledAsync(v),
            "EnableAniListMetadata" => v => _service.UpdateAniListEnabledAsync(v),
            _ => throw new ArgumentOutOfRangeException(nameof(settingName))
        };

        await update(true);
        Assert.True(ReadSection()[settingName].GetBoolean());

        await update(false);
        var section = ReadSection();
        Assert.False(section[settingName].GetBoolean());
        Assert.Single(section);
        Assert.Contains($"Updated setting {settingName} to False", _logger.Messages);
    }

    [Theory]
    [InlineData("SeriesImageMaxBytes", "maxBytes")]
    [InlineData("SeriesImageMaxDownloadBytes", "maxDownloadBytes")]
    [InlineData("SeriesImageMaxDimension", "maxDimension")]
    public async Task PositiveIntegerSetters_PersistPositiveValuesAndRejectTheRest(string settingName, string parameterName)
    {
        Func<int, Task> update = settingName switch
        {
            "SeriesImageMaxBytes" => v => _service.UpdateSeriesImageMaxBytesAsync(v),
            "SeriesImageMaxDownloadBytes" => v => _service.UpdateSeriesImageMaxDownloadBytesAsync(v),
            "SeriesImageMaxDimension" => v => _service.UpdateSeriesImageMaxDimensionAsync(v),
            _ => throw new ArgumentOutOfRangeException(nameof(settingName))
        };

        await update(4096);
        Assert.Equal(4096, ReadSection()[settingName].GetInt32());

        var zero = await Assert.ThrowsAsync<ArgumentException>(() => update(0));
        var negative = await Assert.ThrowsAsync<ArgumentException>(() => update(-5));

        Assert.Equal(parameterName, zero.ParamName);
        Assert.Equal(parameterName, negative.ParamName);
        Assert.Equal(4096, ReadSection()[settingName].GetInt32());
    }

    [Theory]
    [InlineData("GitHubIssueAssignee")]
    [InlineData("ComicVineBaseUrl")]
    [InlineData("MangaDexBaseUrl")]
    [InlineData("AniListBaseUrl")]
    public async Task NullableStringSetters_PersistValuesAndClearToNull(string settingName)
    {
        Func<string?, Task> update = settingName switch
        {
            "GitHubIssueAssignee" => v => _service.UpdateGitHubIssueAssigneeAsync(v),
            "ComicVineBaseUrl" => v => _service.UpdateComicVineBaseUrlAsync(v),
            "MangaDexBaseUrl" => v => _service.UpdateMangaDexBaseUrlAsync(v),
            "AniListBaseUrl" => v => _service.UpdateAniListBaseUrlAsync(v),
            _ => throw new ArgumentOutOfRangeException(nameof(settingName))
        };

        await update("https://provider.example/api");
        Assert.Equal("https://provider.example/api", ReadSection()[settingName].GetString());
        Assert.Contains($"Updated setting {settingName} to https://provider.example/api", _logger.Messages);

        await update(null);
        Assert.Equal(JsonValueKind.Null, ReadSection()[settingName].ValueKind);
        Assert.Contains($"Updated setting {settingName} to null", _logger.Messages);
    }

    [Fact]
    public async Task UpdateSettingAsync_EscapesControlCharactersInTheLoggedValue()
    {
        await _service.UpdateGitHubIssueAssigneeAsync("octo\ncat");

        Assert.Equal("octo\ncat", ReadSection()["GitHubIssueAssignee"].GetString());
        Assert.Contains("Updated setting GitHubIssueAssignee to octo\\ncat", _logger.Messages);
    }

    [Fact]
    public async Task UpdateComicVineApiKeyAsync_PersistsTheKeyWithoutLoggingIt()
    {
        await _service.UpdateComicVineApiKeyAsync("cv-secret-key-123");

        Assert.Equal("cv-secret-key-123", ReadSection()["ComicVineApiKey"].GetString());
        Assert.Contains("Updated setting ComicVineApiKey (value hidden)", _logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("cv-secret-key-123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateComicVineApiKeyAsync_ReportsAClearedKey(string? cleared)
    {
        await _service.UpdateComicVineApiKeyAsync("cv-secret-key-123");
        await _service.UpdateComicVineApiKeyAsync(cleared);

        var stored = ReadSection()["ComicVineApiKey"];
        Assert.Equal(cleared, stored.ValueKind == JsonValueKind.Null ? null : stored.GetString());
        Assert.Contains("Updated setting ComicVineApiKey (cleared)", _logger.Messages);
    }

    [Fact]
    public async Task UpdateGitHubTokenAsync_PersistsTheTokenWithoutLoggingIt()
    {
        await _service.UpdateGitHubTokenAsync("ghp_secret_token_123");

        Assert.Equal("ghp_secret_token_123", ReadSection()["GitHubToken"].GetString());
        Assert.Contains("Updated setting GitHubToken (value hidden)", _logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("ghp_secret_token_123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateGitHubTokenAsync_ReportsAClearedToken(string? cleared)
    {
        await _service.UpdateGitHubTokenAsync("ghp_secret_token_123");
        await _service.UpdateGitHubTokenAsync(cleared);

        var stored = ReadSection()["GitHubToken"];
        Assert.Equal(cleared, stored.ValueKind == JsonValueKind.Null ? null : stored.GetString());
        Assert.Contains("Updated setting GitHubToken (cleared)", _logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("ghp_secret_token_123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("JA-Latn", "ja")]
    [InlineData(" en ", "en")]
    [InlineData("zh-hk", "zh")]
    public async Task UpdateDefaultPreferredLanguageAsync_PersistsThePrimarySubtag(string input, string expected)
    {
        await _service.UpdateDefaultPreferredLanguageAsync(input);

        Assert.Equal(expected, ReadSection()["DefaultPreferredLanguage"].GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task UpdateDefaultPreferredLanguageAsync_BlankClearsTheDefault(string? input)
    {
        await _service.UpdateDefaultPreferredLanguageAsync("ko");
        await _service.UpdateDefaultPreferredLanguageAsync(input);

        Assert.Equal(JsonValueKind.Null, ReadSection()["DefaultPreferredLanguage"].ValueKind);
    }

    [Fact]
    public async Task UpdateDefaultPreferredLanguageAsync_RejectsUnsupportedLanguages()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateDefaultPreferredLanguageAsync("fr"));

        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_PersistsTrimmedValuesAndHidesThePassword()
    {
        await _service.UpdateEmailSettingsAsync(
            smtpHost: " smtp.example.com ",
            smtpPort: 587,
            smtpUsername: " mailer ",
            smtpPassword: "smtp-secret-value",
            smtpUseSsl: true,
            smtpAllowInsecure: false,
            fromAddress: " sender@example.com ",
            fromName: " Comic Bot ",
            maxAttachmentMegabytes: 25);

        var section = ReadSection();
        Assert.Equal("smtp.example.com", section["SmtpHost"].GetString());
        Assert.Equal(587, section["SmtpPort"].GetInt32());
        Assert.Equal("mailer", section["SmtpUsername"].GetString());
        Assert.Equal("smtp-secret-value", section["SmtpPassword"].GetString());
        Assert.True(section["SmtpUseSsl"].GetBoolean());
        Assert.False(section["SmtpAllowInsecure"].GetBoolean());
        Assert.Equal("sender@example.com", section["EmailFromAddress"].GetString());
        Assert.Equal("Comic Bot", section["EmailFromName"].GetString());
        Assert.Equal(25, section["EmailMaxAttachmentMegabytes"].GetInt32());
        Assert.Contains("Updated setting SmtpPassword (value hidden)", _logger.Messages);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("smtp-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_BlankOptionalValuesFallBackToDefaults()
    {
        await _service.UpdateEmailSettingsAsync(
            smtpHost: "   ",
            smtpPort: 25,
            smtpUsername: null,
            smtpPassword: null,
            smtpUseSsl: false,
            smtpAllowInsecure: true,
            fromAddress: "  ",
            fromName: " ",
            maxAttachmentMegabytes: 1);

        var section = ReadSection();
        Assert.Equal(JsonValueKind.Null, section["SmtpHost"].ValueKind);
        Assert.Equal(JsonValueKind.Null, section["SmtpUsername"].ValueKind);
        Assert.Equal(JsonValueKind.Null, section["EmailFromAddress"].ValueKind);
        Assert.Equal("ComicMaintainer", section["EmailFromName"].GetString());
        Assert.True(section["SmtpAllowInsecure"].GetBoolean());
        Assert.Equal(1, section["EmailMaxAttachmentMegabytes"].GetInt32());
        Assert.False(section.ContainsKey("SmtpPassword"));
    }

    [Fact]
    public async Task UpdateEmailSettingsAsync_NullPasswordKeepsTheStoredSecretAndEmptyClearsIt()
    {
        await _service.UpdateEmailSettingsAsync("smtp.example.com", 465, "u", "first-secret", true, false, null, null, 200);

        await _service.UpdateEmailSettingsAsync("smtp.example.com", 465, "u", null, true, false, null, null, 200);
        Assert.Equal("first-secret", ReadSection()["SmtpPassword"].GetString());

        await _service.UpdateEmailSettingsAsync("smtp.example.com", 465, "u", string.Empty, true, false, null, null, 200);
        Assert.Equal(JsonValueKind.Null, ReadSection()["SmtpPassword"].ValueKind);
        Assert.Contains("Updated setting SmtpPassword (cleared)", _logger.Messages);
    }

    [Theory]
    [InlineData(0, 10, "smtpPort")]
    [InlineData(65536, 10, "smtpPort")]
    [InlineData(25, 0, "maxAttachmentMegabytes")]
    [InlineData(25, 201, "maxAttachmentMegabytes")]
    public async Task UpdateEmailSettingsAsync_RejectsOutOfRangeNumbers(int port, int maxMegabytes, string parameterName)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateEmailSettingsAsync(
            "smtp.example.com", port, null, null, true, false, null, null, maxMegabytes));

        Assert.Equal(parameterName, ex.ParamName);
        Assert.False(File.Exists(_settingsPath));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Sender <sender@example.com>")]
    public async Task UpdateEmailSettingsAsync_RejectsAnInvalidFromAddress(string fromAddress)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateEmailSettingsAsync(
            "smtp.example.com", 25, null, null, true, false, fromAddress, null, 10));

        Assert.Equal("fromAddress", ex.ParamName);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task UpdateEmailRateLimitSettingsAsync_PersistsAllThreeLimits()
    {
        await _service.UpdateEmailRateLimitSettingsAsync(maxSendsPerHour: 20, maxSendsPerDay: 200, minSecondsBetweenSends: 30);

        var section = ReadSection();
        Assert.Equal(20, section["EmailMaxSendsPerHour"].GetInt32());
        Assert.Equal(200, section["EmailMaxSendsPerDay"].GetInt32());
        Assert.Equal(30, section["EmailMinSecondsBetweenSends"].GetInt32());
    }

    [Fact]
    public async Task UpdateEmailRateLimitSettingsAsync_AcceptsZeroAndTheUpperBounds()
    {
        await _service.UpdateEmailRateLimitSettingsAsync(
            EmailRateLimitBounds.MaxSendsPerHour,
            EmailRateLimitBounds.MaxSendsPerDay,
            EmailRateLimitBounds.MaxMinSecondsBetweenSends);
        await _service.UpdateEmailRateLimitSettingsAsync(0, 0, 0);

        var section = ReadSection();
        Assert.Equal(0, section["EmailMaxSendsPerHour"].GetInt32());
        Assert.Equal(0, section["EmailMaxSendsPerDay"].GetInt32());
        Assert.Equal(0, section["EmailMinSecondsBetweenSends"].GetInt32());
    }

    [Theory]
    [InlineData(-1, 0, 0, "maxSendsPerHour", "Maximum emails per hour must be between 0 and 10000")]
    [InlineData(10_001, 0, 0, "maxSendsPerHour", "Maximum emails per hour must be between 0 and 10000")]
    [InlineData(0, -1, 0, "maxSendsPerDay", "Maximum emails per day must be between 0 and 100000")]
    [InlineData(0, 100_001, 0, "maxSendsPerDay", "Maximum emails per day must be between 0 and 100000")]
    [InlineData(0, 0, -1, "minSecondsBetweenSends", "Minimum seconds between emails must be between 0 and 86400")]
    [InlineData(0, 0, 86_401, "minSecondsBetweenSends", "Minimum seconds between emails must be between 0 and 86400")]
    public async Task UpdateEmailRateLimitSettingsAsync_RejectsOutOfRangeValuesWithoutSavingAny(
        int maxSendsPerHour,
        int maxSendsPerDay,
        int minSecondsBetweenSends,
        string parameterName,
        string expectedMessage)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateEmailRateLimitSettingsAsync(
            maxSendsPerHour, maxSendsPerDay, minSecondsBetweenSends));

        Assert.Equal(parameterName, ex.ParamName);
        Assert.StartsWith(expectedMessage, ex.Message);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task UpdateSuwayomiSettingsAsync_DisabledWithoutUrlOrUsernameStoresNulls()
    {
        await _service.UpdateSuwayomiSettingsAsync(enabled: false, baseUrl: "   ", username: "  ", password: null);

        var section = ReadSection();
        Assert.False(section["EnableSuwayomi"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, section["SuwayomiBaseUrl"].ValueKind);
        Assert.Equal(JsonValueKind.Null, section["SuwayomiUsername"].ValueKind);
        Assert.False(section.ContainsKey("SuwayomiPassword"));
        Assert.Contains("Updated setting SuwayomiBaseUrl to null", _logger.Messages);
        Assert.Contains("Updated setting SuwayomiUsername to null", _logger.Messages);
    }

    [Fact]
    public async Task UpdateErrorReportingSettingsAsync_RefusesAWhitespaceTokenWhenEnabling()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateErrorReportingSettingsAsync(
            enabled: true,
            gitHubToken: "   ",
            assignee: null,
            maxIssuesPerDay: 5,
            dedupeWindowHours: 12,
            commentOnRecurrence: true));

        Assert.Equal("gitHubToken", ex.ParamName);
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task UpdateErrorReportingSettingsAsync_TreatsAnUnreadableSettingsFileAsHavingNoToken()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            """{ "AppSettings": { "ErrorReportingGitHubToken": "stored-token" } }""");

        // An exclusive lock makes the settings file unreadable for the service.
        using (new FileStream(_settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateErrorReportingSettingsAsync(
                enabled: true,
                gitHubToken: null,
                assignee: null,
                maxIssuesPerDay: 5,
                dedupeWindowHours: 12,
                commentOnRecurrence: true));

            Assert.Equal("gitHubToken", ex.ParamName);
        }

        Assert.Equal("stored-token", ReadSection()["ErrorReportingGitHubToken"].GetString());
    }

    [Fact]
    public async Task UpdateErrorReportingSettingsAsync_IgnoresANonStringStoredToken()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            """{ "AppSettings": { "ErrorReportingGitHubToken": 12345 } }""");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateErrorReportingSettingsAsync(
            true, null, null, 5, 12, true));
    }

    [Fact]
    public async Task PersistFailure_IsLoggedRethrownAndReleasesTheWriteLock()
    {
        Directory.Delete(_configDir, recursive: true);

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _service.UpdateLogMaxBytesAsync(1024));

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Same(ex, error.Exception);
        Assert.Equal("Failed to persist settings LogMaxBytes", error.Message);

        // The semaphore must be released so later writes are not blocked forever.
        Directory.CreateDirectory(_configDir);
        await _service.UpdateLogMaxBytesAsync(2048).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2048, ReadSection()["LogMaxBytes"].GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{ \"AppSettings\": { \"LogMaxBytes\": ")]
    [InlineData("not json at all")]
    public async Task ExistingFileThatIsEmptyOrNotAnObject_IsReplacedWithAValidFile(string existingContent)
    {
        await File.WriteAllTextAsync(_settingsPath, existingContent);

        await _service.UpdateLogMaxBytesAsync(4096);

        var section = ReadSection();
        Assert.Equal(4096, Assert.Single(section, kv => kv.Key == "LogMaxBytes").Value.GetInt32());
        Assert.Single(section);
    }

    [Fact]
    public async Task ExistingValuesOfEveryJsonKind_SurviveAnUnrelatedUpdate()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            """
            {
              "AppSettings": {
                "Tags": ["a", 2, true],
                "Nested": { "Inner": "x", "Count": 3 },
                "Ratio": 0.75,
                "Huge": 1e30,
                "Big": 9007199254740993,
                "Flag": false,
                "Enabled": true,
                "Missing": null,
                "Name": "kept"
              }
            }
            """);

        await _service.UpdateLogMaxBytesAsync(4096);

        var section = ReadSection();
        Assert.Equal(4096, section["LogMaxBytes"].GetInt32());
        Assert.Equal(JsonValueKind.Array, section["Tags"].ValueKind);
        Assert.Equal(new[] { "a", "2", "True" }, section["Tags"].EnumerateArray().Select(e => e.ToString()).ToArray());
        Assert.Equal("x", section["Nested"].GetProperty("Inner").GetString());
        Assert.Equal(3, section["Nested"].GetProperty("Count").GetInt32());
        Assert.Equal(0.75, section["Ratio"].GetDouble());
        Assert.Equal(1e30, section["Huge"].GetDouble());
        Assert.Equal(9007199254740993L, section["Big"].GetInt64());
        Assert.False(section["Flag"].GetBoolean());
        Assert.True(section["Enabled"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, section["Missing"].ValueKind);
        Assert.Equal("kept", section["Name"].GetString());
    }

    private Dictionary<string, JsonElement> ReadSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
        Assert.True(document.RootElement.TryGetProperty("AppSettings", out var section));
        Assert.Equal(JsonValueKind.Object, section.ValueKind);
        return section.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    internal sealed class CapturingLogger : ILogger<SettingsService>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IEnumerable<string> Messages => _entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// Tests that change the process-wide ERROR_REPORTING_GITHUB_TOKEN variable, so
/// they run in a collection that never executes alongside other tests.
/// </summary>
[Collection(CollectionName)]
public sealed class SettingsServiceEnvironmentTokenTests : IDisposable
{
    public const string CollectionName = "SettingsServiceCoverageTests.ProcessEnvironment";
    private const string TokenVariable = "ERROR_REPORTING_GITHUB_TOKEN";

    private readonly string _configDir;
    private readonly SettingsService _service;

    public SettingsServiceEnvironmentTokenTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"SettingsServiceEnvToken_{Guid.NewGuid():N}");
        _service = new SettingsService(
            new SettingsServiceCoverageTests.CapturingLogger(),
            new TestOptionsMonitor<AppSettings>(new AppSettings { ConfigDirectory = _configDir }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateErrorReportingSettingsAsync_AcceptsATokenSuppliedByTheEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "environment-token");
        try
        {
            await _service.UpdateErrorReportingSettingsAsync(
                enabled: true,
                gitHubToken: null,
                assignee: null,
                maxIssuesPerDay: 3,
                dedupeWindowHours: 24,
                commentOnRecurrence: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, previous);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_configDir, "user-settings.json")));
        var section = document.RootElement.GetProperty("AppSettings");
        Assert.True(section.GetProperty("ErrorReportingEnabled").GetBoolean());
        // The environment token is an override; it must never be copied into the file.
        Assert.False(section.TryGetProperty("ErrorReportingGitHubToken", out _));
    }

    [CollectionDefinition(CollectionName, DisableParallelization = true)]
    public sealed class ProcessEnvironmentCollection
    {
    }
}
