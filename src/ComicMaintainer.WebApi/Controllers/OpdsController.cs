using System.Globalization;
using System.Security.Claims;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Authentication;
using ComicMaintainer.WebApi.Infrastructure;
using ComicMaintainer.WebApi.Opds;
using ComicMaintainer.WebApi.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ComicMaintainer.WebApi.Controllers;

/// <summary>
/// The OPDS 1.2 catalog, with OPDS-PSE page streaming, for comic apps. Clients sign in with HTTP Basic (see
/// <see cref="OpdsBasicAuthenticationHandler"/>); while <c>OPDS_ENABLED</c> is off every route answers 404 (see
/// <see cref="Middleware.OpdsFeatureGateMiddleware"/>).
/// </summary>
/// <remarks>
/// A file is named by its row id, never by a path, and every id is checked against the library root again before
/// anything is opened, with the same check as the web reader, so a client cannot reach a file the reader would refuse.
/// Archives are read through the same bounded services as the reader.
/// </remarks>
[ApiController]
[Route(OpdsLinks.RoutePrefix)]
[Authorize(Policy = OpdsBasicAuthenticationHandler.PolicyName)]
public sealed class OpdsController : ControllerBase
{
    /// <summary>Items in one page of a paged feed.</summary>
    public const int PageSize = 50;

    /// <summary>The longest search term accepted.</summary>
    public const int MaxSearchLength = 200;

    // Archives listed at once while building a feed: a feed's first view lists up to PageSize archives.
    private const int MaxConcurrentListings = 4;

    // How long the feeds leave alone a file the reader could not list; a file that changes is listed again at once.
    private static readonly TimeSpan UnreadableRememberedFor = TimeSpan.FromMinutes(15);

    private const string UnreadableCacheKeyPrefix = "opds-unreadable:";

    private readonly ISeriesLibraryService _library;
    private readonly IOverviewService _overview;
    private readonly IFileStoreService _fileStore;
    private readonly IComicReaderService _reader;
    private readonly IFileCoverCacheService _covers;
    private readonly IReadingProgressService _progress;
    private readonly OpdsImageConverter _images;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<OpdsController> _logger;

    public OpdsController(
        ISeriesLibraryService library,
        IOverviewService overview,
        IFileStoreService fileStore,
        IComicReaderService reader,
        IFileCoverCacheService covers,
        IReadingProgressService progress,
        OpdsImageConverter images,
        IMemoryCache cache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<OpdsController> logger)
    {
        _library = library;
        _overview = overview;
        _fileStore = fileStore;
        _reader = reader;
        _covers = covers;
        _progress = progress;
        _images = images;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    private OpdsLinks Links => new(AppBasePath.Normalize(_settings.CurrentValue.BasePath));

    private string? CurrentUserId
    {
        get
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrEmpty(userId) ? null : userId;
        }
    }

    /// <summary>The root navigation feed, also at the bare catalog address some apps are given.</summary>
    [HttpGet]
    [HttpGet("catalog")]
    public IActionResult Catalog()
    {
        var links = Links;
        return Feed(OpdsFeedBuilder.BuildFeed(
            links,
            new OpdsFeedInfo("urn:comicmaintainer:opds:catalog", "ComicMaintainer", OpdsMediaTypes.NavigationFeed, links.Catalog),
            [
                OpdsFeedBuilder.NavigationEntry("urn:comicmaintainer:opds:continue-reading", "Continue Reading", links.ContinueReading, OpdsMediaTypes.AcquisitionFeed, "Pick up where you left off"),
                OpdsFeedBuilder.NavigationEntry("urn:comicmaintainer:opds:series-updates", "Series Updates", links.SeriesUpdates, OpdsMediaTypes.NavigationFeed, "Series with new issues"),
                OpdsFeedBuilder.NavigationEntry("urn:comicmaintainer:opds:newly-added", "Newly Added", links.NewlyAdded, OpdsMediaTypes.NavigationFeed, "Series added recently"),
                OpdsFeedBuilder.NavigationEntry("urn:comicmaintainer:opds:all-series", "All Series", links.AllSeries(), OpdsMediaTypes.NavigationFeed, "Every series in the library"),
            ]),
            OpdsMediaTypes.NavigationFeed);
    }

