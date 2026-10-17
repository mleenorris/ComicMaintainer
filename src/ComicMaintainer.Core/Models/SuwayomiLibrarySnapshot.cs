namespace ComicMaintainer.Core.Models;

public sealed class SuwayomiLibrarySnapshot
{
    public IReadOnlyList<SuwayomiSeries> Series { get; init; } = Array.Empty<SuwayomiSeries>();

    internal IReadOnlyDictionary<string, SuwayomiSeries> NormalizedTitleIndex { get; init; } =
        new Dictionary<string, SuwayomiSeries>(StringComparer.Ordinal);

    internal IReadOnlyList<(SuwayomiSeries Series, string NormalizedTitle)> NormalizedSeries { get; init; } =
        Array.Empty<(SuwayomiSeries, string)>();
}
