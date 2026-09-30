namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Converts comic archives (CBZ/CBR) into fixed-layout AZW3 (Kindle Format 8)
/// files, the format Kindle devices render natively.
/// </summary>
/// <remarks>
/// The book is produced from the same fixed-layout EPUB the
/// <see cref="IEpubConversionService"/> builds, so page preparation, metadata
/// and the <see cref="EpubConversionOptions.MaxSizeBytes"/> budget all behave
/// exactly as they do for EPUB delivery.
/// </remarks>
public interface IAzw3ConversionService
{
    /// <summary>
    /// Convert <paramref name="comicFilePath"/> into an AZW3 written inside
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    /// <returns>The absolute path of the generated .azw3 file.</returns>
    /// <exception cref="FileNotFoundException">The source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">The source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">The archive contains no images.</exception>
    Task<string> ConvertToAzw3Async(
        string comicFilePath,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Condenses several comic archives into a single AZW3 written inside
    /// <paramref name="outputDirectory"/>. Pages are appended in the order the
    /// paths are supplied.
    /// </summary>
    /// <returns>The absolute path of the generated .azw3 file.</returns>
    /// <exception cref="ArgumentException">No source path was supplied.</exception>
    /// <exception cref="FileNotFoundException">A source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">A source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">A source archive contains no images.</exception>
    Task<string> ConvertToAzw3Async(
        IReadOnlyList<string> comicFilePaths,
        string outputDirectory,
        EpubConversionOptions? options = null,
        CancellationToken cancellationToken = default);
}
