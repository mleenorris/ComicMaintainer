using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Unit tests for <see cref="UserPreferencesService"/> using an in-memory database.
/// </summary>
public class UserPreferencesServiceTests
{
    private readonly UserPreferencesService _service;

    public UserPreferencesServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _service = new UserPreferencesService(factory, NullLogger<UserPreferencesService>.Instance);
    }

    [Fact]
    public async Task GetPreferencesAsync_NoPreferencesSaved_ReturnsAllUnset()
    {
        var prefs = await _service.GetPreferencesAsync("user1");

        Assert.Equal("user1", prefs.UserId);
        Assert.Null(prefs.Theme);
        Assert.Null(prefs.PerPage);
        Assert.Null(prefs.ReadingMode);
        Assert.Null(prefs.LibraryViewMode);
        Assert.Null(prefs.FilterMode);
        Assert.Null(prefs.SortMode);
    }

    [Fact]
    public async Task SavePreferencesAsync_NewPreferences_CanBeRetrieved()
    {
        await _service.SavePreferencesAsync(new UserPreferences
        {
            UserId = "user1",
            Theme = "light",
            PerPage = 50,
            ReadingMode = "webcomic",
            LibraryViewMode = "series",
            FilterMode = "unmarked",
            SortMode = "date"
        });

        var retrieved = await _service.GetPreferencesAsync("user1");

        Assert.Equal("light", retrieved.Theme);
        Assert.Equal(50, retrieved.PerPage);
        Assert.Equal("webcomic", retrieved.ReadingMode);
        Assert.Equal("series", retrieved.LibraryViewMode);
        Assert.Equal("unmarked", retrieved.FilterMode);
        Assert.Equal("date", retrieved.SortMode);
    }

    [Fact]
    public async Task SavePreferencesAsync_PartialUpdate_LeavesOtherValuesIntact()
    {
        await _service.SavePreferencesAsync(new UserPreferences
        {
            UserId = "user1",
            Theme = "light",
            PerPage = 50
        });

        // Only the theme is supplied; PerPage must survive the merge.
        await _service.SavePreferencesAsync(new UserPreferences
        {
            UserId = "user1",
            Theme = "dark"
        });

        var retrieved = await _service.GetPreferencesAsync("user1");
        Assert.Equal("dark", retrieved.Theme);
        Assert.Equal(50, retrieved.PerPage);
    }

    [Fact]
    public async Task SavePreferencesAsync_ReturnsMergedResult()
    {
        await _service.SavePreferencesAsync(new UserPreferences { UserId = "user1", PerPage = 25 });

        var result = await _service.SavePreferencesAsync(new UserPreferences { UserId = "user1", Theme = "dark" });

        Assert.Equal("dark", result.Theme);
        Assert.Equal(25, result.PerPage);
    }

    [Fact]
    public async Task SavePreferencesAsync_IsScopedPerUser()
    {
        await _service.SavePreferencesAsync(new UserPreferences { UserId = "user1", Theme = "light" });
        await _service.SavePreferencesAsync(new UserPreferences { UserId = "user2", Theme = "dark" });

        Assert.Equal("light", (await _service.GetPreferencesAsync("user1")).Theme);
        Assert.Equal("dark", (await _service.GetPreferencesAsync("user2")).Theme);
    }

    [Fact]
    public async Task SavePreferencesAsync_SameUserTwice_DoesNotCreateDuplicateRows()
    {
        await _service.SavePreferencesAsync(new UserPreferences { UserId = "user1", Theme = "light" });
        await _service.SavePreferencesAsync(new UserPreferences { UserId = "user1", Theme = "dark" });

        var retrieved = await _service.GetPreferencesAsync("user1");
        Assert.Equal("dark", retrieved.Theme);
    }

    [Fact]
    public async Task GetPreferencesAsync_EmptyUserId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetPreferencesAsync(string.Empty));
    }

    [Fact]
    public async Task SavePreferencesAsync_EmptyUserId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SavePreferencesAsync(new UserPreferences { UserId = string.Empty }));
    }
}
