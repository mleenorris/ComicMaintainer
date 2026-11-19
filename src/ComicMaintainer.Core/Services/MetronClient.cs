using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="IMetronClient"/>. Metron allows 20 requests a minute and 5,000 a day per
/// account, with HTTP Basic sign-in over https. This client stays under 18 a minute (a background run
/// waits for the next one; an interactive one does not) and 4,900 a day, remembers each answer for a
/// week (a series' volume number does not change, and the answers are dropped when the account or
/// address changes), and after a refusal or failure asks nothing for 10 minutes. A rejected sign-in
/// stops it until the username, password or address change, and is shown in the settings
/// (<see cref="Problem"/>).
/// </summary>
public sealed class MetronClient : IMetronClient
{
    /// <summary>The name of the HTTP client registered for Metron.</summary>
    public const string HttpClientName = "Metron";

    internal const int RequestsPerMinute = 18;
    internal const int RequestsPerDay = 4900;
    private static readonly TimeSpan AnswerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(10);

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
    private string? _problem;

    public MetronClient(IHttpClientFactory httpClients, IOptionsMonitor<AppSettings> settings, ILogger<MetronClient> logger, TimeProvider time)
    {
        _httpClients = httpClients;
        _settings = settings;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var config = _settings.CurrentValue;
            return config.EnableExternalSeriesMetadata
                   && !string.IsNullOrWhiteSpace(config.MetronUsername)
                   && !string.IsNullOrWhiteSpace(config.MetronPassword);
        }
    }

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
        if (!Uri.TryCreate($"{config.MetronBaseUrl.TrimEnd('/')}/series/?cv_id={comicVineVolumeId.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            Note(config, "The Metron address must be an https address, such as https://metron.cloud/api");
            return MetronAnswer.Unavailable;
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
            }

            if (_rejectedAccount == account)
            {
                return MetronAnswer.Unavailable;
            }
        }

        if (_answers.TryGetValue(comicVineVolumeId, out var known) && _time.GetUtcNow() - known.At < AnswerLifetime)
        {
            return new MetronAnswer(true, known.Series);
        }

        if (!await TakePermitAsync(waitForPermit, cancellationToken))
        {
            return MetronAnswer.Unavailable;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.MetronUsername}:{config.MetronPassword}")));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                lock (_lock)
                {
                    _rejectedAccount = account;
                }

                Note(config, "Metron refused the username and password: check them under Settings");
                return MetronAnswer.Unavailable;
            }

            if (!response.IsSuccessStatusCode)
            {
                // A 404 is not "no such series" (that is an empty list): the address is likely wrong.
                Pause(config, response.StatusCode == HttpStatusCode.NotFound
                    ? "Metron answered 404: check the Metron address"
                    : $"Metron answered {(int)response.StatusCode}");
                return MetronAnswer.Unavailable;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var series = ParseSeries(document.RootElement);
            _answers[comicVineVolumeId] = (series, _time.GetUtcNow());
            lock (_lock)
            {
                _problem = null;
            }

            return new MetronAnswer(true, series);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Pause(config, ex.Message);
            return MetronAnswer.Unavailable;
        }
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

        Note(config, reason);
    }

    private void Note(AppSettings config, string problem)
    {
        lock (_lock)
        {
            _account = Account(config);
            _problem = problem;
        }

        _logger.LogWarning("Metron is left out for now: {Reason}", LoggingHelper.SanitizeForLog(problem));
    }

    // Who is asked, and where: answers and a refused sign-in belong to it. The password is not kept.
    private static string Account(AppSettings config)
        => $"{config.MetronBaseUrl.TrimEnd('/')}|{config.MetronUsername}|{config.MetronPassword?.GetHashCode(StringComparison.Ordinal)}";

    private static int? Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
