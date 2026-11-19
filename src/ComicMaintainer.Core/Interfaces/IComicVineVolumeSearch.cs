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

    /// <summary>
    /// Every ComicVine volume named exactly <paramref name="title"/> (punctuation and case aside),
    /// oldest first, from ComicVine's volume list filtered by name rather than its ranked search,
    /// so a volume the search ranks too low is still found. The filter matches names that contain
    /// the title, so a much-used name takes several pages: at most <paramref name="maxRequests"/>
    /// are read. <c>Requests</c> is how many were made. Throws
    /// <see cref="ExternalMetadataUnavailableException"/> when ComicVine cannot answer.
    /// </summary>
    Task<ComicVineResult<IReadOnlyList<ExternalSeriesMetadata>>> ListVolumesNamedAsync(
        string title,
        int maxRequests,
        CancellationToken cancellationToken = default);
}
