using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Reader.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Reader;

/// <summary>
/// Unit tests for <see cref="ReaderPreferenceService"/> using an in-memory database.
/// </summary>
public class ReaderPreferenceServiceTests
{
    private readonly ReaderPreferenceService _service;

    public ReaderPreferenceServiceTests()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _service = new ReaderPreferenceService(factory, NullLogger<ReaderPreferenceService>.Instance);
    }

    [Fact]
    public async Task GetPreferencesAsync_NoPreferencesSaved_ReturnsDefaults()
    {
        var prefs = await _service.GetPreferencesAsync("user1");

        Assert.NotNull(prefs);
        Assert.Equal("user1", prefs.UserId);
        Assert.Equal(ReaderMode.SinglePage, prefs.DefaultReaderMode);
        Assert.Equal(ReadingDirection.LeftToRight, prefs.ReadingDirection);
        Assert.True(prefs.TapZonesEnabled);
        Assert.True(prefs.AutoHideChrome);
        Assert.Equal("width", prefs.FitPreference);
    }

    [Fact]
    public async Task SavePreferencesAsync_NewPreferences_CanBeRetrieved()
    {
        var prefs = new ReaderPreferences
        {
            UserId = "user1",
            DefaultReaderMode = ReaderMode.Longstrip,
            ReadingDirection = ReadingDirection.RightToLeft,
            TapZonesEnabled = false,
            AutoHideChrome = false,
            FitPreference = "height"
        };

        await _service.SavePreferencesAsync(prefs);

        var retrieved = await _service.GetPreferencesAsync("user1");
        Assert.NotNull(retrieved);
        Assert.Equal(ReaderMode.Longstrip, retrieved.DefaultReaderMode);
        Assert.Equal(ReadingDirection.RightToLeft, retrieved.ReadingDirection);
        Assert.False(retrieved.TapZonesEnabled);
        Assert.False(retrieved.AutoHideChrome);
        Assert.Equal("height", retrieved.FitPreference);
    }

    [Fact]
    public async Task SavePreferencesAsync_ExistingPreferences_UpdatesRecord()
    {
        await _service.SavePreferencesAsync(new ReaderPreferences
        {
            UserId = "user1",
            DefaultReaderMode = ReaderMode.SinglePage,
            FitPreference = "width"
        });

        await _service.SavePreferencesAsync(new ReaderPreferences
        {
            UserId = "user1",
            DefaultReaderMode = ReaderMode.FitHeight,
            FitPreference = "height"
        });

        var retrieved = await _service.GetPreferencesAsync("user1");
        Assert.Equal(ReaderMode.FitHeight, retrieved.DefaultReaderMode);
        Assert.Equal("height", retrieved.FitPreference);
    }

    [Fact]
    public async Task GetPreferencesAsync_DifferentUsers_AreSeparate()
    {
        await _service.SavePreferencesAsync(new ReaderPreferences { UserId = "user1", DefaultReaderMode = ReaderMode.SinglePage });
        await _service.SavePreferencesAsync(new ReaderPreferences { UserId = "user2", DefaultReaderMode = ReaderMode.Longstrip });

        var p1 = await _service.GetPreferencesAsync("user1");
        var p2 = await _service.GetPreferencesAsync("user2");

        Assert.Equal(ReaderMode.SinglePage, p1.DefaultReaderMode);
        Assert.Equal(ReaderMode.Longstrip, p2.DefaultReaderMode);
    }
}
