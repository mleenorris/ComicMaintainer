using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Reader;

/// <summary>
/// Unit tests for <see cref="ReadingSessionService"/> using an in-memory database.
/// </summary>
public class ReadingSessionServiceTests
{
    private readonly ReadingSessionService _sessionService;
    private readonly ReadingProgressService _progressService;

    public ReadingSessionServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _progressService = new ReadingProgressService(factory, NullLogger<ReadingProgressService>.Instance);
        _sessionService = new ReadingSessionService(factory, _progressService, NullLogger<ReadingSessionService>.Instance);
    }

    [Fact]
    public async Task StartSessionAsync_ReturnsSessionWithCorrectData()
    {
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 3, incognito: false);

        Assert.NotNull(session);
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal("user1", session.UserId);
        Assert.Equal("content1", session.ContentId);
        Assert.Equal(3, session.StartPage);
        Assert.False(session.Incognito);
        Assert.Null(session.EndedAt);
        Assert.Null(session.EndPage);
    }

    [Fact]
    public async Task StartSessionAsync_Incognito_FlagsSessionCorrectly()
    {
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 1, incognito: true);

        Assert.True(session.Incognito);
    }

    [Fact]
    public async Task EndSessionAsync_UnknownSessionId_ReturnsNull()
    {
        var result = await _sessionService.EndSessionAsync(Guid.NewGuid(), endPage: 5);
        Assert.Null(result);
    }

    [Fact]
    public async Task EndSessionAsync_ValidSession_SetsEndedAtAndEndPage()
    {
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 1);

        var ended = await _sessionService.EndSessionAsync(session.SessionId, endPage: 8);

        Assert.NotNull(ended);
        Assert.Equal(8, ended.EndPage);
        Assert.NotNull(ended.EndedAt);
    }

    [Fact]
    public async Task EndSessionAsync_NonIncognito_DoesNotUpdateProgressWhenNoExistingProgress()
    {
        // No prior progress saved → session end should not crash and no progress row is created
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 1, incognito: false);
        var ended = await _sessionService.EndSessionAsync(session.SessionId, endPage: 5);

        Assert.NotNull(ended);

        // No existing progress was present, so no progress row should have been created by the session
        var progress = await _progressService.GetProgressAsync("user1", "content1");
        Assert.Null(progress);
    }

    [Fact]
    public async Task EndSessionAsync_NonIncognito_AdvancesProgressWhenEndPageIsHigher()
    {
        // Seed durable progress at page 3
        await _progressService.SaveProgressAsync(new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 3,
            TotalPages = 20,
            PercentComplete = 15.0,
            LastReadAt = DateTime.UtcNow
        });

        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 3, incognito: false);
        await _sessionService.EndSessionAsync(session.SessionId, endPage: 10);

        var progress = await _progressService.GetProgressAsync("user1", "content1");
        Assert.NotNull(progress);
        Assert.Equal(10, progress.CurrentPage);
    }

    [Fact]
    public async Task EndSessionAsync_NonIncognito_DoesNotRegressProgressWhenEndPageIsLower()
    {
        // Seed durable progress at page 15
        await _progressService.SaveProgressAsync(new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 15,
            TotalPages = 20,
            PercentComplete = 75.0,
            LastReadAt = DateTime.UtcNow
        });

        // Session ends at page 5 (user went back) – should NOT overwrite
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 10, incognito: false);
        await _sessionService.EndSessionAsync(session.SessionId, endPage: 5);

        var progress = await _progressService.GetProgressAsync("user1", "content1");
        Assert.NotNull(progress);
        Assert.Equal(15, progress.CurrentPage);
    }

    [Fact]
    public async Task EndSessionAsync_Incognito_DoesNotOverwriteDurableProgress()
    {
        // Seed durable progress at page 5
        await _progressService.SaveProgressAsync(new ReadingProgress
        {
            UserId = "user1",
            ContentId = "content1",
            CurrentPage = 5,
            TotalPages = 20,
            PercentComplete = 25.0,
            LastReadAt = DateTime.UtcNow
        });

        // Incognito session reads ahead to page 18
        var session = await _sessionService.StartSessionAsync("user1", "content1", startPage: 5, incognito: true);
        await _sessionService.EndSessionAsync(session.SessionId, endPage: 18);

        // Durable progress must remain unchanged at page 5
        var progress = await _progressService.GetProgressAsync("user1", "content1");
        Assert.NotNull(progress);
        Assert.Equal(5, progress.CurrentPage);
    }
}
