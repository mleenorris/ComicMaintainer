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
public record EpubConversionOptions(
    string? SeriesImagePath = null,
    string? SeriesTitle = null,
    long? MaxSizeBytes = null,
    string? Title = null,
    string? OutputFileName = null);

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
