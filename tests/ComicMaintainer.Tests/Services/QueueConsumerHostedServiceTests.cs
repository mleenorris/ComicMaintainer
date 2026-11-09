using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The hosted services are where a queue meets its consumer, so that neither has to be
/// constructed with the other.
/// </summary>
public class QueueConsumerHostedServiceTests
{
    [Fact]
    public async Task SeriesCoverArtifactQueueHostedService_StartsTheQueueWithItsWriterAndStopsIt()
    {
        var queue = new Mock<ISeriesCoverArtifactQueue>();
        var writer = Mock.Of<ISeriesCoverArtifactWriter>();
        var hosted = new SeriesCoverArtifactQueueHostedService(queue.Object, writer, NullLogger<SeriesCoverArtifactQueueHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        queue.Verify(q => q.StartAsync(writer, CancellationToken.None), Times.Once);
        queue.Verify(q => q.StopAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ComicEmailQueueHostedService_StartsTheQueueWithTheDeliveryServiceAndStopsIt()
    {
        var queue = new Mock<IComicEmailQueue>();
        var deliveryService = Mock.Of<IComicEmailService>();
        var hosted = new ComicEmailQueueHostedService(queue.Object, deliveryService, NullLogger<ComicEmailQueueHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        queue.Verify(q => q.StartAsync(deliveryService, CancellationToken.None), Times.Once);
        queue.Verify(q => q.StopAsync(CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ScheduledJobsHostedService_WorksThroughTheJobServiceInterface()
    {
        // The hosted service used to cast its IScheduledJobService to the concrete class, so a
        // decorator or any other implementation failed at construction.
        var jobs = new Mock<IScheduledJobService>();
        jobs.Setup(j => j.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ComicMaintainer.Core.Models.ScheduledJobView>());
        using var hosted = new ScheduledJobsHostedService(jobs.Object, NullLogger<ScheduledJobsHostedService>.Instance, Mock.Of<ComicMaintainer.Core.Notifications.INotificationPublisher>());

        await hosted.StartAsync(CancellationToken.None);
        var queued = await hosted.RunNowAsync("unknown-job");
        await hosted.StopAsync(CancellationToken.None);

        Assert.False(queued);
        jobs.Verify(j => j.GetHandler("unknown-job"), Times.Once);
    }
}
