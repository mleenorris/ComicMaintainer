namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppSettings
{
    public string WatchedDirectory { get; set; } = "/watched_dir";
    public string DuplicateDirectory { get; set; } = "/duplicates";
    public string ConfigDirectory { get; set; } = "/Config";
    public string TempFileDirectory { get; set; } = "/Config/temp";
    public string FilenameFormat { get; set; } = "{series} - Chapter {issue}";
    public int IssueNumberPadding { get; set; } = 4;
    public int MaxWorkers { get; set; } = 4;
    public int LogMaxBytes { get; set; } = 10485760; // 10MB
    public int DbCacheSizeMB { get; set; } = 64;
    public string? BasePath { get; set; }
    public int WebPort { get; set; } = 5000;
    
    // User/Group settings for Docker
    public int PUID { get; set; } = 99;
    public int PGID { get; set; } = 100;
    
    // File watcher settings
    public int WatcherFileStabilityDelaySeconds { get; set; } = 30;
    public int WatcherDirectoryScanDelaySeconds { get; set; } = 2;
    public bool WatcherEnableRename { get; set; } = true;
    public bool WatcherEnableNormalize { get; set; } = true;
    
    // Database cleanup settings
    public int DatabaseCleanupIntervalHours { get; set; } = 12;

    // External series metadata / alias enrichment
    public bool EnableExternalSeriesMetadata { get; set; }
    public string? ComicVineApiKey { get; set; }
    public string ComicVineBaseUrl { get; set; } = "https://comicvine.gamespot.com/api";
}
