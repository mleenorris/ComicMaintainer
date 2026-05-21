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

    /// <summary>
    /// Default library view shown on initial page load.
    /// Supported values: "files" (folder/file list) or "series" (grouped series grid).
    /// </summary>
    public string DefaultLibraryView { get; set; } = "files";

    // External series metadata / alias enrichment
    public bool EnableExternalSeriesMetadata { get; set; }
    public string? ComicVineApiKey { get; set; }
    public string ComicVineBaseUrl { get; set; } = "https://comicvine.gamespot.com/api";

    // MangaDex metadata provider (manga and manhwa)
    public bool EnableMangaDexMetadata { get; set; }
    public string MangaDexBaseUrl { get; set; } = "https://api.mangadex.org";

    // AniList metadata provider (Manhwa and Manga)
    public bool EnableAniListMetadata { get; set; }
    public string AniListBaseUrl { get; set; } = "https://graphql.anilist.co";

    /// <summary>
    /// Client-side rate limit for MangaDex API requests. MangaDex enforces a
    /// global limit of 5 requests/second per IP; we default to 4/sec to leave
    /// headroom and avoid 429 responses.
    /// </summary>
    public int MangaDexRequestsPerSecond { get; set; } = 4;

    /// <summary>
    /// Client-side rate limit for AniList API requests. AniList's documented
    /// cap is 90 requests/minute, but the API is currently in a degraded
    /// state limited to 30/minute; we default to 28/min to leave headroom.
    /// </summary>
    public int AniListRequestsPerMinute { get; set; } = 28;

    /// <summary>
    /// When true (the default), external metadata refreshes also try to
    /// download a series cover image from the chosen provider and persist it
    /// under <see cref="SeriesImageCacheDirectory"/>. Set to false to keep
    /// metadata-only refreshes (e.g. for limited-bandwidth deployments).
    /// </summary>
    public bool DownloadExternalSeriesImages { get; set; } = true;

    /// <summary>
    /// Directory where downloaded and user-uploaded series cover images are
    /// stored. When empty, defaults to <c>{ConfigDirectory}/series-images</c>.
    /// Filenames inside this directory are derived from the normalized series
    /// key + a content hash; the directory is created lazily.
    /// </summary>
    public string? SeriesImageCacheDirectory { get; set; }

    /// <summary>
    /// Maximum size in bytes of a downloaded or uploaded series image.
    /// Defaults to 5 MiB; oversize payloads are rejected.
    /// </summary>
    public int SeriesImageMaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// When true (the default), every time a series cover image is set
    /// (user upload, provider apply, or background refresh download) a copy
    /// is also written into each on-disk folder that contains files for
    /// the series, named <c>cover.&lt;ext&gt;</c>. This lets external
    /// readers (Komga, Kavita, Calibre, file managers) display the same
    /// cover ComicMaintainer's UI shows. Set to false to leave series
    /// folders untouched.
    /// </summary>
    public bool WriteCoverToSeriesFolder { get; set; } = true;

    /// <summary>
    /// Default preferred language (one of <c>en</c>, <c>ja</c>, <c>ko</c>,
    /// <c>zh</c>, or null/empty for "no preference") applied when a series
    /// has no per-series override. When a matching localized title is
    /// available, the library will show it in place of the canonical title.
    /// </summary>
    public string? DefaultPreferredLanguage { get; set; }
}
