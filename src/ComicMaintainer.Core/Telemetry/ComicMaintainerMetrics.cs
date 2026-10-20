using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ComicMaintainer.Core.Telemetry;

/// <summary>
/// Application-level metrics published on the <c>ComicMaintainer</c>
/// <see cref="Meter"/>. Uses only <c>System.Diagnostics.Metrics</c> so Core has no
/// OpenTelemetry dependency; the web host subscribes to <see cref="MeterName"/>
/// and exports the instruments (Prometheus and/or OTLP).
/// </summary>
/// <remarks>
/// Registered as a singleton. Services take it as an optional constructor
/// parameter so existing call sites (and tests) that do not supply one keep
/// working; every recording method is effectively free when nothing is listening.
/// Tag values are always drawn from small, fixed sets (outcome, operation name,
/// job key, provider name) to keep series cardinality bounded.
/// </remarks>
public sealed class ComicMaintainerMetrics : IDisposable
{
    public const string MeterName = "ComicMaintainer";

    /// <summary>
    /// Client-side throttle acquisitions shorter than this are not counted as a
    /// "wait" — acquiring an available permit takes microseconds.
    /// </summary>
    public static readonly TimeSpan RateLimitWaitThreshold = TimeSpan.FromMilliseconds(50);

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _filesProcessed;
    private readonly Histogram<double> _processingJobDuration;
    private readonly Counter<long> _scheduledJobRuns;
    private readonly Histogram<double> _scheduledJobDuration;
    private readonly Counter<long> _providerRequests;
    private readonly Counter<long> _providerRateLimitWaits;
    private readonly Histogram<double> _providerRateLimitWaitDuration;
    private readonly Counter<long> _emailSends;
    private readonly Counter<long> _databaseBackups;

    private Func<long>? _emailQueueDepthProvider;

    public ComicMaintainerMetrics(IMeterFactory? meterFactory = null)
    {
        if (meterFactory is not null)
        {
            _meter = meterFactory.Create(MeterName);
        }
        else
        {
            _meter = new Meter(MeterName);
            _ownsMeter = true;
        }

        _filesProcessed = _meter.CreateCounter<long>(
            "comicmaintainer.files.processed",
            unit: "{file}",
            description: "Comic files processed, tagged by operation and outcome (success/failed).");

        _processingJobDuration = _meter.CreateHistogram<double>(
            "comicmaintainer.processing_job.duration",
            unit: "s",
            description: "Wall-clock duration of batch processing jobs, tagged by operation and final status.");

        _scheduledJobRuns = _meter.CreateCounter<long>(
            "comicmaintainer.scheduled_job.runs",
            unit: "{run}",
            description: "Scheduled job runs, tagged by job key and outcome.");

        _scheduledJobDuration = _meter.CreateHistogram<double>(
            "comicmaintainer.scheduled_job.duration",
            unit: "s",
            description: "Duration of scheduled job runs, tagged by job key and outcome.");

        _providerRequests = _meter.CreateCounter<long>(
            "comicmaintainer.metadata_provider.requests",
            unit: "{request}",
            description: "External metadata provider lookups, tagged by provider and outcome.");

        _providerRateLimitWaits = _meter.CreateCounter<long>(
            "comicmaintainer.metadata_provider.rate_limit_waits",
            unit: "{wait}",
            description: "Provider requests that waited on a rate limit (kind=client throttle or kind=server HTTP 429 back-off).");

        _providerRateLimitWaitDuration = _meter.CreateHistogram<double>(
            "comicmaintainer.metadata_provider.rate_limit_wait.duration",
            unit: "s",
            description: "Time spent waiting on provider rate limits.");

        _emailSends = _meter.CreateCounter<long>(
            "comicmaintainer.email.sends",
            unit: "{email}",
            description: "Ereader email deliveries attempted, tagged by outcome (sent/failed).");

        _meter.CreateObservableGauge(
            "comicmaintainer.email.queue_depth",
            () => _emailQueueDepthProvider?.Invoke() ?? 0L,
            unit: "{delivery}",
            description: "Email deliveries waiting in the in-memory send queue.");

        _databaseBackups = _meter.CreateCounter<long>(
            "comicmaintainer.database.backups",
            unit: "{backup}",
            description: "Database backups created, tagged by outcome (success/failed).");
    }

    /// <summary>The underlying meter (exposed for tests and listeners).</summary>
    public Meter Meter => _meter;

    public void RecordFileProcessed(string operation, bool success) =>
        _filesProcessed.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failed"));

    public void RecordProcessingJob(string operation, string status, TimeSpan duration) =>
        _processingJobDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("status", status));

    public void RecordScheduledJobRun(string jobKey, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "job", jobKey },
            { "outcome", outcome }
        };
        _scheduledJobRuns.Add(1, tags);
        _scheduledJobDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordProviderRequest(string provider, bool success) =>
        _providerRequests.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failed"));

    /// <param name="kind"><c>client</c> (local throttle) or <c>server</c> (HTTP 429 back-off).</param>
    public void RecordProviderRateLimitWait(string provider, string kind, TimeSpan waited)
    {
        var tags = new TagList
        {
            { "provider", provider },
            { "kind", kind }
        };
        _providerRateLimitWaits.Add(1, tags);
        _providerRateLimitWaitDuration.Record(waited.TotalSeconds, tags);
    }

    public void RecordEmailSend(bool success) =>
        _emailSends.Add(1, new KeyValuePair<string, object?>("outcome", success ? "sent" : "failed"));

    public void RecordDatabaseBackup(bool success) =>
        _databaseBackups.Add(1, new KeyValuePair<string, object?>("outcome", success ? "success" : "failed"));

    /// <summary>Supplies the callback read by the email queue-depth gauge.</summary>
    public void SetEmailQueueDepthProvider(Func<long> provider) => _emailQueueDepthProvider = provider;

    public void Dispose()
    {
        // Meters created by an IMeterFactory are owned (and disposed) by the factory.
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
