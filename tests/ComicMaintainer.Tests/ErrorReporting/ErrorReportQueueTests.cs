using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Tests.Helpers;

namespace ComicMaintainer.Tests.ErrorReporting;

public class ErrorReportQueueTests
{
    [Fact]
    public async Task TryEnqueue_DeliversReportsInOrder()
    {
        var queue = new ErrorReportQueue(capacity: 8);

        Assert.True(queue.TryEnqueue(CreateReport("a")));
        Assert.True(queue.TryEnqueue(CreateReport("b")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var report in queue.ReadAllAsync(cts.Token))
        {
            received.Add(report.Fingerprint);
            if (received.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(new[] { "a", "b" }, received);
    }

    [Fact]
    public void TryEnqueue_DropsAndCountsOnceFullRatherThanGrowingUnbounded()
    {
        // An error storm on an already-unhealthy machine must not be able to
        // consume memory; the reports are all in the log files regardless.
        var queue = new ErrorReportQueue(capacity: 2);

        Assert.True(queue.TryEnqueue(CreateReport("a")));
        Assert.True(queue.TryEnqueue(CreateReport("b")));
        Assert.False(queue.TryEnqueue(CreateReport("c")));

        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public void ErrorReportFactory_RedactsBeforeTheReportLeavesTheProcess()
    {
        var settings = new AppSettings { SmtpPassword = "S3cretAppPassword" };
        var factory = new ErrorReportFactory(new TestOptionsMonitor<AppSettings>(settings));

        var report = factory.Create(
            "Error",
            "Failed to send {File}",
            "Failed to send /home/alice/comics/Saga 001.cbz for alice@example.com using S3cretAppPassword",
            new InvalidOperationException("rejected at /home/alice/comics/Saga 001.cbz"),
            "ComicMaintainer.Core.Services.ComicEmailService",
            "corr-1",
            DateTime.UtcNow);

        Assert.DoesNotContain("alice", report.RenderedMessage);
        Assert.DoesNotContain("S3cretAppPassword", report.RenderedMessage);
        Assert.DoesNotContain("alice", report.ExceptionMessage);
        Assert.Equal("System.InvalidOperationException", report.ExceptionType);
        Assert.False(string.IsNullOrEmpty(report.Fingerprint));
    }

    // Each secret alone and with no key name beside it, so only the literal list can mask it.
    // The path rule would blank most of a webhook URL but keep its last segment, which is the token.
    [Theory]
    [InlineData(nameof(AppSettings.NotifyNtfyToken), "Qw3rtyUiop1234", "Qw3rtyUiop1234")]
    [InlineData(nameof(AppSettings.NotifyGotifyToken), "Asdfgh5678Jklz", "Asdfgh5678Jklz")]
    [InlineData(nameof(AppSettings.NotifyDiscordWebhookUrl), "https://discord.example/api/webhooks/1/AbCdEf0123", "AbCdEf0123")]
    [InlineData(nameof(AppSettings.NotifyWebhookUrl), "https://hook.example/p/Zx9Yw8Vu7Ts", "Zx9Yw8Vu7Ts")]
    [InlineData(nameof(AppSettings.NotifyKomgaApiKey), "Mnbvcx0987Lkjh", "Mnbvcx0987Lkjh")]
    [InlineData(nameof(AppSettings.NotifyKavitaApiKey), "Poiuyt6543Rewq", "Poiuyt6543Rewq")]
    [InlineData(nameof(AppSettings.SuwayomiPassword), "Lkjhgf4321Bvcx", "Lkjhgf4321Bvcx")]
    public void ErrorReportFactory_MasksEachConfiguredSecret(string setting, string value, string mustNotAppear)
    {
        var settings = new AppSettings();
        typeof(AppSettings).GetProperty(setting)!.SetValue(settings, value);
        var factory = new ErrorReportFactory(new TestOptionsMonitor<AppSettings>(settings));

        var report = factory.Create("Error", "Send failed", $"The request to {value} was refused", null, "Svc", "corr-1", DateTime.UtcNow);

        Assert.DoesNotContain(mustNotAppear, report.RenderedMessage);
    }

    [Fact]
    public void ErrorReportFactory_TreatsThePlaceholderCorrelationIdAsAbsent()
    {
        var factory = new ErrorReportFactory(new TestOptionsMonitor<AppSettings>(new AppSettings()));

        // Serilog enriches every event with "-" when no request is in scope.
        var report = factory.Create("Error", "boom", "boom", null, "Svc", "-", DateTime.UtcNow);

        Assert.Null(report.CorrelationId);
    }

    private static ErrorReport CreateReport(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Level = "Error",
        MessageTemplate = "boom",
        RenderedMessage = "boom",
        TimestampUtc = DateTime.UtcNow
    };
}
