using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.WebApi.Services;

/// <summary>
/// Logs when Authelia stops responding and when it recovers, for deployments that set
/// <see cref="AutheliaSettings.Url"/> (<c>AUTHELIA_URL</c>).
/// </summary>
/// <remarks>
/// <para>With forward authentication the reverse proxy asks Authelia about every request before passing it on. While
/// Authelia is down the proxy answers every request itself, usually with HTTP 500 or 502, so nothing reaches
/// ComicMaintainer and its logs look as if all is well. This service checks Authelia's health endpoint directly so
/// that the outage shows up in the ComicMaintainer logs.</para>
/// <para>Only changes are logged: the first result, each outage (again every <see cref="ReminderInterval"/> while it
/// lasts) and each recovery. Outages are warnings, never errors: they are a problem with the environment rather than
/// a defect in ComicMaintainer, and errors are filed as GitHub issues by the error-reporting sink.</para>
/// </remarks>
public sealed class AutheliaHealthMonitor : BackgroundService
{
    /// <summary>The named <see cref="HttpClient"/> the checks are sent with.</summary>
    public const string HttpClientName = "AutheliaHealth";

    /// <summary>How often Authelia is checked.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a check waits for Authelia to answer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often an outage is logged again while it lasts.</summary>
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    private static readonly HttpRequestOptionsKey<bool> HealthCheckOption = new("ComicMaintainer.AutheliaHealthCheck");

    private readonly AutheliaSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AutheliaHealthMonitor> _logger;
    private readonly TimeProvider _timeProvider;

    // The result of the last check (null before the first), when the current run of failed checks started, and when
    // the current outage was last logged (null while it has not been).
    private bool? _reachable;
    private DateTimeOffset _unreachableSince;
    private DateTimeOffset? _lastWarning;

    public AutheliaHealthMonitor(
        IOptions<AutheliaSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<AutheliaHealthMonitor> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        HealthCheckUri = GetHealthCheckUri(_settings.Url);
    }

    /// <summary>
    /// The health endpoint that is checked, or null when <see cref="AutheliaSettings.Url"/> is not set or is not an
    /// http or https URL, in which case nothing is checked.
    /// </summary>
    public Uri? HealthCheckUri { get; }

    private string HealthCheckUrl => LoggingHelper.SanitizeForLog(HealthCheckUri?.AbsoluteUri);

