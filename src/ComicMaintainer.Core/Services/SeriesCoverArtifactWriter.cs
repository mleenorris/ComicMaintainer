using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Default <see cref="ISeriesCoverArtifactWriter"/>: applies a cover change to the folder sidecars,
/// then to the first archive.
/// </summary>
public class SeriesCoverArtifactWriter : ISeriesCoverArtifactWriter
{
    private readonly ISeriesMetadataCacheService _cache;
    private readonly ISeriesFolderCoverWriter _folderCoverWriter;
    private readonly ISeriesArchiveCoverWriter _archiveCoverWriter;

    public SeriesCoverArtifactWriter(
        ISeriesMetadataCacheService cache,
        ISeriesFolderCoverWriter folderCoverWriter,
        ISeriesArchiveCoverWriter archiveCoverWriter)
    {
        _cache = cache;
        _folderCoverWriter = folderCoverWriter;
        _archiveCoverWriter = archiveCoverWriter;
    }

    public async Task WriteAsync(
        string normalizedKey,
        string sourceFilePath,
        string contentType,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _folderCoverWriter.WriteAsync(normalizedKey, sourceFilePath, contentType, force, cancellationToken);
        await _archiveCoverWriter.WriteAsync(normalizedKey, sourceFilePath, contentType, force, cancellationToken);
    }

    public async Task RemoveAsync(string normalizedKey, CancellationToken cancellationToken = default)
    {
        await _folderCoverWriter.RemoveAsync(normalizedKey, cancellationToken);
        await _archiveCoverWriter.RemoveAsync(normalizedKey, cancellationToken);
    }

    public async Task<bool> ReapplyAsync(string seriesTitle, CancellationToken cancellationToken = default)
    {
        var source = await _cache.GetCoverSourceAsync(seriesTitle, cancellationToken);
        if (source is null)
        {
            return false;
        }

        await WriteAsync(source.NormalizedKey, source.FilePath, source.ContentType, force: true, cancellationToken);
        return true;
    }
}
