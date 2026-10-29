using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Adds a series found on a Suwayomi source to the Suwayomi library and
/// queues the chapters the local library does not have yet.
/// </summary>
/// <remarks>
/// The local series is found the way the download import finds the folder a
/// Suwayomi series belongs in: a series whose title, canonical title or alias
/// equals the Suwayomi title, ignoring case, spacing and punctuation.
/// </remarks>
public sealed class SuwayomiSeriesAddService : ISuwayomiSeriesAddService
{
    private readonly ISuwayomiService _suwayomi;
    private readonly ISeriesLibraryService _seriesLibrary;
    private readonly ILogger<SuwayomiSeriesAddService> _logger;

    public SuwayomiSeriesAddService(
        ISuwayomiService suwayomi,
        ISeriesLibraryService seriesLibrary,
        ILogger<SuwayomiSeriesAddService> logger)
    {
        _suwayomi = suwayomi;
        _seriesLibrary = seriesLibrary;
        _logger = logger;
    }

    public async Task<SuwayomiAddSeriesResult> AddSeriesAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        var added = await _suwayomi.AddToLibraryAsync(seriesId, cancellationToken);
        var (localTitle, ownedIssues) = await FindLocalIssuesAsync(added.Series.Title, cancellationToken);

        SuwayomiSeriesDownloadResult downloads;
        try
        {
            downloads = await _suwayomi.QueueSeriesDownloadsAsync(seriesId, ownedIssues, cancellationToken);
        }
        catch (SuwayomiException ex)
        {
            // The add itself went through, which the user must not mistake
            // for the series being missing from Suwayomi.
            var done = added.AlreadyInLibrary
                ? $"'{added.Series.Title}' is in the Suwayomi library"
                : $"Added '{added.Series.Title}' to the Suwayomi library";
            throw new SuwayomiException($"{done}, but its chapters could not be queued: {ex.Message}", ex);
        }

        _logger.LogInformation(
            "Suwayomi series {SeriesId} ({SeriesTitle}) added: {Queued} of {Chapters} chapter(s) queued, {Owned} already in local series {LocalTitle}",
            added.Series.Id,
            LoggingHelper.SanitizeForLog(added.Series.Title),
            downloads.QueuedChapters.Count,
            downloads.ChapterCount,
            downloads.AlreadyInLocalLibraryCount,
            LoggingHelper.SanitizeForLog(localTitle ?? "(none)"));

        return new SuwayomiAddSeriesResult
        {
            Series = added.Series,
            AlreadyInLibrary = added.AlreadyInLibrary,
            LocalSeriesTitle = localTitle,
            Downloads = downloads
        };
    }

    private async Task<(string? Title, List<string?> Issues)> FindLocalIssuesAsync(
        string suwayomiTitle,
        CancellationToken cancellationToken)
    {
        var key = SuwayomiService.Normalize(suwayomiTitle);
        if (key.Length == 0)
        {
            return (null, new List<string?>());
        }

        var summaries = await _seriesLibrary.GetSeriesSummariesAsync(
            page: 1,
            perPage: -1,
            cancellationToken: cancellationToken);

        var matches = summaries.Series
            .Where(series => series.Aliases
                .Prepend(series.CanonicalTitle)
                .Prepend(series.Title)
                .Any(title => !string.IsNullOrWhiteSpace(title) && SuwayomiService.Normalize(title) == key))
            .ToList();
        if (matches.Count == 0)
        {
            return (null, new List<string?>());
        }

        var issuesById = await _seriesLibrary.GetSeriesIssuesForIdsAsync(
            matches.Select(series => series.Id),
            cancellationToken);

        var issues = issuesById.Values
            .SelectMany(result => result.Issues)
            .Select(issue => issue.Issue)
            .ToList();
        return (matches[0].Title, issues);
    }
}