    /// <summary>
    /// Authelia's health endpoint, <c>api/health</c>, under <paramref name="url"/>; null unless it is an absolute http
    /// or https URL. A path, used when Authelia is served under one, is kept; user info, query and fragment are not.
    /// </summary>
    public static Uri? GetHealthCheckUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(baseUri.Host))
        {
            return null;
        }

        return new UriBuilder(baseUri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/health",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    /// <summary>A health check request for <paramref name="uri"/>, marked so that <see cref="IsHealthCheck"/> recognizes it.</summary>
    public static HttpRequestMessage CreateHealthCheckRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Options.Set(HealthCheckOption, true);
        return request;
    }

    /// <summary>Whether <paramref name="request"/> is a health check, which is left out of the traces.</summary>
    public static bool IsHealthCheck(HttpRequestMessage request) =>
        request.Options.TryGetValue(HealthCheckOption, out var isHealthCheck) && isHealthCheck;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HealthCheckUri is null)
        {
            LogNotChecked();
            return;
        }

        _logger.LogInformation(
            "Checking Authelia at {HealthCheckUrl} every {IntervalSeconds} seconds",
            HealthCheckUrl,
            (int)CheckInterval.TotalSeconds);

        // Created before the first check so that the checks keep to the interval however long each one takes.
        using var timer = new PeriodicTimer(CheckInterval, _timeProvider);
        try
        {
            do
            {
                await CheckAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Checks Authelia once, and logs the result when it calls for it. Runs every <see cref="CheckInterval"/> once the
    /// service has started; public so that tests can run checks without the timer.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var uri = HealthCheckUri ?? throw new InvalidOperationException("AUTHELIA_URL is not set to an http or https URL.");
        var checkedAt = _timeProvider.GetUtcNow();
        var failure = await GetFailureAsync(uri, cancellationToken);
        if (failure is null)
        {
            RecordReachable(checkedAt);
        }
        else
        {
            RecordUnreachable(checkedAt, LoggingHelper.SanitizeForLog(failure));
        }
    }

    /// <summary>Requests the health endpoint; returns why Authelia is not available, or null when it is.</summary>
    private async Task<string?> GetFailureAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = CreateHealthCheckRequest(uri);
        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
            return response.IsSuccessStatusCode
                ? null
                : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}".TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return $"no response within {(int)RequestTimeout.TotalSeconds} seconds";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DescribeFailure(ex);
        }
    }

    private void RecordReachable(DateTimeOffset checkedAt)
    {
        if (_lastWarning is not null)
        {
            _logger.LogInformation(
                "Authelia is reachable again at {HealthCheckUrl}; it was unreachable for {Downtime}",
                HealthCheckUrl,
                DescribeDuration(checkedAt - _unreachableSince));
        }
        else if (_reachable != true)
        {
            _logger.LogInformation("Authelia is reachable at {HealthCheckUrl}", HealthCheckUrl);
        }

        _reachable = true;
        _lastWarning = null;
    }

    private void RecordUnreachable(DateTimeOffset checkedAt, string reason)
    {
        var wasReachable = _reachable;
        _reachable = false;

        if (wasReachable == true)
        {
            _unreachableSince = checkedAt;
            _lastWarning = checkedAt;
            _logger.LogWarning(
                "Authelia stopped responding at {HealthCheckUrl}: {Reason}. Until it recovers, the reverse proxy rejects requests to ComicMaintainer, usually with HTTP 500 or 502; ComicMaintainer itself is still running",
                HealthCheckUrl,
                reason);
        }
        else if (wasReachable is null)
        {
            // Authelia may be starting at the same time as this container, so the warning waits for the next check.
            _unreachableSince = checkedAt;
            _logger.LogDebug(
                "Authelia is not reachable yet at {HealthCheckUrl}: {Reason}. Checking again in {IntervalSeconds} seconds",
                HealthCheckUrl,
                reason,
                (int)CheckInterval.TotalSeconds);
        }
        else if (_lastWarning is null)
        {
            _lastWarning = checkedAt;
            _logger.LogWarning(
                "Authelia is not reachable at {HealthCheckUrl}: {Reason}. While Authelia is down, the reverse proxy rejects requests to ComicMaintainer, usually with HTTP 500 or 502. If Authelia is running, check that AUTHELIA_URL is an address this container can reach",
                HealthCheckUrl,
                reason);
        }
        else if (checkedAt - _lastWarning >= ReminderInterval)
        {
            _lastWarning = checkedAt;
            _logger.LogWarning(
                "Authelia has not been reachable at {HealthCheckUrl} for {Downtime}: {Reason}",
                HealthCheckUrl,
                DescribeDuration(checkedAt - _unreachableSince),
                reason);
        }
    }

    private void LogNotChecked()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Url))
        {
            _logger.LogWarning(
                "AUTHELIA_URL {AutheliaUrl} is not an http:// or https:// URL, such as http://authelia:9091, so Authelia outages will not be logged",
                LoggingHelper.SanitizeForLog(_settings.Url));
        }
        else if (_settings.Enabled)
        {
            _logger.LogInformation(
                "Set AUTHELIA_URL to Authelia's address, such as http://authelia:9091, to have Authelia outages logged: while Authelia is down, the reverse proxy rejects requests, usually with HTTP 500, before they reach ComicMaintainer");
        }
    }

    /// <summary>The messages of <paramref name="exception"/> and its inner exceptions, leaving out repeats.</summary>
    private static string DescribeFailure(Exception exception)
    {
        var description = string.Empty;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !description.Contains(message, StringComparison.Ordinal))
            {
                description = description.Length == 0 ? message : $"{description} {message}";
            }
        }

        return description.Length > 0 ? description : exception.GetType().Name;
    }

    /// <summary>A duration in words, to the second under a minute and to the minute above: "1 hour 5 minutes".</summary>
    private static string DescribeDuration(TimeSpan duration)
    {
        // Checks run a few milliseconds early or late, so 29.99 seconds is reported as the 30 it stands for.
        duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(duration.TotalSeconds)));
        if (duration < TimeSpan.FromMinutes(1))
        {
            return Count((int)duration.TotalSeconds, "second");
        }

        var parts = new List<string>(3);
        if (duration.Days > 0)
        {
            parts.Add(Count(duration.Days, "day"));
        }

        if (duration.Hours > 0)
        {
            parts.Add(Count(duration.Hours, "hour"));
        }

        if (duration.Minutes > 0)
        {
            parts.Add(Count(duration.Minutes, "minute"));
        }

        return string.Join(' ', parts);
    }

    private static string Count(int value, string unit) => value == 1 ? $"1 {unit}" : $"{value} {unit}s";
}
