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
/// account, with HTTP Basic sign-in; this client stays under 18 a minute, remembers each answer for
/// a week (a series' volume number does not change), and after a refusal (429), a failure or a
/// rejected sign-in, asks nothing for 10 minutes.
/// </summary>
public sealed class MetronClient : IMetronClient
{
    /// <summary>The name of the HTTP client registered for Metron.</summary>
    public const string HttpClientName = "Metron";

    internal const int RequestsPerMinute = 18;
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
    public async Task<MetronAnswer> SeriesOfComicVineVolumeAsync(int comicVineVolumeId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || comicVineVolumeId <= 0)
        {
            return MetronAnswer.Unavailable;
        }

        var now = _time.GetUtcNow();
        if (_answers.TryGetValue(comicVineVolumeId, out var known) && now - known.At < AnswerLifetime)
        {
            return new MetronAnswer(true, known.Series);
        }

        if (!TryTakePermit(now))
        {
            return MetronAnswer.Unavailable;
        }

        var config = _settings.CurrentValue;
        var url = $"{config.MetronBaseUrl.TrimEnd('/')}/series/?cv_id={comicVineVolumeId.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.MetronUsername}:{config.MetronPassword}")));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Remember(comicVineVolumeId, null, now);
            }

            if (!response.IsSuccessStatusCode)
            {
                Pause(now, $"Metron answered {(int)response.StatusCode}");
                return MetronAnswer.Unavailable;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return Remember(comicVineVolumeId, ParseSeries(document.RootElement), now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Pause(now, ex.Message);
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

    private MetronAnswer Remember(int comicVineVolumeId, MetronSeries? series, DateTimeOffset now)
    {
        _answers[comicVineVolumeId] = (series, now);
        return new MetronAnswer(true, series);
    }

    // Under RequestsPerMinute in any minute, and not while paused.
    private bool TryTakePermit(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (now < _pausedUntil)
            {
                return false;
            }

            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1))
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= RequestsPerMinute)
            {
                return false;
            }

            _recent.Enqueue(now);
            return true;
        }
    }

    private void Pause(DateTimeOffset now, string reason)
    {
        lock (_lock)
        {
            _pausedUntil = now + PauseAfterFailure;
        }

        _logger.LogWarning("Metron is not asked for 10 minutes: {Reason}", LoggingHelper.SanitizeForLog(reason));
    }

    private static int? Int(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
