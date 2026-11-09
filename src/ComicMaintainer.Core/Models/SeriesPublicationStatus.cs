namespace ComicMaintainer.Core.Models;

/// <summary>
/// The publication states a series can be in, as stored in the metadata cache and accepted by
/// the library's status filter. Every provider's own wording is mapped onto these.
/// </summary>
public static class SeriesPublicationStatus
{
    public const string Ongoing = "ongoing";
    public const string Completed = "completed";
    public const string Hiatus = "hiatus";
    public const string Cancelled = "cancelled";
    public const string Upcoming = "upcoming";

    /// <summary>Every known status, in the order the library's filter lists them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Ongoing, Completed, Hiatus, Cancelled, Upcoming };

    /// <summary>
    /// The stored spelling of <paramref name="value"/>, or null when it is not a known status.
    /// </summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(status => string.Equals(status, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}
