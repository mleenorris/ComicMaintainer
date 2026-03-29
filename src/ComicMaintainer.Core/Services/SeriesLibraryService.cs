using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public class SeriesLibraryService : ISeriesLibraryService
{
    private const int MaxConcurrentExternalLookups = 5;
    private static readonly Regex SeriesKeySanitizer = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IExternalSeriesMetadataService _externalMetadata;
    private readonly ILogger<SeriesLibraryService> _logger;

    public SeriesLibraryService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IExternalSeriesMetadataService externalMetadata,
        ILogger<SeriesLibraryService> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _externalMetadata = externalMetadata;
        _logger = logger;
    }

    public async Task<SeriesLibraryResult> GetSeriesAsync(
        string? filter = null,
        string? search = null,
        int page = 1,
        int perPage = 100,
        string? sort = "name",
        string? direction = "asc",
        CancellationToken cancellationToken = default)
    {
        var files = (await _fileStore.GetFilteredFilesAsync(filter, cancellationToken)).ToList();
        var fileEntries = new List<(ComicFile File, SeriesMetadata Metadata, string LocalTitle)>(files.Count);

        foreach (var file in files)
        {
            var metadata = await _processor.GetSeriesMetadataAsync(file.FilePath, cancellationToken) ?? new SeriesMetadata();
            fileEntries.Add((file, metadata, ResolveLocalSeriesTitle(metadata, file)));
        }

        var externalLookupMap = await BuildExternalLookupMapAsync(fileEntries.Select(entry => entry.LocalTitle), cancellationToken);
        var groups = new Dictionary<string, SeriesAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in fileEntries)
        {
            var file = entry.File;
            var metadata = entry.Metadata;
            var localTitle = entry.LocalTitle;
            externalLookupMap.TryGetValue(localTitle, out var externalMetadata);

            var canonicalTitle = string.IsNullOrWhiteSpace(externalMetadata?.CanonicalTitle)
                ? localTitle
                : externalMetadata!.CanonicalTitle;

            var groupKey = NormalizeKey(canonicalTitle);
            if (!groups.TryGetValue(groupKey, out var accumulator))
            {
                accumulator = new SeriesAccumulator
                {
                    Id = groupKey,
                    DisplayTitle = canonicalTitle,
                    CanonicalTitle = canonicalTitle,
                    MetadataSource = externalMetadata?.Source,
                    Aliases = externalMetadata?.Aliases
                        .Where(alias => !string.Equals(alias, canonicalTitle, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>()
                };
                groups[groupKey] = accumulator;
            }

            if (!string.Equals(localTitle, canonicalTitle, StringComparison.OrdinalIgnoreCase)
                && accumulator.Aliases.All(alias => !string.Equals(alias, localTitle, StringComparison.OrdinalIgnoreCase)))
            {
                accumulator.Aliases.Add(localTitle);
            }

            if (!string.IsNullOrWhiteSpace(metadata.AlternateSeries)
                && accumulator.Aliases.All(alias => !string.Equals(alias, metadata.AlternateSeries, StringComparison.OrdinalIgnoreCase)))
            {
                accumulator.Aliases.Add(metadata.AlternateSeries);
            }

            accumulator.TotalSize += file.FileSize;
            accumulator.LatestModified = accumulator.LatestModified < file.LastModified
                ? file.LastModified
                : accumulator.LatestModified;

            accumulator.Issues.Add(new SeriesIssueDto
            {
                FilePath = file.FilePath,
                FileName = file.FileName,
                Title = metadata.Title,
                Issue = metadata.Issue,
                Volume = metadata.Volume,
                Publisher = metadata.Publisher,
                Year = metadata.Year,
                Size = file.FileSize,
                Modified = ToUnixTime(file.LastModified),
                Processed = file.IsProcessed,
                Renamed = file.IsRenamed,
                Normalized = file.IsNormalized,
                Duplicate = file.IsDuplicate,
                Read = file.IsRead
            });
        }

        var groupedSeries = groups.Values
            .Select(accumulator =>
            {
                accumulator.Issues = accumulator.Issues
                    .OrderBy(issue => ExtractIssueSortKey(issue.Issue), new NaturalStringComparer())
                    .ThenBy(issue => issue.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new SeriesLibraryDto
                {
                    Id = accumulator.Id,
                    Title = accumulator.DisplayTitle,
                    CanonicalTitle = accumulator.CanonicalTitle,
                    Aliases = accumulator.Aliases
                        .Where(alias => !string.IsNullOrWhiteSpace(alias))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    MetadataSource = accumulator.MetadataSource,
                    IssueCount = accumulator.Issues.Count,
                    TotalSize = accumulator.TotalSize,
                    LatestModified = ToUnixTime(accumulator.LatestModified),
                    CoverFilePath = accumulator.Issues.FirstOrDefault()?.FilePath ?? string.Empty,
                    Issues = accumulator.Issues
                };
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            groupedSeries = groupedSeries.Where(series =>
                    Contains(series.Title, search)
                    || Contains(series.CanonicalTitle, search)
                    || series.Aliases.Any(alias => Contains(alias, search))
                    || series.Issues.Any(issue =>
                        Contains(issue.Title, search)
                        || Contains(issue.Issue, search)
                        || Contains(issue.FileName, search)))
                .ToList();
        }

        groupedSeries = (sort?.ToLowerInvariant(), direction?.ToLowerInvariant()) switch
        {
            ("date", "desc") => groupedSeries.OrderByDescending(series => series.LatestModified).ToList(),
            ("date", "asc") => groupedSeries.OrderBy(series => series.LatestModified).ToList(),
            ("size", "desc") => groupedSeries.OrderByDescending(series => series.TotalSize).ToList(),
            ("size", "asc") => groupedSeries.OrderBy(series => series.TotalSize).ToList(),
            ("name", "desc") => groupedSeries.OrderByDescending(series => series.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => groupedSeries.OrderBy(series => series.Title, StringComparer.OrdinalIgnoreCase).ToList()
        };

        var totalSeries = groupedSeries.Count;
        var totalPages = perPage == -1 ? 1 : (int)Math.Ceiling((double)totalSeries / Math.Max(1, perPage));
        page = Math.Max(1, Math.Min(page, totalPages == 0 ? 1 : totalPages));

        if (perPage != -1)
        {
            groupedSeries = groupedSeries
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToList();
        }

        return new SeriesLibraryResult
        {
            Series = groupedSeries,
            Page = page,
            TotalPages = totalPages,
            TotalSeries = totalSeries
        };
    }

    private static string ResolveLocalSeriesTitle(SeriesMetadata metadata, ComicFile file)
    {
        return FirstNonEmpty(
                metadata.SeriesGroup,
                metadata.AlternateSeries,
                metadata.Series,
                Path.GetFileName(Path.GetDirectoryName(file.FilePath) ?? string.Empty),
                Path.GetFileNameWithoutExtension(file.FileName))
            ?? "Unknown Series";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }

        var normalized = SeriesKeySanitizer.Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();

    private static string ExtractIssueSortKey(string? issue)
        => string.IsNullOrWhiteSpace(issue) ? "~" : issue;

    private async Task<Dictionary<string, ExternalSeriesMetadata?>> BuildExternalLookupMapAsync(IEnumerable<string> localTitles, CancellationToken cancellationToken)
    {
        var uniqueTitles = new HashSet<string>(
            localTitles.Where(title => !string.IsNullOrWhiteSpace(title)),
            StringComparer.OrdinalIgnoreCase);

        var lookupResults = new ConcurrentDictionary<string, ExternalSeriesMetadata?>(StringComparer.OrdinalIgnoreCase);
        using var concurrencyGate = new SemaphoreSlim(MaxConcurrentExternalLookups);

        var tasks = uniqueTitles.Select(async title =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                lookupResults[title] = await _externalMetadata.LookupSeriesAsync(title, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "External metadata lookup failed for {SeriesTitle}", title);
                lookupResults[title] = null;
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new Dictionary<string, ExternalSeriesMetadata?>(lookupResults, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SeriesAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayTitle { get; set; } = string.Empty;
        public string CanonicalTitle { get; set; } = string.Empty;
        public string? MetadataSource { get; set; }
        public List<string> Aliases { get; set; } = new();
        public List<SeriesIssueDto> Issues { get; set; } = new();
        public long TotalSize { get; set; }
        public DateTime LatestModified { get; set; } = DateTime.MinValue;
    }
}
