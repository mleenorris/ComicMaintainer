using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IMetronClient"/>. Metron signs a request in with an API token (a Knox token,
/// sent as <c>Authorization: Bearer</c>), which is preferred, or with the account's username and
/// password (HTTP Basic), always over https. It allows 20 requests a minute and 5,000 a day per
/// account: this client stays under 18 a minute (a background run waits for the next one; an
/// interactive one does not) and 4,900 a day, remembers each answer for a week (a series' volume
/// number does not change, and the answers are dropped when the credentials or address change), and
/// after a refusal or failure asks nothing for 10 minutes. A rejected sign-in stops it until the
/// credentials or address change, or for 6 hours, and is shown in the settings (<see cref="Problem"/>)
/// and on the provider status screen (<see cref="CheckHealthAsync"/>).
/// </summary>
public sealed class MetronClient : IMetronClient
{
    /// <summary>The name of the HTTP client registered for Metron.</summary>
    public const string HttpClientName = "Metron";

    /// <summary>The provider's name on the status screen.</summary>
    public const string ProviderName = "Metron";

    internal const int RequestsPerMinute = 18;
    internal const int RequestsPerDay = 4900;

    /// <summary>The ComicVine volume the status probe asks about: Batman (1940), which Metron surely records.</summary>
    internal const int ProbeVolumeId = 796;

    private static readonly TimeSpan AnswerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryRejectedSignIn = TimeSpan.FromHours(6);
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<MetronClient> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, (MetronSeries? Series, DateTimeOffset At)> _answers = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _lock = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private DateOnly _day;
    private int _today;
    private string? _account;
    private string? _rejectedAccount;
    private DateTimeOffset _rejectedAt;
    private string? _problem;
    private long _successes;
    private long _failures;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;
    private (string Account, DateTimeOffset At, bool Reachable)? _probe;

