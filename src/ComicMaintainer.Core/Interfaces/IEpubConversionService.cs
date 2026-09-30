namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Extra presentation inputs for an EPUB conversion. All members are optional;
/// the converter falls back to the archive's own content/metadata when they are
/// not supplied.
/// </summary>
/// <param name="SeriesImagePath">
/// Absolute path of a cached series cover image to embed as the book cover.
/// Ignored when the file is missing or is not a decodable image.
/// </param>
/// <param name="SeriesTitle">
/// Display name of the series, used for the title and the EPUB collection
/// metadata when the archive's ComicInfo.xml has no (or a worse) series name.
/// </param>
/// <param name="MaxSizeBytes">
/// Optional size budget for the generated book. When the first (lossless) pass
/// exceeds it, the converter retries with progressively stronger page
/// compression so the book can still be delivered. The budget is best-effort:
/// if even the smallest variant is over it, that variant is returned and the
/// caller decides what to do.
/// </param>
/// <param name="Title">
/// Overrides the title derived from the archive metadata. Used by condensed
/// (multi-issue) books, whose title spans a range of issues.
/// </param>
/// <param name="OutputFileName">
/// File name (without extension) for the generated book. Defaults to the name
/// of the first source archive. Invalid file name characters are replaced.
/// </param>
/// <param name="Progress">
/// Optional sink notified as the conversion advances. A book condensed from a
/// large selection takes minutes to build, so callers that surface a status to
/// the user need to observe it while it runs. Reports are frequent (one per
/// page), synchronous and best-effort: an implementation must be cheap and must
/// not throw.
/// </param>
public record EpubConversionOptions(
    string? SeriesImagePath = null,
    string? SeriesTitle = null,
    long? MaxSizeBytes = null,
    string? Title = null,
    string? OutputFileName = null,
    IProgress<EpubConversionProgress>? Progress = null);

/// <summary>Stage a conversion is currently in.</summary>
public enum EpubConversionPhase
{
    /// <summary>Opening the source archives to index their pages and metadata.</summary>
    Reading,

    /// <summary>Writing pages into the book.</summary>
    Writing,

    /// <summary>Re-reading the finished archive to verify it is complete.</summary>
    Validating,

    /// <summary>
    /// The book is over its size budget and is being rebuilt with stronger page
    /// compression. <see cref="EpubConversionProgress.Pass"/> increases and the
    /// page counter restarts.
    /// </summary>
    Recompressing
}

/// <summary>
/// A snapshot of an in-flight conversion.
/// </summary>
/// <param name="Phase">What the converter is currently doing.</param>
/// <param name="CompletedPages">Pages written so far in the current pass.</param>
/// <param name="TotalPages">
/// Pages the book will contain, or 0 while the sources are still being indexed.
/// </param>
/// <param name="CompletedIssues">Issues fully handled so far in the current pass.</param>
/// <param name="TotalIssues">Issues the book spans.</param>
/// <param name="CurrentIssue">File name of the issue being read, if any.</param>
/// <param name="Pass">1-based index of the compression pass being built.</param>
/// <param name="TotalPasses">
/// Passes that may be attempted. Greater than 1 only when a size budget is set,
/// and later passes only run when the book is still too large.
/// </param>
public record EpubConversionProgress(
    EpubConversionPhase Phase,
    int CompletedPages,
    int TotalPages,
    int CompletedIssues,
    int TotalIssues,
    string? CurrentIssue,
    int Pass,
    int TotalPasses);

/// <summary>
/// Converts comic archives (CBZ/CBR) into fixed-layout EPUB3 files suitable for
/// ereaders that cannot open comic archives directly (e.g. Kindle, Kobo).
/// </summary>
public interface IEpubConversionService
{
    /// <summary>
    /// Convert <paramref name="comicFilePath"/> into an EPUB written inside
    /// <paramref name="outputDirectory"/> using default options. Equivalent to
    /// calling the <see cref="EpubConversionOptions"/> overload with no options.
    /// </summary>
    /// <returns>The absolute path of the generated .epub file.</returns>
    /// <exception cref="FileNotFoundException">The source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">The source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">The archive contains no images.</exception>
    Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Convert <paramref name="comicFilePath"/> into an EPUB written inside
    /// <paramref name="outputDirectory"/>, honoring the supplied <paramref name="options"/>.
    /// </summary>
    /// <returns>The absolute path of the generated .epub file.</returns>
    /// <exception cref="FileNotFoundException">The source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">The source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">The archive contains no images.</exception>
    Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Condenses several comic archives into a single EPUB written inside
    /// <paramref name="outputDirectory"/>. Pages are appended in the order the
    /// paths are supplied and the table of contents gets one entry per issue.
    /// </summary>
    /// <returns>The absolute path of the generated .epub file.</returns>
    /// <exception cref="ArgumentException">No source path was supplied.</exception>
    /// <exception cref="FileNotFoundException">A source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">A source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">A source archive contains no images.</exception>
    Task<string> ConvertToEpubAsync(
        IReadOnlyList<string> comicFilePaths,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default);
}
