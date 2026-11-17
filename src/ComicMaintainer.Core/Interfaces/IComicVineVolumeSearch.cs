using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// ComicVine's volumes for a title, read to suggest the number of a volume known only by its
/// start year (see <see cref="Utilities.VolumeOrdinal"/>).
/// </summary>
public interface IComicVineVolumeSearch
{
    /// <summary>
    /// The volumes ComicVine's search returns for <paramref name="title"/>, over several pages so
    /// that every volume of a much-used name (Batman, X-Men) is likely among them. Empty when
    /// ComicVine is not configured. Throws <see cref="ExternalMetadataUnavailableException"/>
    /// when ComicVine cannot answer.
    /// </summary>
    Task<IReadOnlyList<ExternalSeriesMetadata>> SearchVolumesAsync(string title, CancellationToken cancellationToken = default);
}
