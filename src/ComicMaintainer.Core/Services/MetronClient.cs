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
/// password (HTTP Basic), always over https; a refused token falls back to the username and password
/// when they are set. Metron allows 20 requests a minute and 5,000 a day per account: this client
/// stays under 18 a minute (a background run waits for the next one; an interactive one does not)
/// and 4,900 a day, remembers each answer for a week (a series' volume number does not change, and
/// the answers are dropped when the credentials or address change), and after a refusal or failure
/// asks nothing for 10 minutes. A rejected sign-in stops it until the credentials or address change,
/// or for 6 hours, and is shown in the settings (<see cref="Problem"/>) and on the provider status
/// screen (<see cref="CheckHealthAsync"/>).
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
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClients;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<MetronClient> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, (MetronSeries? Series, DateTimeOffset At)> _answers = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private DateOnly _day;
    private int _today;
    private string? _account;
    private string? _rejectedAccount;
    private DateTimeOffset _rejectedAt;
    private string? _rejectedProblem;
    private string? _tokenRejectedAccount;
    private string? _problem;
    private bool? _reachable;
    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;
    private long _successes;
    private long _failures;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;

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
            var config = _settings.CurrentValue;
            lock (_lock)
            {
                return Configured(config) && _account == Account(config) ? _problem : null;
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
        var enabled = config.EnableExternalSeriesMetadata;
        var configured = Configured(config);
        if (!enabled || !configured)
        {
            var health = Snapshot(config);
            health.Enabled = enabled;
            health.Configured = configured;
            health.Reachable = null;
            health.LastError = null;
            health.StatusMessage = enabled ? "Missing API token (or username and password)" : "Disabled in settings";
            return health;
        }

        // A probe at most every 10 minutes, one at a time, so opening the screen costs next to nothing.
        // Every lookup in between tells as much: reachable follows the latest request of any kind.
        var notProbed = false;
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            bool due;
            lock (_lock)
            {
                due = _account != Account(config) || _time.GetUtcNow() - _probedAt >= ProbeInterval;
            }

            if (due && Prepare(config, ProbeVolumeId, out var url))
            {
                if (await TakePermitAsync(wait: false, cancellationToken))
                {
                    lock (_lock)
                    {
                        _probedAt = _time.GetUtcNow();
                    }

                    (await GetAsync(config, url, cancellationToken))?.Dispose();
                }
                else
                {
                    // Out of requests: not checked now, and nothing is concluded from that.
                    notProbed = true;
                }
            }
        }
        finally
        {
            _probeGate.Release();
        }

        var result = Snapshot(config);
        result.Enabled = true;
        result.Configured = true;
        lock (_lock)
        {
            var paused = _time.GetUtcNow() < _pausedUntil;
            result.RateLimited = notProbed || paused || _today >= RequestsPerDay;
            result.RateLimitedUntilUtc = paused ? _pausedUntil.UtcDateTime : null;
        }

        result.StatusMessage = result.Reachable switch
        {
            true => "Reachable",
            _ when result.LastError is { } problem => problem,
            _ when notProbed => "Request limit reached: not checked now",
            _ => "Not checked yet",
        };
        return result;
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

    /// <summary>
    /// The token as Metron expects it after <c>Bearer</c>: a <c>Bearer</c> or <c>Token</c> pasted
    /// in front of it, and surrounding spaces, taken off; null when there is none.
    /// </summary>
    internal static string? CleanToken(string? token)
    {
        var text = token?.Trim();
        foreach (var prefix in new[] { "Bearer ", "Token " })
        {
            if (text is not null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
            }
        }

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool HasPassword(AppSettings config)
        => !string.IsNullOrWhiteSpace(config.MetronUsername) && !string.IsNullOrWhiteSpace(config.MetronPassword);

    private static bool Configured(AppSettings config)
        => config.EnableExternalSeriesMetadata && (CleanToken(config.MetronApiToken) is not null || HasPassword(config));

    // The series lookup's address, after the account is checked: false when the address is not
    // https, or the account's sign-in was refused lately. A changed account starts afresh.
    private bool Prepare(AppSettings config, int comicVineVolumeId, out Uri url)
    {
        var account = Account(config);
        lock (_lock)
        {
            // Answers and what is known of Metron belong to an account and address: another one starts afresh.
            if (_account != account)
            {
                _account = account;
                _answers.Clear();
                _problem = null;
                _reachable = null;
                _probedAt = DateTimeOffset.MinValue;
                _pausedUntil = DateTimeOffset.MinValue;
            }
        }

        if (!Uri.TryCreate($"{config.MetronBaseUrl.TrimEnd('/')}/series/?cv_id={comicVineVolumeId.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute, out url!)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            lock (_lock)
            {
                _reachable = false;
            }

            Note(config, "The Metron address must be an https address, such as https://metron.cloud/api");
            return false;
        }

        lock (_lock)
        {
            // A refused sign-in is tried again after a while: the account may have been fixed on Metron.
            if (_rejectedAccount == account && _time.GetUtcNow() - _rejectedAt < RetryRejectedSignIn)
            {
                // Still why it is left out, after switching to another account and back.
                _problem ??= _rejectedProblem;
                _reachable = false;
                return false;
            }

            return true;
        }
    }

    // One signed-in request; the parsed answer, or null after recording why there is none. A refused
    // token is tried again once with the username and password, which are used from then on.
    private async Task<JsonDocument?> GetAsync(AppSettings config, Uri url, CancellationToken cancellationToken)
    {
        var account = Account(config);
        string? token;
        lock (_lock)
        {
            token = _tokenRejectedAccount == account ? null : CleanToken(config.MetronApiToken);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = token is null
                ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.MetronUsername}:{config.MetronPassword}")))
                : new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token is not null && HasPassword(config))
                {
                    lock (_lock)
                    {
                        _tokenRejectedAccount = account;
                    }

                    _logger.LogWarning("Metron refused the API token; signing in with the username and password instead");
                    return await GetAsync(config, url, cancellationToken);
                }

                var refused = token is null
                    ? "Metron refused the username and password: check them under Settings"
                    : "Metron refused the API token: check it under Settings";
                lock (_lock)
                {
                    _rejectedAccount = account;
                    _rejectedAt = _time.GetUtcNow();
                    _rejectedProblem = refused;
                }

                Fail(config, refused);
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
                _reachable = true;
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
            _reachable = false;
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

    // The status entry, with what is known of the account set now only.
    private ProviderHealth Snapshot(AppSettings config)
    {
        var current = Account(config);
        lock (_lock)
        {
            var ours = _account == current;
            return new ProviderHealth
            {
                Name = ProviderName,
                Reachable = ours ? _reachable : null,
                LastError = ours ? _problem : null,
                LastSuccessUtc = _lastSuccessUtc,
                LastFailureUtc = _lastFailureUtc,
                SuccessCount = _successes,
                FailureCount = _failures,
            };
        }
    }

    // Who is asked, and where: answers and a refused sign-in belong to it. The secrets are not kept.
    private static string Account(AppSettings config)
        => $"{config.MetronBaseUrl.TrimEnd('/')}|{config.MetronUsername}|{config.MetronPassword?.GetHashCode(StringComparison.Ordinal)}|{CleanToken(config.MetronApiToken)?.GetHashCode(StringComparison.Ordinal)}";

    private static int? Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
