using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class OverviewServiceCoverageTests
{
    private readonly DbContextOptions<ComicMaintainerDbContext> _options =
        new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase($"overview-coverage-{Guid.NewGuid()}")
            .Options;

    private OverviewService CreateService(params SeriesOverviewEntry[] entries)
    {
        var library = new Mock<ISeriesLibraryService>();
        library
            .Setup(l => l.GetSeriesOverviewEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        return new OverviewService(library.Object, new DbFactory(_options), NullLogger<OverviewService>.Instance);
    }

    private static SeriesOverviewEntry Entry(string id, params string[] filePaths) => new()
    {
        Summary = new SeriesSummaryDto { Id = id, Title = id, CanonicalTitle = id },
        EarliestCreatedAt = DateTime.UtcNow.AddDays(-400),
        LatestCreatedAt = DateTime.UtcNow.AddDays(-300),
        FilePaths = filePaths.ToList()
    };

    private async Task SeedAsync(params ReadingProgressEntity[] progress)
    {
        await using var db = new ComicMaintainerDbContext(_options);
        db.ReadingProgresses.AddRange(progress);
        await db.SaveChangesAsync();
    }

    private static ReadingProgressEntity InProgress(string contentId, int page, DateTime lastReadAt) => new()
    {
        UserId = "user-1",
        ContentId = contentId,
        CurrentPage = page,
        TotalPages = 20,
        PercentComplete = page * 5,
        LastReadAt = lastReadAt
    };

    [Fact]
    public async Task ContinueReading_UsesTheMostRecentRecordWhenAPathWasRecordedUnderDifferentCasing()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            InProgress("/lib/dup/1.cbz", page: 3, now.AddHours(-2)),
            InProgress("/LIB/DUP/1.cbz", page: 9, now.AddMinutes(-10)),
            InProgress("/Lib/Dup/1.cbz", page: 5, now.AddHours(-1)));
        var service = CreateService(Entry("dup", "/lib/dup/1.cbz", "/lib/dup/2.cbz"));

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("/LIB/DUP/1.cbz", card.ResumeFilePath);
        Assert.Equal(9, card.ResumePage);
        Assert.Equal(now.AddMinutes(-10), card.LastReadUtc);
    }

    [Fact]
    public async Task ContinueReading_ResumesTheNextIssueAtItsSavedPageWhenItWasStartedEarlier()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/s/1.cbz",
                CurrentPage = 20,
                TotalPages = 20,
                PercentComplete = 100,
                LastReadAt = now.AddMinutes(-5),
                CompletedAt = now.AddMinutes(-5)
            },
            InProgress("/lib/s/2.cbz", page: 4, now.AddDays(-3)));
        var service = CreateService(Entry("s", "/lib/s/1.cbz", "/lib/s/2.cbz", "/lib/s/3.cbz"));

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("/lib/s/2.cbz", card.ResumeFilePath);
        Assert.Equal(4, card.ResumePage);
        Assert.Equal(now.AddMinutes(-5), card.LastReadUtc);
    }

    private sealed class DbFactory(DbContextOptions<ComicMaintainerDbContext> options)
        : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);
    }
}
