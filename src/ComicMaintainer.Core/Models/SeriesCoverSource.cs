namespace ComicMaintainer.Core.Models;

/// <summary>A series' cached cover image, as a file that can be copied into its artifacts.</summary>
/// <param name="NormalizedKey">The series cache key.</param>
/// <param name="FilePath">Absolute path of the cached image file.</param>
/// <param name="ContentType">The image content type.</param>
public sealed record SeriesCoverSource(string NormalizedKey, string FilePath, string ContentType);
