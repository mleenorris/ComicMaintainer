using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// Resolves the series name to write into a file through <see cref="ISeriesNameResolver"/>,
    /// so the normalize pipeline, audit handlers, library-scan retag pass and diagnostic
    /// endpoint all share the same documented priority.
    /// <para>
    /// Returns an empty string, meaning "no series", for a loose file in a library root that
    /// nothing names a series for: the "Unknown Series" fallback would rename every such file
    /// to the same "Unknown Series - Chapter NNNN" and flag all but one as duplicates.
    /// </para>
    /// <para>
    /// An Annual (see <see cref="AnnualIssue.IsAnnual"/>) keeps "Annual" in its series: the
    /// series it belongs to is resolved ("Green Lantern Annual" is looked up as "Green
    /// Lantern"), then "Annual" is put back ("Green Lantern Annual"). Annual #1 then neither
    /// takes issue #1's name nor its place, while it stays in the same series, and no Annual
    /// gets a series record of its own.
    /// </para>
    /// <para>
    /// <paramref name="mutateCache"/> is passed to the resolver: a processing preview passes
    /// false, so it records no alias and persists no provider lookup.
    /// </para>
    /// </summary>
    private async Task<string> ResolveNormalizedSeriesAsync(ComicMetadata metadata, string filePath, bool mutateCache, CancellationToken cancellationToken)
    {
        var annual = AnnualIssue.IsAnnual(metadata.Series, metadata.Issue, Path.GetFileName(filePath));
        var lookup = metadata;
        if (annual && AnnualIssue.IsAnnualSeries(metadata.Series))
        {
            lookup = metadata.Clone();
            lookup.Series = AnnualIssue.WithoutAnnual(metadata.Series!);
        }

        var resolution = await _seriesNameResolver.ResolveAsync(filePath, lookup, mutateCache, cancellationToken);
        var unresolved = string.IsNullOrWhiteSpace(resolution.ResolvedSeries)
            || resolution.WinningStep == SeriesNameResolutionStep.UnknownSentinel;
        if (unresolved && IsInLibraryRoot(filePath))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(resolution.ResolvedSeries))
        {
            return UnknownSeries;
        }

        return annual && !unresolved ? AnnualIssue.WithAnnual(resolution.ResolvedSeries) : resolution.ResolvedSeries;
    }

    /// <summary>
    /// True for a file directly in the watched or duplicates directory rather than in a series
    /// folder, whose folder name therefore does not name its series.
    /// </summary>
    private bool IsInLibraryRoot(string filePath) =>
        SeriesFromPath.IsDirectlyInLibraryRoot(filePath, _settings.WatchedDirectory, _settings.DuplicateDirectory);

    /// <summary>History and job message for a file nothing names a series for.</summary>
    private string NoSeriesMessage(string filePath, string fallback) =>
        IsInLibraryRoot(filePath)
            ? "Loose file in the library root: neither its metadata nor its file name names a series, so it is not renamed"
            : fallback;
}
