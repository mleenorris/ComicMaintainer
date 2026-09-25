namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Converts comic archives (CBZ/CBR) into fixed-layout EPUB3 files suitable for
/// ereaders that cannot open comic archives directly (e.g. Kindle, Kobo).
/// </summary>
public interface IEpubConversionService
{
    /// <summary>
    /// Convert <paramref name="comicFilePath"/> into an EPUB written inside
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    /// <returns>The absolute path of the generated .epub file.</returns>
    /// <exception cref="FileNotFoundException">The source comic does not exist.</exception>
    /// <exception cref="NotSupportedException">The source file is not a supported comic archive.</exception>
    /// <exception cref="InvalidOperationException">The archive contains no images.</exception>
    Task<string> ConvertToEpubAsync(
        string comicFilePath,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
