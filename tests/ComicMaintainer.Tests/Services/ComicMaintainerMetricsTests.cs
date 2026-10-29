using System.Diagnostics.Metrics;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class ComicMaintainerMetricsTests
{
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = new();

        public Recorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }
            lock (Measurements)
            {
                Measurements.Add((instrument.Name, value, dict));
            }
        }

        public void Collect() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void RecordsCountersHistogramsAndGauges()
    {
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new Recorder(metrics.Meter);

        metrics.RecordFileProcessed("Process", success: true);
        metrics.RecordFileProcessed("Rename", success: false);
        metrics.RecordScheduledJobRun("database-backup", "success", TimeSpan.FromSeconds(2));
        metrics.RecordProviderRequest("MangaDex", success: true);
        metrics.RecordProviderRateLimitWait("MangaDex", "server", TimeSpan.FromSeconds(5));
        metrics.RecordEmailSend(success: false);
        metrics.RecordDatabaseBackup(success: true);
        var emailQueue = new ComicEmailQueue(
            () => Mock.Of<IComicEmailService>(),
            Mock.Of<ILogger<ComicEmailQueue>>(),
            metrics);
        emailQueue.Enqueue(1);
        emailQueue.Enqueue(2);
        recorder.Collect();

        var m = recorder.Measurements;
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.files.processed" && (string?)x.Tags["outcome"] == "success" && (string?)x.Tags["operation"] == "Process");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.files.processed" && (string?)x.Tags["outcome"] == "failed");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.scheduled_job.runs" && (string?)x.Tags["job"] == "database-backup");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.scheduled_job.duration" && Math.Abs(x.Value - 2) < 0.001);
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.metadata_provider.requests" && (string?)x.Tags["provider"] == "MangaDex");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.metadata_provider.rate_limit_waits" && (string?)x.Tags["kind"] == "server");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.email.sends" && (string?)x.Tags["outcome"] == "failed");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.database.backups");
        Assert.Contains(m, x => x.Instrument == "comicmaintainer.email.queue_depth" && Math.Abs(x.Value - 2) < 0.001);
    }

    [Fact]
    public void MeterUsesWellKnownName()
    {
        using var metrics = new ComicMaintainerMetrics();
        Assert.Equal("ComicMaintainer", metrics.Meter.Name);
        Assert.Equal(ComicMaintainerMetrics.MeterName, metrics.Meter.Name);
    }
}
