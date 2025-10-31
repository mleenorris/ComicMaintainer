namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Utility class for handling comic file extensions and validation
/// </summary>
public static class ComicFileExtensions
{
    /// <summary>
    /// Supported comic archive extensions
    /// </summary>
    public static readonly string[] SupportedExtensions = { ".cbz", ".cbr", ".zip", ".rar" };

    /// <summary>
    /// Extensions that support writing (ZIP-based formats)
    /// </summary>
    public static readonly string[] WritableExtensions = { ".cbz" };

    /// <summary>
    /// Check if a file is a comic archive based on its extension
    /// </summary>
    /// <param name="filePath">The file path to check</param>
    /// <returns>True if the file has a supported comic archive extension</returns>
    public static bool IsComicArchive(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return Array.Exists(SupportedExtensions, ext => ext == extension);
    }

    /// <summary>
    /// Check if a file extension supports writing/modification
    /// </summary>
    /// <param name="filePath">The file path to check</param>
    /// <returns>True if the file can be modified (CBZ format)</returns>
    public static bool IsWritableArchive(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return Array.Exists(WritableExtensions, ext => ext == extension);
    }

    /// <summary>
    /// Normalize a comic file extension to a standard format
    /// </summary>
    /// <param name="extension">The extension to normalize</param>
    /// <returns>Normalized extension (e.g., .zip becomes .cbz)</returns>
    public static string NormalizeExtension(string extension)
    {
        var normalized = extension.ToLowerInvariant();
        return normalized switch
        {
            ".zip" => ".cbz",
            ".rar" => ".cbr",
            _ => normalized
        };
    }
}