    /// <summary>The user's Continue Reading row, as the issue to resume in each series.</summary>
    [HttpGet("continue-reading")]
    [FailureMessage("Error building the Continue Reading feed")]
    public async Task<IActionResult> ContinueReading(CancellationToken cancellationToken)
    {
        var links = Links;
        var userId = CurrentUserId;
        var cards = userId is null ? [] : (await _overview.GetOverviewAsync(userId, cancellationToken)).ContinueReading;
        var seriesIssues = await _library.GetSeriesIssuesForIdsAsync(cards.Select(c => c.Id).ToArray(), cancellationToken);

        var resumeIssues = new List<(string SeriesTitle, SeriesIssueDto Issue)>();
        foreach (var card in cards)
        {
            var issue = seriesIssues.GetValueOrDefault(card.Id)?.Issues
                .FirstOrDefault(i => string.Equals(i.FilePath, card.ResumeFilePath, StringComparison.OrdinalIgnoreCase));
            if (issue is not null)
            {
                resumeIssues.Add((card.Title, issue));
            }
        }

        var entries = await BuildIssueEntriesAsync(links, resumeIssues, cancellationToken);
        return Feed(OpdsFeedBuilder.BuildFeed(
            links,
            new OpdsFeedInfo("urn:comicmaintainer:opds:continue-reading", "Continue Reading", OpdsMediaTypes.AcquisitionFeed, links.ContinueReading, Up: links.Catalog),
            entries),
            OpdsMediaTypes.AcquisitionFeed);
    }

    /// <summary>The Series Updates row: series that gained an issue recently.</summary>
    [HttpGet("series-updates")]
    [FailureMessage("Error building the Series Updates feed")]
    public async Task<IActionResult> SeriesUpdates(CancellationToken cancellationToken)
    {
        var links = Links;
        var cards = await OverviewCardsAsync(o => o.SeriesUpdates, cancellationToken);
        return await SeriesNavigationFeedAsync(
            links,
            new OpdsFeedInfo("urn:comicmaintainer:opds:series-updates", "Series Updates", OpdsMediaTypes.NavigationFeed, links.SeriesUpdates, Up: links.Catalog),
            cards,
            cancellationToken);
    }

    /// <summary>The Newly Added row: series that appeared recently.</summary>
    [HttpGet("newly-added")]
    [FailureMessage("Error building the Newly Added feed")]
    public async Task<IActionResult> NewlyAdded(CancellationToken cancellationToken)
    {
        var links = Links;
        var cards = await OverviewCardsAsync(o => o.NewlyAddedSeries, cancellationToken);
        return await SeriesNavigationFeedAsync(
            links,
            new OpdsFeedInfo("urn:comicmaintainer:opds:newly-added", "Newly Added", OpdsMediaTypes.NavigationFeed, links.NewlyAdded, Up: links.Catalog),
            cards,
            cancellationToken);
    }

    /// <summary>Every series in the library, a page at a time.</summary>
    [HttpGet("series")]
    [FailureMessage("Error building the series feed")]
    public async Task<IActionResult> AllSeries([FromQuery] string? page = null, CancellationToken cancellationToken = default)
    {
        var links = Links;
        var result = await _library.GetSeriesSummariesAsync(page: PageNumber(page), perPage: PageSize, sort: "name", direction: "asc", cancellationToken: cancellationToken);
        return await SeriesNavigationFeedAsync(
            links,
            new OpdsFeedInfo(
                "urn:comicmaintainer:opds:all-series",
                "All Series",
                OpdsMediaTypes.NavigationFeed,
                links.AllSeries(result.Page),
                Up: links.Catalog,
                Paging: new OpdsPaging(result.Page, result.TotalPages, PageSize, result.TotalSeries, p => links.AllSeries(p))),
            result.Series,
            cancellationToken);
    }

    /// <summary>The acquisition feed of one series: its issues, a page at a time.</summary>
    [HttpGet("series/{seriesId}")]
    [FailureMessage("Error building the series feed")]
    public async Task<IActionResult> Series(string seriesId, [FromQuery] string? page = null, CancellationToken cancellationToken = default)
    {
        var links = Links;
        var result = await _library.GetSeriesIssuesAsync(seriesId, filter: null, page: PageNumber(page), perPage: PageSize, cancellationToken: cancellationToken);
        if (result is null)
        {
            return Refuse(StatusCodes.Status404NotFound, "Series not found");
        }

        var entries = await BuildIssueEntriesAsync(links, result.Issues.Select(i => (result.Title, i)).ToArray(), cancellationToken);
        return Feed(OpdsFeedBuilder.BuildFeed(
            links,
            new OpdsFeedInfo(
                $"urn:comicmaintainer:series:{Uri.EscapeDataString(result.Id)}",
                result.Title,
                OpdsMediaTypes.AcquisitionFeed,
                links.Series(seriesId, result.Page),
                Up: links.AllSeries(),
                Paging: new OpdsPaging(result.Page, result.TotalPages, PageSize, result.IssueCount, p => links.Series(seriesId, p))),
            entries),
            OpdsMediaTypes.AcquisitionFeed);
    }

