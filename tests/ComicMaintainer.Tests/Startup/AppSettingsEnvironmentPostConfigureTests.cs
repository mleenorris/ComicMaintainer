using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Tests.Startup;

/// <summary>
/// Tests for the environment-variable overrides applied on top of <see cref="AppSettings"/>.
/// The class under test only reads process environment variables, so every test runs in
/// the non-parallel <see cref="EnvironmentCollection"/> and restores the variables it touches.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class AppSettingsEnvironmentPostConfigureTests : IDisposable
{
    private static readonly string[] StringVariables =
    [
        "WATCHED_DIR", "DUPLICATE_DIR", "CONFIG_DIR", "BASE_PATH", "COMICVINE_API_KEY", "COMICVINE_BASE_URL",
        "MANGADEX_BASE_URL", "ANILIST_BASE_URL", "SUWAYOMI_BASE_URL", "SUWAYOMI_USERNAME", "SUWAYOMI_PASSWORD",
        "SERIES_IMAGE_CACHE_DIR", "SMTP_HOST", "SMTP_USERNAME", "SMTP_PASSWORD", "EMAIL_FROM_ADDRESS",
        "EMAIL_FROM_NAME", "ERROR_REPORTING_GITHUB_TOKEN", "ERROR_REPORTING_ASSIGNEE",
    ];

    private static readonly (string Variable, Func<AppSettings, bool> Get, Action<AppSettings, bool> Set)[] BooleanVariables =
    [
        ("ENABLE_EXTERNAL_SERIES_METADATA", o => o.EnableExternalSeriesMetadata, (o, v) => o.EnableExternalSeriesMetadata = v),
        ("ENABLE_MANGADEX_METADATA", o => o.EnableMangaDexMetadata, (o, v) => o.EnableMangaDexMetadata = v),
        ("ENABLE_ANILIST_METADATA", o => o.EnableAniListMetadata, (o, v) => o.EnableAniListMetadata = v),
        ("ENABLE_SUWAYOMI", o => o.EnableSuwayomi, (o, v) => o.EnableSuwayomi = v),
        ("DOWNLOAD_EXTERNAL_SERIES_IMAGES", o => o.DownloadExternalSeriesImages, (o, v) => o.DownloadExternalSeriesImages = v),
        ("SMTP_USE_SSL", o => o.SmtpUseSsl, (o, v) => o.SmtpUseSsl = v),
        ("SMTP_ALLOW_INSECURE", o => o.SmtpAllowInsecure, (o, v) => o.SmtpAllowInsecure = v),
        ("ERROR_REPORTING_ENABLED", o => o.ErrorReportingEnabled, (o, v) => o.ErrorReportingEnabled = v),
        ("ERROR_REPORTING_COMMENT_ON_RECURRENCE", o => o.ErrorReportingCommentOnRecurrence, (o, v) => o.ErrorReportingCommentOnRecurrence = v),
    ];

    private static readonly (string Variable, Func<AppSettings, int> Get)[] PositiveIntegerVariables =
    [
        ("MANGADEX_REQUESTS_PER_SECOND", o => o.MangaDexRequestsPerSecond),
        ("ANILIST_REQUESTS_PER_MINUTE", o => o.AniListRequestsPerMinute),
        ("SERIES_IMAGE_MAX_BYTES", o => o.SeriesImageMaxBytes),
        ("SERIES_IMAGE_MAX_DOWNLOAD_BYTES", o => o.SeriesImageMaxDownloadBytes),
        ("SERIES_IMAGE_MAX_DIMENSION", o => o.SeriesImageMaxDimension),
        ("EMAIL_MAX_ATTACHMENT_MB", o => o.EmailMaxAttachmentMegabytes),
        ("ERROR_REPORTING_DEDUPE_WINDOW_HOURS", o => o.ErrorReportingDedupeWindowHours),
    ];

    private static readonly string[] EmailRateLimitVariables =
    [
        "EMAIL_MAX_SENDS_PER_HOUR", "EMAIL_MAX_SENDS_PER_DAY", "EMAIL_MIN_SECONDS_BETWEEN_SENDS",
    ];

    private static readonly string[] AllVariables =
    [
        .. StringVariables,
        .. BooleanVariables.Select(b => b.Variable),
        .. PositiveIntegerVariables.Select(i => i.Variable),
        "SMTP_PORT",
        "ERROR_REPORTING_MAX_ISSUES_PER_DAY",
        .. EmailRateLimitVariables,
    ];

    // AppSettingsEnvironmentPostConfigure is internal to the WebApi assembly (no InternalsVisibleTo).
    private static readonly IPostConfigureOptions<AppSettings> PostConfigure = (IPostConfigureOptions<AppSettings>)Activator.CreateInstance(
        typeof(Program).Assembly.GetType("ComicMaintainer.WebApi.Startup.AppSettingsEnvironmentPostConfigure", throwOnError: true)!)!;

    private readonly EnvironmentScope _environment = new(AllVariables);

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void WithoutEnvironmentVariables_LeavesTheOptionsUnchanged()
    {
        var options = new AppSettings
        {
            WatchedDirectory = "/library",
            BasePath = "/comics",
            ComicVineApiKey = "from-settings",
            EnableSuwayomi = true,
            SmtpPort = 2525,
            ErrorReportingMaxIssuesPerDay = 3,
        };
        var before = JsonSerializer.Serialize(options);

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Fact]
    public void AppliesEveryEnvironmentVariable()
    {
        foreach (var variable in StringVariables)
        {
            _environment.Set(variable, "env:" + variable);
        }

        foreach (var (variable, _, _) in BooleanVariables)
        {
            _environment.Set(variable, "true");
        }

        for (var i = 0; i < PositiveIntegerVariables.Length; i++)
        {
            _environment.Set(PositiveIntegerVariables[i].Variable, (1000 + i).ToString());
        }

        _environment.Set("SMTP_PORT", "2525");
        _environment.Set("ERROR_REPORTING_MAX_ISSUES_PER_DAY", "42");
        _environment.Set("EMAIL_MAX_SENDS_PER_HOUR", "20");
        _environment.Set("EMAIL_MAX_SENDS_PER_DAY", "200");
        _environment.Set("EMAIL_MIN_SECONDS_BETWEEN_SENDS", "30");
        var options = new AppSettings();
        foreach (var (_, _, set) in BooleanVariables)
        {
            set(options, false);
        }

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.Equal("env:WATCHED_DIR", options.WatchedDirectory);
        Assert.Equal("env:DUPLICATE_DIR", options.DuplicateDirectory);
        Assert.Equal("env:CONFIG_DIR", options.ConfigDirectory);
        Assert.Equal("env:BASE_PATH", options.BasePath);
        Assert.Equal("env:COMICVINE_API_KEY", options.ComicVineApiKey);
        Assert.Equal("env:COMICVINE_BASE_URL", options.ComicVineBaseUrl);
        Assert.Equal("env:MANGADEX_BASE_URL", options.MangaDexBaseUrl);
        Assert.Equal("env:ANILIST_BASE_URL", options.AniListBaseUrl);
        Assert.Equal("env:SUWAYOMI_BASE_URL", options.SuwayomiBaseUrl);
        Assert.Equal("env:SUWAYOMI_USERNAME", options.SuwayomiUsername);
        Assert.Equal("env:SUWAYOMI_PASSWORD", options.SuwayomiPassword);
        Assert.Equal("env:SERIES_IMAGE_CACHE_DIR", options.SeriesImageCacheDirectory);
        Assert.Equal("env:SMTP_HOST", options.SmtpHost);
        Assert.Equal("env:SMTP_USERNAME", options.SmtpUsername);
        Assert.Equal("env:SMTP_PASSWORD", options.SmtpPassword);
        Assert.Equal("env:EMAIL_FROM_ADDRESS", options.EmailFromAddress);
        Assert.Equal("env:EMAIL_FROM_NAME", options.EmailFromName);
        Assert.Equal("env:ERROR_REPORTING_GITHUB_TOKEN", options.ErrorReportingGitHubToken);
        Assert.Equal("env:ERROR_REPORTING_ASSIGNEE", options.ErrorReportingAssignee);

        Assert.All(BooleanVariables, b => Assert.True(b.Get(options), b.Variable));

        for (var i = 0; i < PositiveIntegerVariables.Length; i++)
        {
            Assert.Equal(1000 + i, PositiveIntegerVariables[i].Get(options));
        }

        Assert.Equal(2525, options.SmtpPort);
        Assert.Equal(42, options.ErrorReportingMaxIssuesPerDay);
        Assert.Equal(20, options.EmailMaxSendsPerHour);
        Assert.Equal(200, options.EmailMaxSendsPerDay);
        Assert.Equal(30, options.EmailMinSecondsBetweenSends);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    public void BooleanVariables_AreTrueOnlyForTheWordTrue(string value, bool expected)
    {
        foreach (var (variable, _, _) in BooleanVariables)
        {
            _environment.Set(variable, value);
        }

        var options = new AppSettings();
        foreach (var (_, _, set) in BooleanVariables)
        {
            set(options, !expected);
        }

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.All(BooleanVariables, b => Assert.Equal(expected, b.Get(options)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void PositiveIntegerVariables_IgnoreValuesThatAreNotPositiveIntegers(string value)
    {
        foreach (var (variable, _) in PositiveIntegerVariables)
        {
            _environment.Set(variable, value);
        }

        var defaults = new AppSettings();
        var options = new AppSettings();

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.All(PositiveIntegerVariables, i => Assert.Equal(i.Get(defaults), i.Get(options)));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("465", 465)]
    [InlineData("65535", 65535)]
    [InlineData("0", 587)]
    [InlineData("-25", 587)]
    [InlineData("65536", 587)]
    [InlineData("smtp", 587)]
    public void SmtpPort_IsAppliedOnlyForAValidTcpPort(string value, int expected)
    {
        _environment.Set("SMTP_PORT", value);
        var options = new AppSettings();

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.Equal(expected, options.SmtpPort);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("25", 25)]
    [InlineData("-1", 10)]
    [InlineData("many", 10)]
    public void ErrorReportingMaxIssuesPerDay_AcceptsZeroToDisableReporting(string value, int expected)
    {
        _environment.Set("ERROR_REPORTING_MAX_ISSUES_PER_DAY", value);
        var options = new AppSettings();

        PostConfigure.PostConfigure(Options.DefaultName, options);

        Assert.Equal(expected, options.ErrorReportingMaxIssuesPerDay);
    }

    [Theory]
    [InlineData("EMAIL_MAX_SENDS_PER_HOUR", "0", 0)]
    [InlineData("EMAIL_MAX_SENDS_PER_HOUR", "10000", 10_000)]
    [InlineData("EMAIL_MAX_SENDS_PER_HOUR", "10001", 7)]
    [InlineData("EMAIL_MAX_SENDS_PER_HOUR", "-1", 7)]
    [InlineData("EMAIL_MAX_SENDS_PER_DAY", "0", 0)]
    [InlineData("EMAIL_MAX_SENDS_PER_DAY", "100000", 100_000)]
    [InlineData("EMAIL_MAX_SENDS_PER_DAY", "100001", 7)]
    [InlineData("EMAIL_MAX_SENDS_PER_DAY", "lots", 7)]
    [InlineData("EMAIL_MIN_SECONDS_BETWEEN_SENDS", "0", 0)]
    [InlineData("EMAIL_MIN_SECONDS_BETWEEN_SENDS", "86400", 86_400)]
    [InlineData("EMAIL_MIN_SECONDS_BETWEEN_SENDS", "86401", 7)]
    [InlineData("EMAIL_MIN_SECONDS_BETWEEN_SENDS", "1.5", 7)]
    public void EmailRateLimitVariables_AcceptZeroUpToTheirBound(string variable, string value, int expected)
    {
        _environment.Set(variable, value);
        var options = new AppSettings { EmailMaxSendsPerHour = 7, EmailMaxSendsPerDay = 7, EmailMinSecondsBetweenSends = 7 };

        PostConfigure.PostConfigure(Options.DefaultName, options);

        var actual = variable switch
        {
            "EMAIL_MAX_SENDS_PER_HOUR" => options.EmailMaxSendsPerHour,
            "EMAIL_MAX_SENDS_PER_DAY" => options.EmailMaxSendsPerDay,
            _ => options.EmailMinSecondsBetweenSends,
        };
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Collection for tests that read or write process-wide environment variables. xUnit runs
    /// collections with parallelization disabled on their own, after the parallel ones.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class EnvironmentCollection
    {
        public const string Name = "Process environment variables";
    }

    /// <summary>
    /// Clears the given variables for the duration of a test and restores the original values.
    /// </summary>
    public sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public EnvironmentScope(params string[] variables)
        {
            foreach (var variable in variables)
            {
                Set(variable, null);
            }
        }

        public void Set(string variable, string? value)
        {
            _original.TryAdd(variable, Environment.GetEnvironmentVariable(variable));
            Environment.SetEnvironmentVariable(variable, value);
        }

        public void Dispose()
        {
            foreach (var (variable, value) in _original)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }
    }
}
