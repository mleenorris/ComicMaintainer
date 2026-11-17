using System.Globalization;
using System.Text.Json;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Fills library files with ComicVine's issue details: the title, cover date and summary from
/// the issue list of their volume (100 issues to a request), and the writers and story arcs from
/// each issue's own page (a request per issue). ComicVine allows about 200 requests an hour, so
/// every run has a request budget; what it cannot fetch waits for the next run, and files whose
/// details are already stored are filled without asking ComicVine again.
/// </summary>
/// <remarks>
/// A file's ComicVine volume is the one named like its series that started in the file's volume
/// start year, counted as for volume numbers (see <see cref="VolumeOrdinal"/>). A file that does
/// not know its start year uses the series' own ComicVine match, if it has one. Annuals and
/// Specials are left out: ComicVine lists them as volumes of their own.
/// </remarks>
public sealed class ComicVineIssueDetailsService : IComicVineIssueDetailsService
{
    /// <summary>How long a series volume ComicVine did not have is left before it is searched again.</summary>
    internal static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(30);

    /// <summary>How long a volume's issue list is used before it is listed again for new issues.</summary>
    internal static readonly TimeSpan IssueListRefresh = TimeSpan.FromDays(7);

    /// <summary>Requests a series job may make; ComicVine's hourly limit applies on top.</summary>
    internal const int JobMaxRequests = 250;

    /// <summary>Requests one search for a volume can take (<see cref="IComicVineVolumeSearch"/> reads up to three pages).</summary>
    private const int SearchRequests = 3;

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IFileStoreService _fileStore;
    private readonly IComicVineVolumeSearch _volumeSearch;
    private readonly IComicVineCatalogClient _client;
    private readonly ISeriesMetadataCacheService _seriesCache;
    private readonly IComicProcessorService _processor;
    private readonly IBatchJobRegistry _jobRegistry;
    private readonly ILogger<ComicVineIssueDetailsService> _logger;