    /// <summary>Series whose names or issues match <paramref name="q"/>, a page at a time.</summary>
    [HttpGet("search")]
    [FailureMessage("Error searching the library")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? page = null, CancellationToken cancellationToken = default)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return Refuse(StatusCodes.Status400BadRequest, "A search term is required");
        }

        if (query.Length > MaxSearchLength)
        {
            return Refuse(StatusCodes.Status400BadRequest, $"The search term must be {MaxSearchLength} characters or fewer");
        }

        var links = Links;
        var result = await _library.GetSeriesSummariesAsync(search: query, page: PageNumber(page), perPage: PageSize, sort: "name", direction: "asc", cancellationToken: cancellationToken);
        return await SeriesNavigationFeedAsync(
            links,
            new OpdsFeedInfo(
                $"urn:comicmaintainer:opds:search:{Uri.EscapeDataString(query)}",
                $"Search: {query}",
                OpdsMediaTypes.NavigationFeed,
                links.Search(query, result.Page),
                Up: links.Catalog,
                Paging: new OpdsPaging(result.Page, result.TotalPages, PageSize, result.TotalSeries, p => links.Search(query, p))),
            result.Series,
            cancellationToken);
    }

    /// <summary>The OpenSearch description of the search.</summary>
    [HttpGet("opensearch.xml")]
    public IActionResult OpenSearchDescription() =>
        Feed(OpdsFeedBuilder.BuildOpenSearchDescription(Links), OpdsMediaTypes.OpenSearchDescription);

    /// <summary>
    /// A comic's cover, used for both the image and the thumbnail links. It never counts as reading: only the page
    /// stream records progress.
    /// </summary>
    [HttpGet("issues/{id:int}/cover")]
    [FailureMessage("Error reading cover")]
    public async Task<IActionResult> Cover(int id, CancellationToken cancellationToken)
    {
        if (await ResolveIssueAsync(id, cancellationToken) is not { } issue)
        {
            return IssueNotFound();
        }

        var validator = _covers.GetValidator(issue.ResolvedPath);
        if (validator is null)
        {
            return IssueNotFound();
        }

        var etag = ComicReaderController.BuildCoverETag(validator.Value);
        if (ConditionalRequests.IfNoneMatchSatisfied(Request.Headers.IfNoneMatch, etag))
        {
            SetPrivateCaching(etag);
            return StatusCode(StatusCodes.Status304NotModified);
        }

        FileCoverCacheEntry? entry;
        try
        {
            entry = await _covers.GetOrCreateAsync(issue.ResolvedPath, cancellationToken);
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }

        if (entry is null)
        {
            return Refuse(StatusCodes.Status404NotFound, "Cover not available");
        }

        var served = entry.Value.Validator;
        var (data, contentType) = await _images.ConvertAsync(entry.Value.Data, entry.Value.ContentType, OpdsMediaTypes.Jpeg, cancellationToken);
        SetPrivateCaching(ComicReaderController.BuildCoverETag(served));
        Response.Headers.LastModified = served.SourceLastModified.ToString("R");
        return File(data, contentType);
    }

    /// <summary>
    /// One page of a comic for OPDS-PSE. <paramref name="pageNumber"/> starts at 0, where the reader's page numbers
    /// start at 1. This is the only OPDS route that records reading progress.
    /// </summary>
    [HttpGet("issues/{id:int}/pages/{pageNumber:int}")]
    [FailureMessage("Error reading page")]
    public async Task<IActionResult> Page(int id, int pageNumber, CancellationToken cancellationToken)
    {
        if (pageNumber < 0)
        {
            return Refuse(StatusCodes.Status400BadRequest, "Page numbers start at 0");
        }

        if (await ResolveIssueAsync(id, cancellationToken) is not { } issue)
        {
            return IssueNotFound();
        }

        var info = new FileInfo(issue.ResolvedPath);
        if (!info.Exists)
        {
            return IssueNotFound();
        }

        try
        {
            var pageNames = await _reader.GetPageNamesAsync(issue.ResolvedPath);
            if (pageNumber >= pageNames.Count)
            {
                return Refuse(StatusCodes.Status404NotFound, "Page not found");
            }

            var readerPage = pageNumber + 1;
            var etag = ComicReaderController.BuildPageETag(info.Length, info.LastWriteTimeUtc, readerPage);
            if (ConditionalRequests.IfNoneMatchSatisfied(Request.Headers.IfNoneMatch, etag))
            {
                await RecordProgressAsync(issue.FilePath, readerPage, pageNames.Count, cancellationToken);
                SetPrivateCaching(etag);
                return StatusCode(StatusCodes.Status304NotModified);
            }

            var page = await _reader.GetPageAsync(issue.ResolvedPath, readerPage);
            if (page is null)
            {
                return Refuse(StatusCodes.Status404NotFound, "Page not found");
            }

            await RecordProgressAsync(issue.FilePath, readerPage, pageNames.Count, cancellationToken);

            var (data, contentType) = await _images.ConvertAsync(page.Value.Data, page.Value.ContentType, OpdsMediaTypes.ForPageStream(pageNames), cancellationToken);
            SetPrivateCaching(etag);
            Response.Headers.LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToString("R");
            return File(data, contentType);
        }
        catch (ArchiveLimitExceededException ex)
        {
            return ArchiveRefused(ex);
        }
    }

    /// <summary>The comic's original file, with range support so that clients can resume a download.</summary>
    [HttpGet("issues/{id:int}/file")]
    [HttpHead("issues/{id:int}/file")]
    // A large file over a slow connection takes as long as it takes.
    [DisableRequestTimeout]
    [FailureMessage("Error reading the comic file")]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        if (await ResolveIssueAsync(id, cancellationToken) is not { } issue)
        {
            return IssueNotFound();
        }

        FileStream stream;
        try
        {
            // Readers and writers elsewhere in the app can have the file open; it may also be renamed during a download.
            stream = System.IO.File.Open(issue.ResolvedPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return IssueNotFound();
        }

        var info = new FileInfo(issue.ResolvedPath);
        var etag = new EntityTagHeaderValue($"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"");
        Response.Headers.CacheControl = ComicReaderController.PrivateCacheControl;
        return File(
            stream,
            OpdsMediaTypes.ForArchive(issue.FilePath),
            Path.GetFileName(issue.FilePath),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            etag,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// The file behind an opaque id: it must be a tracked comic archive inside the library, by the same containment
    /// check as the web reader. Anything else is reported as not found.
    /// </summary>
    private async Task<(string FilePath, string ResolvedPath)?> ResolveIssueAsync(int id, CancellationToken cancellationToken)
    {
        var filePath = await _fileStore.GetFilePathAsync(id, cancellationToken);
        if (filePath is null || !ComicFileExtensions.IsComicArchive(filePath))
        {
            return null;
        }

        if (!LibraryPathValidator.TryResolveWithin(filePath, out var resolvedPath, _settings.CurrentValue.WatchedDirectory))
        {
            _logger.LogWarning("Refused an OPDS request for a file outside the watched directory: {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }

        return (filePath, resolvedPath);
    }

    private async Task<List<OverviewSeriesCard>> OverviewCardsAsync(Func<OverviewResult, List<OverviewSeriesCard>> row, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId;
        return userId is null ? [] : row(await _overview.GetOverviewAsync(userId, cancellationToken));
    }

    private async Task<IActionResult> SeriesNavigationFeedAsync(OpdsLinks links, OpdsFeedInfo feed, IReadOnlyList<SeriesSummaryDto> series, CancellationToken cancellationToken)
    {
        var coverPaths = series.Where(s => !string.IsNullOrEmpty(s.CoverFilePath)).Select(s => s.CoverFilePath).Distinct().ToArray();
        var coverIds = await _fileStore.GetFileIdsAsync(coverPaths, cancellationToken);

        var entries = series.Select(s => OpdsFeedBuilder.SeriesEntry(
            links,
            s.Id,
            s.Title,
            DateTimeOffset.FromUnixTimeSeconds(s.LatestModified),
            s.Synopsis,
            coverIds.TryGetValue(s.CoverFilePath, out var coverId) ? coverId : null,
            s.Genres));
        return Feed(OpdsFeedBuilder.BuildFeed(links, feed, entries), feed.MediaType);
    }

    private async Task<IReadOnlyList<XElement>> BuildIssueEntriesAsync(OpdsLinks links, IReadOnlyList<(string SeriesTitle, SeriesIssueDto Issue)> issues, CancellationToken cancellationToken)
    {
        var paths = issues.Select(i => i.Issue.FilePath).ToArray();
        var ids = await _fileStore.GetFileIdsAsync(paths, cancellationToken);
        IReadOnlyDictionary<string, ReadingProgress> progress = CurrentUserId is { } userId
            ? await _progress.GetProgressForContentAsync(userId, paths, cancellationToken)
            : new Dictionary<string, ReadingProgress>();

        var pageLists = new List<string>?[issues.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, issues.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentListings, CancellationToken = cancellationToken },
            async (index, _) => pageLists[index] = await ReadPageNamesAsync(issues[index].Issue.FilePath));

        var entries = new List<XElement>(issues.Count);
        for (var i = 0; i < issues.Count; i++)
        {
            var (seriesTitle, issue) = issues[i];
            if (!ids.TryGetValue(issue.FilePath, out var fileId))
            {
                continue;
            }

            var pages = pageLists[i];
            var read = progress.GetValueOrDefault(issue.FilePath);
            entries.Add(OpdsFeedBuilder.IssueEntry(links, new OpdsIssueEntry(
                fileId,
                IssueTitle(seriesTitle, issue),
                DateTimeOffset.FromUnixTimeSeconds(issue.Modified),
                issue.Size,
                OpdsMediaTypes.ForArchive(issue.FileName),
                issue.Publisher,
                issue.Year,
                pages?.Count ?? 0,
                pages is null ? OpdsMediaTypes.Jpeg : OpdsMediaTypes.ForPageStream(pages),
                read?.CurrentPage,
                read?.LastReadAt)));
        }

        return entries;
    }

    private async Task<List<string>?> ReadPageNamesAsync(string filePath)
    {
        if (!LibraryPathValidator.TryResolveWithin(filePath, out var resolvedPath, _settings.CurrentValue.WatchedDirectory))
        {
            return null;
        }

        var info = new FileInfo(resolvedPath);
        if (!info.Exists)
        {
            return null;
        }

        // A feed lists every issue it shows, opened or not, and the reader does not remember a listing that failed:
        // without this a corrupt file would be reopened, and logged as an error, on every view of every feed.
        var unreadableKey = string.Create(CultureInfo.InvariantCulture, $"{UnreadableCacheKeyPrefix}{info.Length:x}:{info.LastWriteTimeUtc.Ticks:x}:{resolvedPath}");
        if (_cache.TryGetValue(unreadableKey, out _))
        {
            return null;
        }

        try
        {
            var names = await _reader.GetPageNamesAsync(resolvedPath);
            if (names.Count == 0)
            {
                _cache.Set(unreadableKey, true, UnreadableRememberedFor);
            }

            return names;
        }
        catch (ArchiveLimitExceededException)
        {
            // The file can still be downloaded; only its stream is left out. The reader service has logged why.
            return null;
        }
    }

    /// <summary>The page asked for. Anything that is not a page number means page 1, so an odd link still gets a feed.</summary>
    private static int PageNumber(string? page) =>
        int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 1;

    private static string IssueTitle(string seriesTitle, SeriesIssueDto issue)
    {
        var title = string.IsNullOrWhiteSpace(issue.Issue)
            ? Path.GetFileNameWithoutExtension(issue.FileName)
            : $"{seriesTitle} #{issue.Issue}";
        return string.IsNullOrWhiteSpace(issue.Title) ? title : $"{title} - {issue.Title}";
    }

    /// <summary>
    /// Records the page against the user, the same write as the web reader's, and marks the issue read once its last
    /// page is reached (the reader does that with its own request; PSE has none). It is bookkeeping, so a busy database
    /// is logged and does not cost the client the page.
    /// </summary>
    private async Task RecordProgressAsync(string filePath, int readerPage, int totalPages, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return;
        }

        try
        {
            await _progress.RecordPageAsync(userId, filePath, readerPage, totalPages, cancellationToken);
            if (readerPage >= totalPages)
            {
                await _fileStore.MarkFileReadAsync(filePath, true, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or SqliteException)
        {
            _logger.LogWarning(ex, "Could not record reading progress for {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
        }
    }

    private void SetPrivateCaching(string etag)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = ComicReaderController.PrivateCacheControl;
    }

    private ContentResult Feed(string document, string mediaType) => Content(document, mediaType);

    private NotFoundObjectResult IssueNotFound() => NotFound(Problem(StatusCodes.Status404NotFound, "Comic not found"));

    private UnprocessableEntityObjectResult ArchiveRefused(ArchiveLimitExceededException ex) =>
        UnprocessableEntity(Problem(StatusCodes.Status422UnprocessableEntity, ex.Message));

    private ObjectResult Refuse(int status, string error) => new(Problem(status, error)) { StatusCode = status };

    /// <summary>A problem document that also carries the <c>error</c> string every client of this API reads.</summary>
    private static ProblemDetails Problem(int status, string error)
    {
        var problem = new ProblemDetails { Status = status, Title = error };
        problem.Extensions["error"] = error;
        return problem;
    }
}
