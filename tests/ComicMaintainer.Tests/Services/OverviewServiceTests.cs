using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class OverviewServiceTests
{
    private static OverviewService CreateService(
        IReadOnlyList<SeriesOverviewEntry> entries,
        IDbContextFactory<ComicMaintainerDbContext> factory)
    {
        var library = new Mock<ISeriesLibraryService>();
        library
            .Setup(l => l.GetSeriesOverviewEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        return new OverviewService(
            library.Object,
            factory,
            Mock.Of<ILogger<OverviewService>>());
    }

    private static IDbContextFactory<ComicMaintainerDbContext> CreateFactory(out DbContextOptions<ComicMaintainerDbContext> options)
    {
        options = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OverviewTestDbContextFactory(options);
    }

    private static SeriesOverviewEntry Entry(
        string id,
        DateTime earliest,
        DateTime latest,
        params string[] filePaths)
        => new()
        {
            Summary = new SeriesSummaryDto { Id = id, Title = id, CanonicalTitle = id },
            EarliestCreatedAt = earliest,
            LatestCreatedAt = latest,
            FilePaths = filePaths.ToList()
        };

    [Fact]
    public async Task GetOverviewAsync_BucketsUpdatesNewlyAdded_RespectingThirtyDayWindow()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out _);

        var entries = new List<SeriesOverviewEntry>
        {
            // Brand-new series: earliest within window -> Newly Added (NOT Updates).
            Entry("new-series", now.AddDays(-3), now.AddDays(-1), "/lib/new/1.cbz"),
            // Existing series with a recent file -> Series Updates (earliest old).
            Entry("updated-series", now.AddDays(-200), now.AddDays(-2), "/lib/upd/2.cbz"),
            // Stale series: nothing recent -> neither.
            Entry("stale-series", now.AddDays(-400), now.AddDays(-90), "/lib/stale/1.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        Assert.Contains(result.NewlyAddedSeries, c => c.Id == "new-series");
        Assert.DoesNotContain(result.NewlyAddedSeries, c => c.Id == "updated-series");

        Assert.Contains(result.SeriesUpdates, c => c.Id == "updated-series");
        // Brand-new series must be excluded from Series Updates.
        Assert.DoesNotContain(result.SeriesUpdates, c => c.Id == "new-series");

        Assert.DoesNotContain(result.SeriesUpdates, c => c.Id == "stale-series");
        Assert.DoesNotContain(result.NewlyAddedSeries, c => c.Id == "stale-series");
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_IsPerUser()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // user-1 is reading file A; user-2 is reading file B.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/seriesA/1.cbz",
                CurrentPage = 5,
                TotalPages = 20,
                PercentComplete = 25,
                LastReadAt = now.AddHours(-1),
                CompletedAt = null
            });
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-2",
                ContentId = "/lib/seriesB/1.cbz",
                CurrentPage = 8,
                TotalPages = 20,
                PercentComplete = 40,
                LastReadAt = now.AddHours(-1),
                CompletedAt = null
            });
            await db.SaveChangesAsync();
        }

        var entries = new List<SeriesOverviewEntry>
        {
            Entry("seriesA", now.AddDays(-200), now.AddDays(-100), "/lib/seriesA/1.cbz"),
            Entry("seriesB", now.AddDays(-200), now.AddDays(-100), "/lib/seriesB/1.cbz"),
        };

        var service = CreateService(entries, factory);

        var forUser1 = await service.GetOverviewAsync("user-1");
        Assert.Contains(forUser1.ContinueReading, c => c.Id == "seriesA");
        Assert.DoesNotContain(forUser1.ContinueReading, c => c.Id == "seriesB");

        var resume = forUser1.ContinueReading.Single(c => c.Id == "seriesA");
        Assert.Equal("/lib/seriesA/1.cbz", resume.ResumeFilePath);
        Assert.Equal(5, resume.ResumePage);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_ExcludesCompleted()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/done/1.cbz",
                CurrentPage = 20,
                TotalPages = 20,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-1),
                CompletedAt = now.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        var entries = new List<SeriesOverviewEntry>
        {
            Entry("done-series", now.AddDays(-200), now.AddDays(-100), "/lib/done/1.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");
        Assert.Empty(result.ContinueReading);
    }
}

internal sealed class OverviewTestDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
{
    private readonly DbContextOptions<ComicMaintainerDbContext> _options;

    public OverviewTestDbContextFactory(DbContextOptions<ComicMaintainerDbContext> options)
    {
        _options = options;
    }

    public ComicMaintainerDbContext CreateDbContext() => new(_options);

    public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