    public ComicVineIssueDetailsService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IFileStoreService fileStore,
        IComicVineVolumeSearch volumeSearch,
        IComicVineCatalogClient client,
        ISeriesMetadataCacheService seriesCache,
        IComicProcessorService processor,
        IBatchJobRegistry jobRegistry,
        ILogger<ComicVineIssueDetailsService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _fileStore = fileStore;
        _volumeSearch = volumeSearch;
        _client = client;
        _seriesCache = seriesCache;
        _processor = processor;
        _jobRegistry = jobRegistry;
        _logger = logger;
    }

    /// <summary>
    /// The key a series' volumes are linked under: its name without a trailing volume or
    /// "Annual", in letters and digits only, lower case (<c>All-New X-Men (2016)</c> gives
    /// <c>allnewxmen</c>).
    /// </summary>
    public static string SeriesKey(string series)
        => Key(SeriesTitle(series));

    /// <summary>The series' name as ComicVine names the volume: without a trailing volume or "Annual".</summary>
    internal static string SeriesTitle(string series)
        => AnnualIssue.WithoutAnnual(ComicVolume.WithoutVolume(series.Trim())).Trim();

    /// <inheritdoc />
    public async Task<ComicVineDetailsRunSummary> RunAsync(
        IReadOnlyCollection<string>? filePaths,
        int maxRequests,
        CancellationToken cancellationToken = default)
    {
        var files = filePaths is null
            ? (await _fileStore.GetAllFilesAsync(cancellationToken)).ToList()
            : await LoadFilesAsync(filePaths, cancellationToken);

        var run = new Run(this, maxRequests);
        await run.LoadAsync(cancellationToken);
        foreach (var file in files.OrderBy(f => f.FilePath, StringComparer.Ordinal))
        {
            await run.FillAsync(file, cancellationToken);
        }

        return run.Summary();
    }

    /// <inheritdoc />
    public Guid StartJob(IReadOnlyCollection<string> filePaths)
    {
        var run = new Run(this, JobMaxRequests);
        var loaded = new Lazy<Task>(() => run.LoadAsync(CancellationToken.None));
        var definition = new BatchJobDefinition(
            "ComicVineIssueDetails",
            "filling ComicVine issue details for",
            async (filePath, token) =>
            {
                await loaded.Value;
                var file = await _fileStore.GetFileAsync(filePath, token);
                if (file is null)
                {
                    return new BatchItemResult(false, "File not found");
                }

                var outcome = await run.FillAsync(file, token);
                return outcome == Outcome.Failed ? new BatchItemResult(false, run.LastError) : true;
            },
            "Filling ComicVine issue details failed");
        return _jobRegistry.StartJob(definition, filePaths);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeriesStoryArc>> GetStoryArcsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var states = await db.ComicVineFileStates.AsNoTracking()
            .Where(state => paths.Contains(state.FilePath))
            .ToListAsync(cancellationToken);
        var issueIds = states.Select(state => state.IssueId).Distinct().ToList();
        var arcsByIssue = (await db.ComicVineIssues.AsNoTracking()
                .Where(issue => issueIds.Contains(issue.Id) && issue.StoryArcsJson != null)
                .Select(issue => new { issue.Id, issue.StoryArcsJson })
                .ToListAsync(cancellationToken))
            .ToDictionary(issue => issue.Id, issue => ComicVineIssueFill.StoryArcs(issue.StoryArcsJson));

        var arcs = new Dictionary<int, (string Name, List<string> Paths)>();
        foreach (var state in states.OrderBy(state => state.FilePath, StringComparer.Ordinal))
        {
            if (!arcsByIssue.TryGetValue(state.IssueId, out var issueArcs))
            {
                continue;
            }

            foreach (var arc in issueArcs)
            {
                if (!arcs.TryGetValue(arc.Id, out var entry))
                {
                    entry = (arc.Name, new List<string>());
                    arcs[arc.Id] = entry;
                }

                entry.Paths.Add(state.FilePath);
            }
        }

        return arcs
            .Select(pair => new SeriesStoryArc(pair.Key, pair.Value.Name, pair.Value.Paths))
            .OrderBy(arc => arc.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<StoryArcReadingOrder?> GetStoryArcAsync(int storyArcId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.ComicVineStoryArcs.FirstOrDefaultAsync(arc => arc.Id == storyArcId, cancellationToken);
        if (stored is null)
        {
            var fetched = await _client.GetStoryArcAsync(storyArcId, cancellationToken);
            if (fetched is null)
            {
                return null;
            }

            stored = new ComicVineStoryArcEntity
            {
                Id = fetched.Id,
                Name = Truncate(fetched.Name, 512)!,
                Summary = Truncate(fetched.Summary, 4000),
                SiteUrl = Truncate(fetched.SiteUrl, 512),
                IssuesJson = JsonSerializer.Serialize(fetched.Issues, JsonOptions),
                FetchedUtc = DateTime.UtcNow
            };
            db.ComicVineStoryArcs.Add(stored);
            await db.SaveChangesAsync(cancellationToken);
        }

        var issues = ReadArcIssues(stored.IssuesJson);
        var ids = issues.Select(issue => issue.Id).ToList();
        var files = await db.ComicVineFileStates.AsNoTracking()
            .Where(state => ids.Contains(state.IssueId))
            .OrderBy(state => state.FilePath)
            .ToListAsync(cancellationToken);

        // A file renamed or deleted since it was filled is not offered.
        var fileByIssue = new Dictionary<int, string>();
        foreach (var state in files)
        {
            if (fileByIssue.ContainsKey(state.IssueId))
            {
                continue;
            }

            if (await _fileStore.FileExistsAsync(state.FilePath, cancellationToken))
            {
                fileByIssue[state.IssueId] = state.FilePath;
            }
        }

        return new StoryArcReadingOrder(
            stored.Id,
            stored.Name,
            stored.Summary,
            stored.SiteUrl,
            issues.Select(issue => new StoryArcEntry(
                issue.Id,
                issue.Name,
                issue.IssueNumber,
                issue.Volume,
                fileByIssue.GetValueOrDefault(issue.Id))).ToList());
    }

    private async Task<List<ComicFile>> LoadFilesAsync(IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken)
    {
        var files = new List<ComicFile>();
        foreach (var path in filePaths.Distinct(StringComparer.Ordinal))
        {
            var file = await _fileStore.GetFileAsync(path, cancellationToken);
            if (file is null)
            {
                continue;
            }

            files.Add(file);
        }

        return files;
    }

    private static IReadOnlyList<ComicVineArcIssue> ReadArcIssues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ComicVineArcIssue>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Key(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Truncate(string? text, int length)
        => text is null || text.Length <= length ? text : text[..length];

    private static bool SameNumber(string left, string right)
        => decimal.TryParse(left, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
           && decimal.TryParse(right, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)
            ? a == b
            : string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    internal enum Outcome { Filled, Unchanged, NoVolume, NoIssue, Skipped, Failed }

    /// <summary>
    /// One run over a set of files, sharing what it has looked up. Looking things up on ComicVine
    /// and storing them is done one file at a time (the job runs files in parallel); writing the
    /// archives is not.
    /// </summary>
    private sealed class Run(ComicVineIssueDetailsService owner, int maxRequests)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<(string Key, int Year), ComicVineVolumeLinkEntity> _links = new();
        private readonly Dictionary<int, List<ComicVineIssueEntity>> _issuesByVolume = new();
        private readonly Dictionary<string, string?> _synopsisBySeries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComicVineFileStateEntity> _states = new(StringComparer.Ordinal);
        private readonly int[] _counts = new int[Enum.GetValues<Outcome>().Length];
        private readonly HashSet<int> _listedThisRun = new();
        private int _requests;
        private bool _stoppedEarly;

        public string? LastError { get; private set; }

        private int RequestsLeft => maxRequests - _requests;

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (var link in await db.ComicVineVolumeLinks.AsNoTracking().ToListAsync(cancellationToken))
            {
                _links[(link.SeriesKey, link.StartYear)] = link;
            }

            foreach (var state in await db.ComicVineFileStates.AsNoTracking().ToListAsync(cancellationToken))
            {
                _states[state.FilePath] = state;
            }
        }

        public ComicVineDetailsRunSummary Summary()
            => new(
                _counts.Sum(),
                _counts[(int)Outcome.Filled],
                _counts[(int)Outcome.Unchanged],
                _counts[(int)Outcome.NoVolume],
                _counts[(int)Outcome.NoIssue],
                _counts[(int)Outcome.Skipped],
                _counts[(int)Outcome.Failed],
                _requests,
                _stoppedEarly);

        public async Task<Outcome> FillAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var outcome = await FillCoreAsync(file, cancellationToken);
            Interlocked.Increment(ref _counts[(int)outcome]);
            return outcome;
        }

        private async Task<Outcome> FillCoreAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata;
            if (file.IsDuplicate || metadata is null
                || string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue)
                || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                return Outcome.Skipped;
            }

            ComicVineIssueEntity? issue;
            string? synopsis;
            ComicVineFileStateEntity? state;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var link = await LinkAsync(metadata.Series, metadata.VolumeYear ?? 0, cancellationToken);
                if (link?.VolumeId is not { } volumeId)
                {
                    return Outcome.NoVolume;
                }

                var issues = await IssuesAsync(link, cancellationToken);
                issue = issues.FirstOrDefault(candidate => SameNumber(candidate.IssueNumber, metadata.Issue));
                if (issue is null)
                {
                    return Outcome.NoIssue;
                }

                if (issue.DetailsUtc is null && RequestsLeft > 0)
                {
                    await FetchDetailsAsync(issue, cancellationToken);
                }

                synopsis = await SeriesSynopsisAsync(metadata.Series, cancellationToken);
                state = _states.GetValueOrDefault(file.FilePath);
            }
            catch (ExternalMetadataUnavailableException ex)
            {
                // ComicVine is limiting requests or not answering: stop asking it for the rest of
                // this run; files already looked up are still filled.
                _stoppedEarly = true;
                _requests = Math.Max(_requests, maxRequests);
                LastError = ex.UserMessage;
                owner._logger.LogWarning("ComicVine stopped answering during an issue details run: {Reason}", LoggingHelper.SanitizeForLog(ex.Message));
                return Outcome.Failed;
            }
            finally
            {
                _gate.Release();
            }

            // Written again only when the file has not had this issue's details yet.
            var newest = issue.DetailsUtc is { } details && details > issue.ListedUtc ? details : issue.ListedUtc;
            if (state is not null && state.IssueId == issue.Id && state.AppliedUtc >= newest)
            {
                return Outcome.Unchanged;
            }

            var plan = ComicVineIssueFill.For(metadata, issue, synopsis);
            var outcome = Outcome.Unchanged;
            if (!plan.IsEmpty)
            {
                var write = await owner._processor.WriteProviderDetailsAsync(file.FilePath, plan.Metadata, plan.Fields, plan.Fill, cancellationToken);
                if (!write.Succeeded)
                {
                    LastError = "The archive could not be written";
                    return Outcome.Failed;
                }

                outcome = write.Outcome == MetadataWriteOutcome.Unchanged ? Outcome.Unchanged : Outcome.Filled;
            }

            await RecordAppliedAsync(file.FilePath, issue.Id, cancellationToken);
            return outcome;
        }

        private async Task<ComicVineVolumeLinkEntity?> LinkAsync(string series, int startYear, CancellationToken cancellationToken)
        {
            var key = (SeriesKey(series), startYear);
            if (key.Item1.Length == 0)
            {
                return null;
            }

            if (_links.TryGetValue(key, out var link)
                && (link.VolumeId is not null || DateTime.UtcNow - link.LinkedUtc < NotFoundRetry))
            {
                return link;
            }

            var title = SeriesTitle(series);
            ExternalSeriesMetadata? volume = null;
            if (startYear > 0)
            {
                if (RequestsLeft < SearchRequests)
                {
                    _stoppedEarly = true;
                    return link;
                }

                var volumes = await owner._volumeSearch.SearchVolumesAsync(title, cancellationToken);
                _requests += Math.Min(SearchRequests, volumes.Count / 50 + 1);
                volume = VolumeOrdinal.Suggest(volumes, title, startYear)?.Match;
            }
            else if (await owner._seriesCache.GetByTitleAsync(series, cancellationToken) is { } record
                     && string.Equals(record.Source, "ComicVine", StringComparison.OrdinalIgnoreCase)
                     && record.LookupStatus is "success" or "manual_match")
            {
                // No start year: the series' own ComicVine match is its only volume we know of.
                volume = new ExternalSeriesMetadata { CanonicalTitle = record.CanonicalTitle, SourceId = record.SourceId };
            }

            link ??= new ComicVineVolumeLinkEntity { SeriesKey = key.Item1, StartYear = startYear };
            link.VolumeId = int.TryParse(volume?.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
            link.VolumeName = Truncate(volume?.CanonicalTitle, 512);
            link.Publisher = Truncate(volume?.Publisher, 256);
            link.LinkedUtc = DateTime.UtcNow;
            link.IssuesListedUtc = null;

            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (link.Id == 0)
            {
                db.ComicVineVolumeLinks.Add(link);
            }
            else
            {
                db.ComicVineVolumeLinks.Update(link);
            }

            await db.SaveChangesAsync(cancellationToken);
            _links[key] = link;
            return link;
        }

        private async Task<List<ComicVineIssueEntity>> IssuesAsync(ComicVineVolumeLinkEntity link, CancellationToken cancellationToken)
        {
            var volumeId = link.VolumeId!.Value;
            if (!_issuesByVolume.TryGetValue(volumeId, out var issues))
            {
                await using var read = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                issues = await read.ComicVineIssues.AsNoTracking().Where(issue => issue.VolumeId == volumeId).ToListAsync(cancellationToken);
                _issuesByVolume[volumeId] = issues;
            }

            var stale = link.IssuesListedUtc is not { } listed || DateTime.UtcNow - listed >= IssueListRefresh;
            if (!stale || _listedThisRun.Contains(volumeId))
            {
                return issues;
            }

            if (RequestsLeft <= 0)
            {
                _stoppedEarly = true;
                return issues;
            }

            var budget = RequestsLeft;
            var listing = await owner._client.ListVolumeIssuesAsync(volumeId, budget, cancellationToken);
            _requests += listing.Requests;
            _listedThisRun.Add(volumeId);

            // The list is complete only when ComicVine was asked and the last page was not cut
            // off by the budget (a full last page with the budget used up means more pages exist).
            var complete = owner._client.IsConfigured
                           && !(listing.Requests >= budget && listing.Value.Count >= listing.Requests * 100);
            if (!complete)
            {
                _stoppedEarly = true;
            }

            var now = DateTime.UtcNow;

            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            var known = issues.ToDictionary(issue => issue.Id);
            foreach (var summary in listing.Value)
            {
                var isNew = !known.TryGetValue(summary.Id, out var issue);
                issue ??= new ComicVineIssueEntity { Id = summary.Id, VolumeId = volumeId };
                var changed = isNew
                              || issue.IssueNumber != summary.IssueNumber || issue.Name != summary.Name
                              || issue.CoverDate != summary.CoverDate || issue.Summary != Truncate(summary.Summary, 4000)
                              || issue.SiteUrl != summary.SiteUrl;
                if (!changed)
                {
                    continue;
                }

                issue.VolumeId = volumeId;
                issue.IssueNumber = Truncate(summary.IssueNumber, 50)!;
                issue.Name = Truncate(summary.Name, 512);
                issue.CoverDate = Truncate(summary.CoverDate, 10);
                issue.Summary = Truncate(summary.Summary, 4000);
                issue.SiteUrl = Truncate(summary.SiteUrl, 512);
                issue.ListedUtc = now;
                if (isNew)
                {
                    db.ComicVineIssues.Add(issue);
                    issues.Add(issue);
                }
                else
                {
                    db.ComicVineIssues.Update(issue);
                }
            }

            // An incomplete list is not stamped, so the next run lists the volume again.
            if (complete)
            {
                link.IssuesListedUtc = now;
                db.ComicVineVolumeLinks.Update(link);
            }

            await db.SaveChangesAsync(cancellationToken);
            return issues;
        }

        private async Task FetchDetailsAsync(ComicVineIssueEntity issue, CancellationToken cancellationToken)
        {
            var details = await owner._client.GetIssueDetailsAsync(issue.Id, cancellationToken);
            _requests++;
            issue.Writers = details is { Writers.Count: > 0 } ? Truncate(string.Join(", ", details.Writers), 1024) : null;
            issue.StoryArcsJson = details is { StoryArcs.Count: > 0 } ? ComicVineIssueFill.StoryArcsJson(details.StoryArcs, 4000) : null;
            issue.DetailsUtc = DateTime.UtcNow;

            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.ComicVineIssues.Update(issue);
            await db.SaveChangesAsync(cancellationToken);
        }

        private async Task<string?> SeriesSynopsisAsync(string series, CancellationToken cancellationToken)
        {
            if (!_synopsisBySeries.TryGetValue(series, out var synopsis))
            {
                synopsis = (await owner._seriesCache.GetByTitleAsync(series, cancellationToken))?.Synopsis;
                _synopsisBySeries[series] = synopsis;
            }

            return synopsis;
        }

        private async Task RecordAppliedAsync(string filePath, int issueId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                var known = _states.TryGetValue(filePath, out var state);
                state ??= new ComicVineFileStateEntity { FilePath = filePath };
                state.IssueId = issueId;
                state.AppliedUtc = DateTime.UtcNow;
                if (known)
                {
                    db.ComicVineFileStates.Update(state);
                }
                else
                {
                    db.ComicVineFileStates.Add(state);
                }

                await db.SaveChangesAsync(cancellationToken);
                _states[filePath] = state;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
