using ComicMaintainer.Core.Configuration;

namespace ComicMaintainer.Tests.Configuration;

public class AppSettingsTests
{
    [Fact]
    public void GetAllWatchedDirectories_WithWatchedDirectoriesPopulated_ReturnsWatchedDirectories()
    {
        // Arrange
        var settings = new AppSettings
        {
            WatchedDirectory = "/legacy",
            WatchedDirectories = new List<string> { "/dir1", "/dir2" }
        };

        // Act
        var result = settings.GetAllWatchedDirectories().ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("/dir1", result);
        Assert.Contains("/dir2", result);
        Assert.DoesNotContain("/legacy", result); // Should not include legacy property
    }

    [Fact]
    public void GetAllWatchedDirectories_WithEmptyWatchedDirectories_ReturnsSingleWatchedDirectory()
    {
        // Arrange
        var settings = new AppSettings
        {
            WatchedDirectory = "/legacy",
            WatchedDirectories = new List<string>()
        };

        // Act
        var result = settings.GetAllWatchedDirectories().ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("/legacy", result[0]);
    }

    [Fact]
    public void GetAllWatchedDirectories_WithNullWatchedDirectories_ReturnsSingleWatchedDirectory()
    {
        // Arrange - WatchedDirectories is initialized by default to empty list, but test the case
        var settings = new AppSettings
        {
            WatchedDirectory = "/legacy"
        };

        // Act
        var result = settings.GetAllWatchedDirectories().ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("/legacy", result[0]);
    }

    [Fact]
    public void AppSettings_DefaultValues_AreSet()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal("/watched_dir", settings.WatchedDirectory);
        Assert.NotNull(settings.WatchedDirectories);
        Assert.Empty(settings.WatchedDirectories);
        Assert.Equal("/duplicates", settings.DuplicateDirectory);
        Assert.Equal("/Config", settings.ConfigDirectory);
        Assert.Equal("{series} - Chapter {issue}", settings.FilenameFormat);
        Assert.Equal(4, settings.IssueNumberPadding);
        Assert.Equal(4, settings.MaxWorkers);
        Assert.Equal(10485760, settings.LogMaxBytes); // 10MB
        Assert.Equal(64, settings.DbCacheSizeMB);
        Assert.Equal(5000, settings.WebPort);
        Assert.Equal(99, settings.PUID);
        Assert.Equal(100, settings.PGID);
        Assert.Equal(30, settings.WatcherFileStabilityDelaySeconds);
        Assert.Equal(2, settings.WatcherDirectoryScanDelaySeconds);
        Assert.True(settings.WatcherEnableRename);
        Assert.True(settings.WatcherEnableNormalize);
        Assert.Equal(12, settings.DatabaseCleanupIntervalHours);
    }
}
