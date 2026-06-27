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

    /// <summary>
    /// Size (in kilobytes) of the FileSystemWatcher internal buffer. A larger buffer lets the
    /// watcher hold more pending file-system events before the OS drops them, which is important
    /// when a large batch of files arrives at once. The OS caps this at 64 KB and the default of
    /// 8 KB overflows easily under bursty loads. Overflows are still recovered via a directory
    /// rescan, but a larger buffer avoids the overflow in the first place.
    /// </summary>
    public int WatcherInternalBufferSizeKB { get; set; } = 64;
    
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
    /// When true, every time a series cover image is set (user upload,
    /// provider apply, or background refresh download) a copy is also
    /// embedded inside the <em>first</em> comic archive of the series as a
    /// <c>cover.&lt;ext&gt;</c> entry. This lets readers that derive the
    /// series cover from the first issue's archive contents (rather than a
    /// sidecar file) display the same cover ComicMaintainer's UI shows.
    ///
    /// <para>Defaults to <c>false</c> because, unlike
    /// <see cref="WriteCoverToSeriesFolder"/>, this rewrites the comic
    /// archive in place. The write is idempotent (skipped when the archive
    /// already contains a byte-identical cover) and only ever targets
    /// writable CBZ archives.</para>
    /// </summary>
    public bool WriteCoverToFirstArchive { get; set; } = false;

    /// <summary>
    /// Default preferred language (one of <c>en</c>, <c>ja</c>, <c>ko</c>,
    /// <c>zh</c>, or null/empty for "no preference") applied when a series
    /// has no per-series override. When a matching localized title is
    /// available, the library will show it in place of the canonical title.
    /// </summary>
    public string? DefaultPreferredLanguage { get; set; }

    /// <summary>
    /// Hard cap on the number of comic archives a single
    /// <c>GetSeriesIssuesAsync</c> call is allowed to open from disk to
    /// upgrade missing <c>Title</c> / <c>Issue</c> metadata. The upgrade
    /// path is best-effort and only runs for issues whose cached DTO is
    /// missing both fields, so most requests do zero disk reads. This cap
    /// protects the request from pathological cases (e.g. a 1000-issue
    /// series with no cached metadata) where the upgrade loop would
    /// otherwise dominate the request latency. Default 100 matches the
    /// default frontend page size.
    /// </summary>
    public int SeriesIssuesMaxArchiveUpgrades { get; set; } = 100;

    /// <summary>
    /// When true (the default), the server caches the cover (page 1) bytes of
    /// every comic file it serves to the UI on disk under
    /// <see cref="FileCoverCacheDirectory"/>. Cached entries are validated
    /// against the source file's last-write-time and length on every request,
    /// so changes to the underlying archive automatically invalidate the
    /// cache. Set to false to bypass the cache and always extract page 1
    /// from the archive on demand.
    /// </summary>
    public bool FileCoverCacheEnabled { get; set; } = true;

    /// <summary>
    /// Directory where per-file cover thumbnails (page 1 of each comic
    /// archive) are cached. When empty, defaults to
    /// <c>{ConfigDirectory}/file-covers</c>. Filenames inside this directory
    /// are derived from a hash of the absolute file path; the directory is
    /// created lazily.
    /// </summary>
    public string? FileCoverCacheDirectory { get; set; }

    /// <summary>
    /// Soft cap on the total size of the file-cover cache directory in
    /// megabytes. When exceeded, the oldest entries (by last-access time)
    /// are pruned opportunistically on cache writes. Defaults to 512 MiB.
    /// </summary>
    public int FileCoverCacheMaxMegabytes { get; set; } = 512;
}
