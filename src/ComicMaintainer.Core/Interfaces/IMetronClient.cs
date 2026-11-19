namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// One series on Metron (metron.cloud): its volume number and start year as Metron counts them.
/// Metron records the ComicVine volume of each series, so it is a second opinion on what a ComicVine
/// volume is.
/// </summary>
public sealed record MetronSeries(int Id, string? Name, int? Volume, int? YearBegan);

/// <summary>What Metron said about a ComicVine volume (see <see cref="IMetronClient.SeriesOfComicVineVolumeAsync"/>).</summary>
/// <param name="Available">False when Metron could not be asked (not configured, failing, or limiting requests).</param>
/// <param name="Series">Metron's series for the volume; null when it has none, or could not be asked.</param>
public sealed record MetronAnswer(bool Available, MetronSeries? Series)
{
    /// <summary>Metron could not be asked.</summary>
    public static MetronAnswer Unavailable { get; } = new(false, null);
}

/// <summary>
/// Metron's API, asked about ComicVine volumes. Optional: it needs a Metron account (username and
/// password in the settings). It never throws for Metron's failures; it answers
/// <see cref="MetronAnswer.Unavailable"/>, so matching goes on without it.
/// </summary>
public interface IMetronClient
{
    /// <summary>True when a Metron username and password are set and external metadata is on.</summary>
    bool IsConfigured { get; }

    /// <summary>The Metron series that records ComicVine volume <paramref name="comicVineVolumeId"/>.</summary>
    Task<MetronAnswer> SeriesOfComicVineVolumeAsync(int comicVineVolumeId, CancellationToken cancellationToken = default);
}
