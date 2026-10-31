namespace ComicMaintainer.Core.Models;

/// <summary>
/// Result of probing the configured Suwayomi server.
/// </summary>
public class SuwayomiConnectionStatus
{
    /// <summary>True when the Suwayomi integration is enabled in settings.</summary>
    public bool Enabled { get; set; }

    /// <summary>True when a base URL has been configured.</summary>
    public bool Configured { get; set; }

    /// <summary>True when the server answered the probe successfully.</summary>
    public bool Connected { get; set; }

    /// <summary>Server name reported by Suwayomi (e.g. "Suwayomi-Server").</summary>
    public string? ServerName { get; set; }

    /// <summary>Server version reported by Suwayomi.</summary>
    public string? Version { get; set; }

    /// <summary>Human readable failure reason when <see cref="Connected"/> is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// A manga/series entry in the Suwayomi library.
/// </summary>
public class SuwayomiSeries
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool InLibrary { get; set; }

    /// <summary>Source display name when the series came from a source search.</summary>
    public string? Source { get; set; }

    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// A chapter belonging to a <see cref="SuwayomiSeries"/>.
/// </summary>
public class SuwayomiChapter
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double ChapterNumber { get; set; }
    public bool IsDownloaded { get; set; }
}

/// <summary>
/// Outcome of asking Suwayomi to download the issues a series is missing.
/// </summary>
public class SuwayomiDownloadResult
{
    /// <summary>
    /// The first Suwayomi series that supplied a matching chapter, or the
    /// first matching series found when no requested issue was available.
    /// </summary>
    public SuwayomiSeries? Series { get; set; }

    /// <summary>Chapters that were added to the Suwayomi download queue.</summary>
    public List<SuwayomiChapter> QueuedChapters { get; set; } = new();

    /// <summary>
    /// Requested issue numbers that Suwayomi has no (non-downloaded) chapter
    /// for, so nothing could be queued for them.
    /// </summary>
    public List<int> UnmatchedIssueNumbers { get; set; } = new();

    /// <summary>
    /// Requested issue numbers whose chapter is already downloaded on the
    /// Suwayomi side (so it was not queued again).
    /// </summary>
    public List<int> AlreadyDownloadedIssueNumbers { get; set; } = new();

    /// <summary>
    /// Installed sources or matching source entries that could not be checked.
    /// Other sources may still have supplied downloads.
    /// </summary>
    public List<SuwayomiDownloadFailure> DiscoveryFailures { get; set; } = new();
}

/// <summary>
/// One non-fatal failure while looking across Suwayomi's installed sources
/// for a missing issue.
/// </summary>
public class SuwayomiDownloadFailure
{
    /// <summary>Source or series that could not be checked.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Human-readable reason the check failed.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// A catalogue source on the Suwayomi server: one site, in one language, of
/// an installed extension.
/// </summary>
public class SuwayomiSource
{
    /// <summary>
    /// Source id. Suwayomi ids are 64-bit numbers, kept as a string so
    /// JavaScript clients do not round them.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Site name, such as "MangaDex".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Language code, such as "en", or "all" for multi-language sources.</summary>
    public string Lang { get; set; } = string.Empty;

    /// <summary>Name with the language, such as "MangaDex (EN)".</summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// One page of series a Suwayomi source found for a search.
/// </summary>
public class SuwayomiSourceSearchResult
{
    /// <summary>The series found, best title match first.</summary>
    public List<SuwayomiSeries> Series { get; set; } = new();

    /// <summary>True when the source has further pages of results.</summary>
    public bool HasNextPage { get; set; }
}

/// <summary>
/// Outcome of adding a series to the Suwayomi library.
/// </summary>
public class SuwayomiAddToLibraryResult
{
    /// <summary>The series as Suwayomi lists it after the add.</summary>
    public SuwayomiSeries Series { get; set; } = new();

    /// <summary>True when the series was already in the Suwayomi library.</summary>
    public bool AlreadyInLibrary { get; set; }
}

/// <summary>
/// Outcome of queueing the chapters of a Suwayomi series that are neither
/// downloaded on the Suwayomi side nor already in the local library.
/// </summary>
public class SuwayomiSeriesDownloadResult
{
    /// <summary>Chapters Suwayomi lists for the series.</summary>
    public int ChapterCount { get; set; }

    /// <summary>Chapters that were added to the Suwayomi download queue.</summary>
    public List<SuwayomiChapter> QueuedChapters { get; set; } = new();

    /// <summary>Chapter numbers skipped because the local library has that issue.</summary>
    public int AlreadyInLocalLibraryCount { get; set; }

    /// <summary>Chapter numbers skipped because Suwayomi has already downloaded them.</summary>
    public int AlreadyDownloadedCount { get; set; }

    /// <summary>
    /// Chapters skipped because another chapter with the same number (usually
    /// another scanlation) was queued instead.
    /// </summary>
    public int DuplicateChapterCount { get; set; }

    /// <summary>
    /// Chapters skipped because they have no chapter number (extras,
    /// announcements) while the series has numbered chapters.
    /// </summary>
    public int UnnumberedChapterCount { get; set; }
}

/// <summary>
/// Outcome of adding a series to the Suwayomi library and queueing the
/// chapters the local library is missing.
/// </summary>
public class SuwayomiAddSeriesResult
{
    /// <summary>The series as Suwayomi lists it after the add.</summary>
    public SuwayomiSeries Series { get; set; } = new();

    /// <summary>True when the series was already in the Suwayomi library.</summary>
    public bool AlreadyInLibrary { get; set; }

    /// <summary>
    /// Title of the local series whose issues were left out of the queue, or
    /// null when no local series has the Suwayomi title.
    /// </summary>
    public string? LocalSeriesTitle { get; set; }

    /// <summary>What was queued, and why the other chapters were not.</summary>
    public SuwayomiSeriesDownloadResult Downloads { get; set; } = new();
}

/// <summary>
/// A series cover fetched from the Suwayomi server.
/// </summary>
public sealed record SuwayomiThumbnail(byte[] Content, string ContentType);

/// <summary>
/// Raised when a Suwayomi request cannot be completed (not configured,
/// unreachable, or the server returned an error).
/// </summary>
public class SuwayomiException : Exception
{
    public SuwayomiException(string message) : base(message)
    {
    }

    public SuwayomiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
