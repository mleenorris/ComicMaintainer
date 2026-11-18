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

    /// <summary>Requests the cover picker may make: a search for the volume and its issue list.</summary>
    internal const int CoverRequests = 8;

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

        var run = new Run(this, maxRequests, retryNotFound: false) { LibraryFiles = files };
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
        // Asked for by the user: a volume ComicVine did not have last time is searched for again,
        // and a file that could not be filled counts as failed, with the reason.
        var run = new Run(this, JobMaxRequests, retryNotFound: true);
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

                var (outcome, reason) = await run.FillAsync(file, token);
                return outcome is Outcome.Failed or Outcome.NoVolume or Outcome.NoIssue
                    ? new BatchItemResult(false, reason)
                    : true;
            },
            "Filling ComicVine issue details failed");
        return _jobRegistry.StartJob(definition, filePaths);
    }

    /// <inheritdoc />
    public async Task<CoverCandidates?> GetCoverCandidatesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (await _fileStore.GetFileAsync(filePath, cancellationToken) is not { } file)
        {
            return null;
        }

        // Asked for by the user: a volume not found before is searched again, and one listed
        // before covers were kept is listed again.
        var run = new Run(this, CoverRequests, retryNotFound: true) { ListForCovers = true };
        await run.LoadAsync(cancellationToken);
        return await run.CoverCandidatesAsync(file, cancellationToken);
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
    private sealed class Run(ComicVineIssueDetailsService owner, int maxRequests, bool retryNotFound)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<(string Key, int Year, int Number), ComicVineVolumeLinkEntity> _links = new();
        private readonly Dictionary<int, List<ComicVineIssueEntity>> _issuesByVolume = new();
        private readonly Dictionary<string, string?> _synopsisBySeries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComicVineFileStateEntity> _states = new(StringComparer.Ordinal);
        private readonly int[] _counts = new int[Enum.GetValues<Outcome>().Length];
        private readonly HashSet<int> _listedThisRun = new();
        private readonly HashSet<(string Key, int Year, int Number)> _searchedThisRun = new();
        private Dictionary<(string Key, int Year, int Number), int>? _highestIssueHeld;
        private int _requests;
        private bool _stoppedEarly;

        private int RequestsLeft => maxRequests - _requests;

        /// <summary>True for the cover picker: a volume whose stored issues have no covers is listed again.</summary>
        public bool ListForCovers { get; init; }

        /// <summary>The library's files, when the run already has them; otherwise read when first needed.</summary>
        public IReadOnlyCollection<ComicFile>? LibraryFiles { get; init; }

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (var link in await db.ComicVineVolumeLinks.AsNoTracking().ToListAsync(cancellationToken))
            {
                _links[(link.SeriesKey, link.StartYear, link.VolumeNumber)] = link;
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

        public async Task<(Outcome Outcome, string? Reason)> FillAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var result = await FillCoreAsync(file, cancellationToken);
            Interlocked.Increment(ref _counts[(int)result.Outcome]);
            return result;
        }

        private async Task<(Outcome Outcome, string? Reason)> FillCoreAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata;
            if (file.IsDuplicate || metadata is null
                || string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue)
                || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                return (Outcome.Skipped, null);
            }

            var (startYear, volumeNumber) = VolumeOf(metadata);

            ComicVineIssueEntity? issue;
            string? synopsis;
            ComicVineFileStateEntity? state;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var (link, outOfRequests) = await LinkAsync(metadata.Series, startYear, volumeNumber, cancellationToken);
                if (link?.VolumeId is null)
                {
                    return (Outcome.NoVolume, outOfRequests
                        ? "No ComicVine requests were left to look up the issue's volume; the next run continues"
                        : NoVolumeReason(metadata.Series, startYear, volumeNumber));
                }

                var issues = await IssuesAsync(link, cancellationToken);
                issue = issues.FirstOrDefault(candidate => SameNumber(candidate.IssueNumber, metadata.Issue));
                if (issue is null)
                {
                    return (Outcome.NoIssue, $"ComicVine's {link.VolumeName ?? SeriesTitle(metadata.Series)} (volume {link.VolumeId}) has no issue #{metadata.Issue}");
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
                owner._logger.LogWarning("ComicVine stopped answering during an issue details run: {Reason}", LoggingHelper.SanitizeForLog(ex.Message));
                return (Outcome.Failed, ex.UserMessage);
            }
            finally
            {
                _gate.Release();
            }

            // Written again only when the file has not had this issue's details yet.
            var newest = issue.DetailsUtc is { } details && details > issue.ListedUtc ? details : issue.ListedUtc;
            if (state is not null && state.IssueId == issue.Id && state.AppliedUtc >= newest)
            {
                return (Outcome.Unchanged, null);
            }

            // A file filled from another ComicVine issue before was matched to the wrong volume:
            // what that issue wrote may be replaced.
            var previous = state is not null && state.IssueId != issue.Id
                ? await StoredIssueAsync(state.IssueId, cancellationToken)
                : null;
            var plan = ComicVineIssueFill.For(metadata, issue, synopsis, previous);
            var outcome = Outcome.Unchanged;
            if (!plan.IsEmpty)
            {
                var write = await owner._processor.WriteProviderDetailsAsync(file.FilePath, plan.Metadata, plan.Fields, plan.Fill, cancellationToken);
                if (!write.Succeeded)
                {
                    return (Outcome.Failed, "The archive could not be written");
                }

                outcome = write.Outcome == MetadataWriteOutcome.Unchanged ? Outcome.Unchanged : Outcome.Filled;
            }

            await RecordAppliedAsync(file.FilePath, issue.Id, cancellationToken);
            return (outcome, null);
        }

        public async Task<CoverCandidates> CoverCandidatesAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata;
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Series))
            {
                return NoCovers("The issue has no series name to look up on ComicVine");
            }

            if (IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                return NoCovers("Annuals, Specials and Giant-Size issues are not matched to ComicVine yet");
            }

            var (startYear, volumeNumber) = VolumeOf(metadata);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var (link, outOfRequests) = await LinkAsync(metadata.Series, startYear, volumeNumber, cancellationToken);
                if (link?.VolumeId is not { } volumeId)
                {
                    return NoCovers(outOfRequests
                        ? "No ComicVine requests were left to look up the issue's volume; try again later"
                        : NoVolumeReason(metadata.Series, startYear, volumeNumber));
                }

                var issues = await IssuesAsync(link, cancellationToken);
                var matched = string.IsNullOrWhiteSpace(metadata.Issue)
                    ? null
                    : issues.FirstOrDefault(issue => SameNumber(issue.IssueNumber, metadata.Issue));
                var covers = issues
                    .Where(issue => issue.CoverUrl is not null)
                    .OrderBy(issue => decimal.TryParse(issue.IssueNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : decimal.MaxValue)
                    .ThenBy(issue => issue.IssueNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(issue => new CoverCandidate(issue.Id, issue.IssueNumber, issue.Name, issue.CoverDate, issue.CoverUrl!, issue.CoverThumbUrl, issue.SiteUrl))
                    .ToList();
                var volumeName = link.VolumeName ?? SeriesTitle(metadata.Series);
                var reason = matched is null
                    ? $"ComicVine's {volumeName} has no issue #{metadata.Issue}; pick its cover from the others"
                    : matched.CoverUrl is null ? "ComicVine has no cover for this issue" : null;
                return new CoverCandidates(volumeId, link.VolumeName, matched?.Id, reason, covers);
            }
            catch (ExternalMetadataUnavailableException ex)
            {
                return NoCovers(ex.UserMessage);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static CoverCandidates NoCovers(string reason) => new(null, null, null, reason, []);

        /// <summary>
        /// What names the file's volume: its start year, else its volume number (a year written in
        /// <c>&lt;Volume&gt;</c> counts as the start year); both 0 when it knows neither.
        /// </summary>
        private static (int StartYear, int VolumeNumber) VolumeOf(ComicMetadata metadata)
        {
            if (metadata.VolumeYear is { } year && year > 0)
            {
                return (year, 0);
            }

            return ComicVolume.FromTag(metadata.Volume) switch
            {
                { IsYear: true } tagged => (tagged.Value, 0),
                { } number => (0, number.Value),
                null => (0, 0)
            };
        }

        private static string NoVolumeReason(string series, int startYear, int volumeNumber)
        {
            var title = SeriesTitle(series);
            if (startYear > 0)
            {
                return $"ComicVine has no {title} volume that started in {startYear}";
            }

            return volumeNumber > 0
                ? $"ComicVine has no {title} Vol. {volumeNumber}"
                : $"The issue's volume is not known and {series} has no ComicVine match: set its volume number or start year, or match the series to ComicVine";
        }

        /// <summary>
        /// The ComicVine volume the series volume belongs to: by start year, as for volume numbers
        /// (see <see cref="VolumeOrdinal.Suggest"/>); else by volume number, the publisher's Nth
        /// volume of that name (see <see cref="VolumeOrdinal.Numbered"/>); else the series' own
        /// ComicVine match. The match is read afresh every time, so matching the series to
        /// ComicVine takes effect at once. <c>OutOfRequests</c> is true when a search was needed
        /// but the run had no requests left.
        /// </summary>
        private async Task<(ComicVineVolumeLinkEntity? Link, bool OutOfRequests)> LinkAsync(
            string series,
            int startYear,
            int volumeNumber,
            CancellationToken cancellationToken)
        {
            var key = (SeriesKey(series), startYear, volumeNumber);
            if (key.Item1.Length == 0)
            {
                return (null, false);
            }

            _links.TryGetValue(key, out var link);
            var title = SeriesTitle(series);
            ExternalSeriesMetadata? volume;
            if (startYear == 0 && volumeNumber == 0)
            {
                // Costs no request, so it is never kept as "not found".
                volume = await SeriesMatchAsync(series, cancellationToken);
                if (volume is null)
                {
                    return (null, false);
                }

                if (link is not null && link.VolumeId?.ToString(CultureInfo.InvariantCulture) == volume.SourceId)
                {
                    return (link, false);
                }
            }
            else
            {
                // A volume found by its number is checked against the issues the library holds:
                // one with fewer issues than that cannot be it (see VolumeOrdinal.Numbered), even
                // when it was stored before this check existed.
                var highestHeld = startYear == 0 ? await HighestIssueHeldAsync(key, cancellationToken) : 0;
                var tooShort = link?.VolumeId is { } linkedVolume && highestHeld > 0
                               && await HighestListedIssueAsync(linkedVolume, cancellationToken) is { } listed
                               && listed < highestHeld;
                if (link is not null
                    && !tooShort
                    && (link.VolumeId is not null
                        || _searchedThisRun.Contains(key)
                        || (!retryNotFound && DateTime.UtcNow - link.LinkedUtc < NotFoundRetry)))
                {
                    return (link, false);
                }

                if (tooShort && _searchedThisRun.Contains(key))
                {
                    return (link, false);
                }

                if (RequestsLeft < SearchRequests)
                {
                    _stoppedEarly = true;
                    return (link, true);
                }

                var volumes = await owner._volumeSearch.SearchVolumesAsync(title, cancellationToken);
                _requests += Math.Min(SearchRequests, volumes.Count / 50 + 1);
                _searchedThisRun.Add(key);
                volume = startYear > 0
                    ? VolumeOrdinal.Suggest(volumes, title, startYear)?.Match
                    : VolumeOrdinal.Numbered(volumes, title, volumeNumber, (await SeriesMatchAsync(series, cancellationToken))?.SourceId, highestHeld);
            }

            link ??= new ComicVineVolumeLinkEntity { SeriesKey = key.Item1, StartYear = startYear, VolumeNumber = volumeNumber };
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
            return (link, false);
        }

        /// <summary>
        /// The highest whole issue number the library has of one series volume (regular issues,
        /// not duplicates); 0 when it has none.
        /// </summary>
        private async Task<int> HighestIssueHeldAsync((string Key, int Year, int Number) key, CancellationToken cancellationToken)
        {
            if (_highestIssueHeld is null)
            {
                var files = LibraryFiles ?? (await owner._fileStore.GetAllFilesAsync(cancellationToken)).ToList();
                _highestIssueHeld = new();
                foreach (var file in files)
                {
                    var metadata = file.Metadata;
                    if (file.IsDuplicate || metadata is null || string.IsNullOrWhiteSpace(metadata.Series)
                        || !decimal.TryParse(metadata.Issue, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                        || number < 1 || number > 100000
                        || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
                    {
                        continue;
                    }

                    var (startYear, volumeNumber) = VolumeOf(metadata);
                    var group = (SeriesKey(metadata.Series), startYear, volumeNumber);
                    _highestIssueHeld[group] = Math.Max(_highestIssueHeld.GetValueOrDefault(group), (int)number);
                }
            }

            return _highestIssueHeld.GetValueOrDefault(key);
        }

        private async Task<ComicVineIssueEntity?> StoredIssueAsync(int issueId, CancellationToken cancellationToken)
        {
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.ComicVineIssues.AsNoTracking().FirstOrDefaultAsync(issue => issue.Id == issueId, cancellationToken);
        }

        /// <summary>The highest whole issue number stored for a ComicVine volume; null when none is stored yet.</summary>
        private async Task<int?> HighestListedIssueAsync(int volumeId, CancellationToken cancellationToken)
        {
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            var numbers = await db.ComicVineIssues.AsNoTracking()
                .Where(issue => issue.VolumeId == volumeId)
                .Select(issue => issue.IssueNumber)
                .ToListAsync(cancellationToken);
            var parsed = numbers
                .Select(text => decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
                .Where(n => n is not null)
                .ToList();
            return parsed.Count == 0 ? null : parsed.Max();
        }

        /// <summary>The series' own ComicVine match, or null when its match is another provider's or there is none.</summary>
        private async Task<ExternalSeriesMetadata?> SeriesMatchAsync(string series, CancellationToken cancellationToken)
            => await owner._seriesCache.GetByTitleAsync(series, cancellationToken) is { } record
               && string.Equals(record.Source, "ComicVine", StringComparison.OrdinalIgnoreCase)
               && record.LookupStatus is "success" or "manual_match"
                ? new ExternalSeriesMetadata { CanonicalTitle = record.CanonicalTitle, SourceId = record.SourceId }
                : null;

        private async Task<List<ComicVineIssueEntity>> IssuesAsync(ComicVineVolumeLinkEntity link, CancellationToken cancellationToken)
        {
            var volumeId = link.VolumeId!.Value;
            if (!_issuesByVolume.TryGetValue(volumeId, out var issues))
            {
                await using var read = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                issues = await read.ComicVineIssues.AsNoTracking().Where(issue => issue.VolumeId == volumeId).ToListAsync(cancellationToken);
                _issuesByVolume[volumeId] = issues;
            }

            // The cover picker also lists again a volume listed before covers were kept.
            var stale = link.IssuesListedUtc is not { } listed || DateTime.UtcNow - listed >= IssueListRefresh
                        || (ListForCovers && issues.Count > 0 && issues.All(issue => issue.CoverUrl is null));
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
                              || issue.SiteUrl != summary.SiteUrl
                              || issue.CoverUrl != Truncate(summary.CoverUrl, 512)
                              || issue.CoverThumbUrl != Truncate(summary.CoverThumbUrl, 512);
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
                issue.CoverUrl = Truncate(summary.CoverUrl, 512);
                issue.CoverThumbUrl = Truncate(summary.CoverThumbUrl, 512);
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
