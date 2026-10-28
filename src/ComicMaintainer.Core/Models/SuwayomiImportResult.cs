namespace ComicMaintainer.Core.Models;

/// <summary>Outcome of one Suwayomi download import pass.</summary>
public sealed class SuwayomiImportResult
{
    /// <summary>
    /// Why the pass did not run (no folder configured, a folder is missing or
    /// overlaps the library, ...); null when it ran.
    /// </summary>
    public string? SkippedReason { get; init; }

    /// <summary>True when the pass was skipped because no downloads folder is configured.</summary>
    public bool NotConfigured { get; init; }

    /// <summary>Chapters moved into the library.</summary>
    public List<SuwayomiImportedChapter> Imported { get; } = new();

    /// <summary>Archives written too recently to be finished; retried on the next pass.</summary>
    public int StillDownloading { get; set; }

    /// <summary>Settled archives that could not be opened; left in place.</summary>
    public int Unreadable { get; set; }

    /// <summary>Archives that could not be moved (the log has details); left in place.</summary>
    public int Failed { get; set; }

    /// <summary>
    /// True when the pass stopped early because moving a file was refused
    /// for lack of permission, which usually affects every file alike.
    /// </summary>
    public bool PermissionDenied { get; set; }

    /// <summary>Archives directly in the downloads folder, with no series folder above them; left in place.</summary>
    public int WithoutSeriesFolder { get; set; }

    /// <summary>Empty folders removed from the downloads folder.</summary>
    public int RemovedFolders { get; set; }

    /// <summary>
    /// Chapter folders holding loose page images, meaning Suwayomi is not
    /// saving chapters as CBZ archives. These are not imported.
    /// </summary>
    public int ImageFolders { get; set; }

    public static SuwayomiImportResult Skipped(string reason, bool notConfigured = false)
        => new() { SkippedReason = reason, NotConfigured = notConfigured };
}

/// <summary>A chapter archive moved into the library.</summary>
/// <param name="SourcePath">Where Suwayomi saved the archive.</param>
/// <param name="DestinationPath">Where the archive now lives in the library.</param>
public sealed record SuwayomiImportedChapter(string SourcePath, string DestinationPath);
