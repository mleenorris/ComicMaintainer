using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// The one answer to "which tracked files belong to series X", shared by the
/// metadata refresh job, apply-match, the preferred-language retag and the
/// audits so they cannot drift apart.
/// </summary>
/// <remarks>
/// A series is identified by the series keys (<see cref="SeriesKey"/>) of
/// every title it is known by. A file belongs to it when the series key of its
/// ComicInfo <c>&lt;Series&gt;</c> (<see cref="ComicFile.Metadata"/>, the cached
/// copy of the archive's tags, filled in by the background metadata scan) or of its
/// folder-derived series name is one of them. The folder fallback finds the
/// files the scan has not reached yet, and files tagged with a name the series
/// is no longer known by: the folder name is the title the series was first
/// looked up under.
/// </remarks>
public static class SeriesFileMatcher
{
    /// <summary>
    /// Every title a file of <paramref name="record"/>'s series can carry or
    /// live under: the canonical title, the user-selected name, provider and
    /// user aliases, and localized titles (so files whose
    /// <c>&lt;Series&gt;</c> was written in another language still match).
    /// Blank titles are skipped; duplicates are removed case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> TitlesOf(SeriesMetadataCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? title)
        {
            if (!string.IsNullOrWhiteSpace(title) && seen.Add(title.Trim()))
            {
                titles.Add(title.Trim());
            }
        }

        Add(record.CanonicalTitle);
        Add(record.SeriesName);
        foreach (var alias in record.Aliases ?? Enumerable.Empty<string>()) Add(alias);
        foreach (var alias in record.UserAliases ?? Enumerable.Empty<string>()) Add(alias);
        foreach (var localized in record.LocalizedTitles ?? Enumerable.Empty<LocalizedTitle>()) Add(localized?.Title);
        return titles;
    }

    /// <summary>The series keys of <paramref name="titles"/>; titles without letters or digits are skipped.</summary>
    public static HashSet<string> KeysOf(IEnumerable<string?> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return new HashSet<string>(titles.Select(title => SeriesKey.TryNormalize(title)).OfType<string>(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The series keys of every title of <paramref name="record"/> (see
    /// <see cref="TitlesOf"/>), of the record's own key (the title it was first
    /// looked up under, typically the folder name, which a manual match does
    /// not keep among its titles) and of <paramref name="extraTitles"/>, such
    /// as the title a refresh or match was requested for.
    /// </summary>
    public static HashSet<string> KeysOf(SeriesMetadataCacheRecord record, params string?[] extraTitles)
    {
        var keys = KeysOf(TitlesOf(record).Concat(extraTitles ?? Array.Empty<string?>()));
        // The stored key is already a series key; "unknown-series" names no title.
        if (SeriesKey.TryNormalize(record.NormalizedKey) is { } ownKey
            && !string.Equals(ownKey, SeriesKey.Unknown, StringComparison.Ordinal))
        {
            keys.Add(ownKey);
        }
        return keys;
    }

    /// <summary>
    /// The series name derived from the folder that holds
    /// <paramref name="filePath"/> (normalized as for display, see
    /// <see cref="ComicFileProcessor.NormalizeSeriesName"/>), or null when the
    /// file has no named parent folder.
    /// </summary>
    public static string? FolderSeriesOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var series = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        return string.IsNullOrWhiteSpace(series) ? null : series;
    }

    /// <summary>
    /// True when <paramref name="file"/> belongs to the series identified by
    /// <paramref name="seriesKeys"/>: the key of its tracked <c>&lt;Series&gt;</c>
    /// or of its folder-derived series name is one of them.
    /// </summary>
    public static bool BelongsTo(ComicFile file, IReadOnlySet<string> seriesKeys)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(seriesKeys);
        if (seriesKeys.Count == 0)
        {
            return false;
        }

        if (SeriesKey.TryNormalize(file.Metadata?.Series) is { } metadataKey && seriesKeys.Contains(metadataKey))
        {
            return true;
        }

        return SeriesKey.TryNormalize(FolderSeriesOf(file.FilePath)) is { } folderKey && seriesKeys.Contains(folderKey);
    }

    /// <summary>
    /// The distinct paths of the files in <paramref name="files"/> that belong
    /// to the series identified by <paramref name="seriesKeys"/>, in their
    /// original order. Paths are compared exactly: on a case-sensitive
    /// filesystem two paths that differ only in case are two different files.
    /// </summary>
    public static List<string> SelectFilePaths(IEnumerable<ComicFile> files, IReadOnlySet<string> seriesKeys) =>
        SelectFiles(files, seriesKeys).ConvertAll(file => file.FilePath);

    /// <summary>
    /// The distinct files in <paramref name="files"/> that belong to the series
    /// identified by <paramref name="seriesKeys"/>, in their original order; see
    /// <see cref="SelectFilePaths"/>.
    /// </summary>
    public static List<ComicFile> SelectFiles(IEnumerable<ComicFile> files, IReadOnlySet<string> seriesKeys)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(seriesKeys);
        // DistinctBy keeps the first file of each path, as a seen-set would.
        return files
            .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.FilePath) && BelongsTo(file, seriesKeys))
            .DistinctBy(file => file.FilePath, StringComparer.Ordinal)
            .ToList();
    }
}
