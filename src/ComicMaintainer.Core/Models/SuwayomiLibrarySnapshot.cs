namespace ComicMaintainer.Core.Models;

public sealed class SuwayomiLibrarySnapshot
{
    public IReadOnlyList<SuwayomiSeries> Series { get; init; } = Array.Empty<SuwayomiSeries>();

    internal IReadOnlyList<(SuwayomiSeries Series, string NormalizedTitle)> NormalizedSeries { get; init; } =
        Array.Empty<(SuwayomiSeries, string)>();

    // A scheduled missing-issues pass reuses one snapshot for every local
    // series, so load the installed sources only once for that pass.
    internal IReadOnlyList<SuwayomiSource>? Sources { get; set; }
}
