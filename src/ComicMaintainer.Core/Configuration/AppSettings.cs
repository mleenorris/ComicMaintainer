namespace ComicMaintainer.Core.Configuration;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppSettings
{
    public string WatchedDirectory { get; set; } = "/watched_dir";
    public string DuplicateDirectory { get; set; } = "/duplicates";
    public string ConfigDirectory { get; set; } = "/Config";
    public string FilenameFormat { get; set; } = "{series} - Chapter {issue}";
    public int IssueNumberPadding { get; set; } = 4;
    public int MaxWorkers { get; set; } = 4;
    public bool WatcherEnabled { get; set; } = true;
    public int LogMaxBytes { get; set; } = 10485760; // 10MB
    public int DbCacheSizeMB { get; set; } = 64;
    public string? BasePath { get; set; }
    public int WebPort { get; set; } = 5000;
    
    // User/Group settings for Docker
    public int PUID { get; set; } = 99;
    public int PGID { get; set; } = 100;
    
    // GitHub integration settings
    public string? GitHubToken { get; set; }
    public string? GitHubRepository { get; set; }
    public string? GitHubIssueAssignee { get; set; }
    
    // File watcher settings
    public int WatcherFileStabilityDelaySeconds { get; set; } = 30;
    public int WatcherDirectoryScanDelaySeconds { get; set; } = 2;
}
