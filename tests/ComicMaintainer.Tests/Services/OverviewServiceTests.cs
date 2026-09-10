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
            // Brand-new series with only its initial issue -> Newly Added (NOT Updates).
            Entry("new-series", now.AddDays(-3), now.AddDays(-3), "/lib/new/1.cbz"),
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
    public async Task GetOverviewAsync_SeriesUpdates_IncludesRecentlyAddedSeriesThatReceivedANewIssue()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out _);

        var entries = new List<SeriesOverviewEntry>
        {
            // Series first appeared within the last 30 days but has since received
            // a newer issue. It must show up in Series Updates (and be retained by
            // its latest addition), not be locked to the Newly Added first-added
            // clock.
            Entry("new-then-updated", now.AddDays(-5), now.AddDays(-1),
                "/lib/nu/1.cbz", "/lib/nu/2.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        Assert.Contains(result.SeriesUpdates, c => c.Id == "new-then-updated");
    }

    [Fact]
    public async Task GetOverviewAsync_SeriesUpdates_StaysForThirtyDaysFromLastUpdateNotFirstAdded()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out _);

        var entries = new List<SeriesOverviewEntry>
        {
            // First appeared long ago; last update 20 days ago -> still within the
            // 30-day-from-last-update window.
            Entry("recently-updated", now.AddDays(-300), now.AddDays(-20),
                "/lib/ru/1.cbz", "/lib/ru/2.cbz"),
            // First appeared long ago; last update 40 days ago -> outside the
            // window and must fall off regardless of when it first appeared.
            Entry("expired-update", now.AddDays(-300), now.AddDays(-40),
                "/lib/eu/1.cbz", "/lib/eu/2.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        Assert.Contains(result.SeriesUpdates, c => c.Id == "recently-updated");
        Assert.DoesNotContain(result.SeriesUpdates, c => c.Id == "expired-update");
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

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_IncludesSeriesWithCompletedIssueAndRemainingIssues()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // user-1 finished issue 1 of a 3-issue series and hasn't started issue 2.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/ongoing/1.cbz",
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
            Entry("ongoing", now.AddDays(-200), now.AddDays(-100),
                "/lib/ongoing/1.cbz", "/lib/ongoing/2.cbz", "/lib/ongoing/3.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("ongoing", card.Id);
        // Resume target is the next unread issue, starting at the beginning.
        Assert.Equal("/lib/ongoing/2.cbz", card.ResumeFilePath);
        Assert.Equal(0, card.ResumePage);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_IncludesSeriesWhenLaterIssueCompletedButEarlierUnread()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // user-1 finished issue 3 (read out of order) but issues 1 and 2 remain
        // unread. The series must stay in Continue Reading, resuming at the
        // earliest unread issue.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/ooo/3.cbz",
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
            Entry("ooo", now.AddDays(-200), now.AddDays(-100),
                "/lib/ooo/1.cbz", "/lib/ooo/2.cbz", "/lib/ooo/3.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("ooo", card.Id);
        // Resume at the earliest unread issue since nothing follows the anchor.
        Assert.Equal("/lib/ooo/1.cbz", card.ResumeFilePath);
        Assert.Equal(0, card.ResumePage);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_ExcludesFullyCompletedSeries()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // Every issue in the series is completed -> nothing left to continue.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/finished/1.cbz",
                CurrentPage = 20,
                TotalPages = 20,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-2),
                CompletedAt = now.AddHours(-2)
            });
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/finished/2.cbz",
                CurrentPage = 18,
                TotalPages = 18,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-1),
                CompletedAt = now.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        var entries = new List<SeriesOverviewEntry>
        {
            Entry("finished", now.AddDays(-200), now.AddDays(-100),
                "/lib/finished/1.cbz", "/lib/finished/2.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");
        Assert.Empty(result.ContinueReading);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_DoesNotUseGlobalReadFlags()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // The user finished issue 1 in the reader. A global file read flag for
        // issue 2 must not affect this user's Continue Reading state.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/flagged/1.cbz",
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
            Entry("flagged", now.AddDays(-200), now.AddDays(-100),
                "/lib/flagged/1.cbz", "/lib/flagged/2.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");
        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("/lib/flagged/2.cbz", card.ResumeFilePath);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_DoesNotSkipIssuesMarkedReadGlobally()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // Issue 1 was completed by this user. A global read flag on issue 2
        // must not advance this user's resume position to issue 3.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/mixed/1.cbz",
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
            Entry("mixed", now.AddDays(-200), now.AddDays(-100),
                "/lib/mixed/1.cbz", "/lib/mixed/2.cbz", "/lib/mixed/3.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("mixed", card.Id);
        Assert.Equal("/lib/mixed/2.cbz", card.ResumeFilePath);
        Assert.Equal(0, card.ResumePage);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_ExcludesFullyReadSeries_WhenLastIssueReached100PercentWithoutCompletionMark()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // Issue 1 was explicitly completed. Issue 2 (the final issue) was read
        // to the last page -> 100% -> but never received an explicit completion
        // mark (CompletedAt is null), which can happen for the last issue of a
        // series read in webcomic mode. The whole series is effectively read and
        // must not appear on Continue Reading.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/done/1.cbz",
                CurrentPage = 20,
                TotalPages = 20,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-2),
                CompletedAt = now.AddHours(-2)
            });
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/done/2.cbz",
                CurrentPage = 18,
                TotalPages = 18,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-1),
                CompletedAt = null
            });
            await db.SaveChangesAsync();
        }

        var entries = new List<SeriesOverviewEntry>
        {
            Entry("done", now.AddDays(-200), now.AddDays(-100),
                "/lib/done/1.cbz", "/lib/done/2.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");
        Assert.Empty(result.ContinueReading);
    }

    [Fact]
    public async Task GetOverviewAsync_ContinueReading_PrefersInProgressIssueOverCompleted()
    {
        var now = DateTime.UtcNow;
        var factory = CreateFactory(out var options);

        // Issue 1 finished earlier; issue 2 is currently in progress.
        await using (var db = new ComicMaintainerDbContext(options))
        {
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/mix/1.cbz",
                CurrentPage = 20,
                TotalPages = 20,
                PercentComplete = 100,
                LastReadAt = now.AddHours(-3),
                CompletedAt = now.AddHours(-3)
            });
            db.ReadingProgresses.Add(new ReadingProgressEntity
            {
                UserId = "user-1",
                ContentId = "/lib/mix/2.cbz",
                CurrentPage = 7,
                TotalPages = 20,
                PercentComplete = 35,
                LastReadAt = now.AddMinutes(-10),
                CompletedAt = null
            });
            await db.SaveChangesAsync();
        }

        var entries = new List<SeriesOverviewEntry>
        {
            Entry("mix", now.AddDays(-200), now.AddDays(-100),
                "/lib/mix/1.cbz", "/lib/mix/2.cbz", "/lib/mix/3.cbz"),
        };

        var service = CreateService(entries, factory);

        var result = await service.GetOverviewAsync("user-1");

        var card = Assert.Single(result.ContinueReading);
        Assert.Equal("/lib/mix/2.cbz", card.ResumeFilePath);
        Assert.Equal(7, card.ResumePage);
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
