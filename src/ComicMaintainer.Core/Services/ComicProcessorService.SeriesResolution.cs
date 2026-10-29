using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// Resolves the series name to write into a file through <see cref="ISeriesNameResolver"/>,
    /// so the normalize pipeline, audit handlers, library-scan retag pass and diagnostic
    /// endpoint all share the same documented priority.
    /// </summary>
    private async Task<string> ResolveNormalizedSeriesAsync(ComicMetadata metadata, string filePath, CancellationToken cancellationToken)
    {
        var resolution = await _seriesNameResolver.ResolveAsync(filePath, metadata, mutateCache: true, cancellationToken);
        return string.IsNullOrWhiteSpace(resolution.ResolvedSeries) ? UnknownSeries : resolution.ResolvedSeries;
    }
}