    public MetronClient(IHttpClientFactory httpClients, IOptionsMonitor<AppSettings> settings, ILogger<MetronClient> logger, TimeProvider time)
    {
        _httpClients = httpClients;
        _settings = settings;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public bool IsConfigured => Configured(_settings.CurrentValue);

    /// <inheritdoc />
    public string? Problem
    {
        get
        {
            lock (_lock)
            {
                return IsConfigured && _account == Account(_settings.CurrentValue) ? _problem : null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<MetronAnswer> SeriesOfComicVineVolumeAsync(int comicVineVolumeId, bool waitForPermit = false, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || comicVineVolumeId <= 0)
        {
            return MetronAnswer.Unavailable;
        }

        var config = _settings.CurrentValue;
        if (!Prepare(config, comicVineVolumeId, out var url))
        {
            return MetronAnswer.Unavailable;
        }

        if (_answers.TryGetValue(comicVineVolumeId, out var known) && _time.GetUtcNow() - known.At < AnswerLifetime)
        {
            return new MetronAnswer(true, known.Series);
        }

        if (!await TakePermitAsync(waitForPermit, cancellationToken))
        {
            return MetronAnswer.Unavailable;
        }

        if (await GetAsync(config, url, cancellationToken) is not { } document)
        {
            return MetronAnswer.Unavailable;
        }

        using (document)
        {
            var series = ParseSeries(document.RootElement);
            _answers[comicVineVolumeId] = (series, _time.GetUtcNow());
            return new MetronAnswer(true, series);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = _settings.CurrentValue;
        var health = Snapshot();
        health.Enabled = config.EnableExternalSeriesMetadata;
        health.Configured = Configured(config);
        if (!health.Enabled)
        {
            health.StatusMessage = "Disabled in settings";
            return health;
        }

        if (!health.Configured)
        {
            health.StatusMessage = "Missing API token (or username and password)";
            return health;
        }

        // A probe at most every 10 minutes, so opening the screen costs next to nothing.
        var account = Account(config);
        bool fresh;
        bool reachable;
        lock (_lock)
        {
            (fresh, reachable) = _probe is { } probe && probe.Account == account && _time.GetUtcNow() - probe.At < ProbeLifetime
                ? (true, probe.Reachable)
                : (false, false);
        }

        if (!fresh)
        {
            reachable = Prepare(config, ProbeVolumeId, out var url)
                        && await TakePermitAsync(wait: false, cancellationToken)
                        && await GetAsync(config, url, cancellationToken) is { } document
                        && Dispose(document);
            lock (_lock)
            {
                _probe = (account, _time.GetUtcNow(), reachable);
            }
        }

        health = Snapshot();
        health.Enabled = true;
        health.Configured = true;
        health.Reachable = reachable;
        health.StatusMessage = reachable ? "Reachable" : Problem ?? "Not reachable now";
        return health;
    }

    /// <summary>The first series of a Metron list answer; null when it lists none.</summary>
    internal static MetronSeries? ParseSeries(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Int(item, "id") is not { } id)
            {
                continue;
            }

            var name = Text(item, "name") ?? Text(item, "series") ?? Text(item, "display_name");
            return new MetronSeries(id, name, Int(item, "volume"), Int(item, "year_began"));
        }

        return null;
    }

    private static bool Configured(AppSettings config)
        => config.EnableExternalSeriesMetadata
           && (!string.IsNullOrWhiteSpace(config.MetronApiToken)
               || (!string.IsNullOrWhiteSpace(config.MetronUsername) && !string.IsNullOrWhiteSpace(config.MetronPassword)));

    // The series lookup's address, after the account is checked: false when the address is not
    // https, or the account's sign-in was refused lately. A changed account starts afresh.
    private bool Prepare(AppSettings config, int comicVineVolumeId, out Uri url)
    {
        if (!Uri.TryCreate($"{config.MetronBaseUrl.TrimEnd('/')}/series/?cv_id={comicVineVolumeId.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute, out url!)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            Note(config, "The Metron address must be an https address, such as https://metron.cloud/api");
            return false;
        }

        var account = Account(config);
        lock (_lock)
        {
            // Answers belong to an account and address: another one asks again.
            if (_account != account)
            {
                _account = account;
                _answers.Clear();
                _problem = null;
                _pausedUntil = DateTimeOffset.MinValue;
                _probe = null;
            }

            // A refused sign-in is tried again after a while: the account may have been fixed on Metron.
            return !(_rejectedAccount == account && _time.GetUtcNow() - _rejectedAt < RetryRejectedSignIn);
        }
    }

    // One signed-in request; the parsed answer, or null after recording why there is none.
    private async Task<JsonDocument?> GetAsync(AppSettings config, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = string.IsNullOrWhiteSpace(config.MetronApiToken)
                ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.MetronUsername}:{config.MetronPassword}")))
                : new AuthenticationHeaderValue("Bearer", config.MetronApiToken.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                lock (_lock)
                {
                    _rejectedAccount = Account(config);
                    _rejectedAt = _time.GetUtcNow();
                }

                Fail(config, string.IsNullOrWhiteSpace(config.MetronApiToken)
                    ? "Metron refused the username and password: check them under Settings"
                    : "Metron refused the API token: check it under Settings");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // A 404 is not "no such series" (that is an empty list): the address is likely wrong.
                Pause(config, response.StatusCode == HttpStatusCode.NotFound
                    ? "Metron answered 404: check the Metron address"
                    : $"Metron answered {(int)response.StatusCode}");
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            lock (_lock)
            {
                _problem = null;
                _successes++;
                _lastSuccessUtc = _time.GetUtcNow().UtcDateTime;
            }

            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Pause(config, ex.Message);
            return null;
        }
    }

    // Under RequestsPerMinute in any minute and RequestsPerDay in a day, and not while paused. A
    // background run waits for the minute to free a permit; nothing waits for a day or a pause.
    private async Task<bool> TakePermitAsync(bool wait, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                if (now < _pausedUntil)
                {
                    return false;
                }

                var day = DateOnly.FromDateTime(now.UtcDateTime);
                if (day != _day)
                {
                    (_day, _today) = (day, 0);
                }

                if (_today >= RequestsPerDay)
                {
                    return false;
                }

                while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1))
                {
                    _recent.Dequeue();
                }

                if (_recent.Count < RequestsPerMinute)
                {
                    _recent.Enqueue(now);
                    _today++;
                    return true;
                }

                if (!wait)
                {
                    return false;
                }

                delay = _recent.Peek() + TimeSpan.FromMinutes(1) - now;
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), _time, cancellationToken);
        }
    }

    private void Pause(AppSettings config, string reason)
    {
        lock (_lock)
        {
            _pausedUntil = _time.GetUtcNow() + PauseAfterFailure;
        }

        Fail(config, reason);
    }

    private void Fail(AppSettings config, string reason)
    {
        lock (_lock)
        {
            _failures++;
            _lastFailureUtc = _time.GetUtcNow().UtcDateTime;
        }

        Note(config, reason);
    }

    private void Note(AppSettings config, string problem)
    {
        bool changed;
        lock (_lock)
        {
            _account = Account(config);
            changed = _problem != problem;
            _problem = problem;
        }

        // Once per problem, not on every request a run makes while it lasts.
        if (changed)
        {
            _logger.LogWarning("Metron is left out for now: {Reason}", LoggingHelper.SanitizeForLog(problem));
        }
    }

    private ProviderHealth Snapshot()
    {
        lock (_lock)
        {
            return new ProviderHealth
            {
                Name = ProviderName,
                LastError = _problem,
                LastSuccessUtc = _lastSuccessUtc,
                LastFailureUtc = _lastFailureUtc,
                SuccessCount = _successes,
                FailureCount = _failures,
            };
        }
    }

    private static bool Dispose(JsonDocument document)
    {
        document.Dispose();
        return true;
    }

    // Who is asked, and where: answers and a refused sign-in belong to it. The secrets are not kept.
    private static string Account(AppSettings config)
        => $"{config.MetronBaseUrl.TrimEnd('/')}|{config.MetronUsername}|{config.MetronPassword?.GetHashCode(StringComparison.Ordinal)}|{config.MetronApiToken?.Trim().GetHashCode(StringComparison.Ordinal)}";

    private static int? Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
