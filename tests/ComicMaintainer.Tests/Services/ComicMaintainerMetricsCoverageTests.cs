using System.Diagnostics.Metrics;
using ComicMaintainer.Core.Telemetry;

namespace ComicMaintainer.Tests.Services;

public class ComicMaintainerMetricsCoverageTests
{
    [Fact]
    public void RecordProcessingJob_RecordsTheDurationInSecondsTaggedWithOperationAndStatus()
    {
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new Recorder(metrics.Meter);

        metrics.RecordProcessingJob("ProcessAll", "Completed", TimeSpan.FromSeconds(3));

        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal("comicmaintainer.processing_job.duration", measurement.Instrument);
        Assert.Equal(3d, measurement.Value);
        Assert.Equal("ProcessAll", measurement.Tags["operation"]);
        Assert.Equal("Completed", measurement.Tags["status"]);
    }

    [Theory]
    [InlineData(true, "sent")]
    [InlineData(false, "failed")]
    public void RecordEmailSend_CountsOneSendTaggedWithItsOutcome(bool success, string expectedOutcome)
    {
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new Recorder(metrics.Meter);

        metrics.RecordEmailSend(success);

        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal("comicmaintainer.email.sends", measurement.Instrument);
        Assert.Equal(1d, measurement.Value);
        Assert.Equal(expectedOutcome, measurement.Tags["outcome"]);
    }

    [Theory]
    [InlineData(true, "success")]
    [InlineData(false, "failed")]
    public void RecordDatabaseBackup_CountsOneBackupTaggedWithItsOutcome(bool success, string expectedOutcome)
    {
        using var metrics = new ComicMaintainerMetrics();
        using var recorder = new Recorder(metrics.Meter);

        metrics.RecordDatabaseBackup(success);

        var measurement = Assert.Single(recorder.Measurements);
        Assert.Equal("comicmaintainer.database.backups", measurement.Instrument);
        Assert.Equal(1d, measurement.Value);
        Assert.Equal(expectedOutcome, measurement.Tags["outcome"]);
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();

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

        public IReadOnlyList<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return _measurements.ToList();
                }
            }
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, dict));
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
