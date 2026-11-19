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

    /// <summary>
    /// Pages of ComicVine's volume list read by name (see
    /// <see cref="IComicVineVolumeSearch.ListVolumesNamedAsync"/>) when the ranked search finds no
    /// fitting volume: 500 volumes containing the name at most.
    /// </summary>
    private const int NamedListRequests = 5;

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

    /// <summary>The page of a ComicVine volume; ComicVine finds a volume by its id alone.</summary>
    internal static string VolumeSiteUrl(int volumeId)
        => $"https://comicvine.gamespot.com/volume/4050-{volumeId.ToString(CultureInfo.InvariantCulture)}/";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SeriesVolumeLink>> GetVolumeLinksAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        // The regular issues of each volume (Annuals and Specials are volumes of their own on
        // ComicVine, and are not matched), with a series name to find its link by.
        var groups = new Dictionary<(string Key, int Year, int Number), (string Series, int Issues)>();
        foreach (var file in await LoadFilesAsync(filePaths, cancellationToken))
        {
            var metadata = file.Metadata;
            if (file.IsDuplicate || metadata is null || string.IsNullOrWhiteSpace(metadata.Series)
                || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                continue;
            }

            var (startYear, volumeNumber) = Run.VolumeOf(metadata);
            var key = (SeriesKey(metadata.Series), startYear, volumeNumber);
            groups[key] = groups.TryGetValue(key, out var group) ? (group.Series, group.Issues + 1) : (metadata.Series, 1);
        }

        if (groups.Count == 0)
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await StoredVolumesAsync(db, groups.ToDictionary(group => group.Key, group => group.Value.Series), cancellationToken);

        var result = new List<SeriesVolumeLink>();
        foreach (var ((key, startYear, volumeNumber), (_, issues)) in groups)
        {
            var volume = stored[(key, startYear, volumeNumber)];
            result.Add(new SeriesVolumeLink(
                volumeNumber,
                startYear,
                issues,
                volume.Id,
                volume.Id is null ? null : volume.Name,
                volume.Id is null ? null : volume.Publisher,
                volume.Id is { } id ? VolumeSiteUrl(id) : null));
        }

        return result
            .OrderBy(volume => volume.VolumeNumber == 0 && volume.StartYear == 0 ? 1 : 0)
            .ThenBy(volume => volume.StartYear == 0 ? int.MaxValue : volume.StartYear)
            .ThenBy(volume => volume.VolumeNumber)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IssueComicVineLink>> GetIssueLinksAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var files = await LoadFilesAsync(filePaths, cancellationToken);
        if (files.Count == 0)
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var paths = files.Select(file => file.FilePath).ToList();
        var filled = await db.ComicVineFileStates.AsNoTracking()
            .Where(state => paths.Contains(state.FilePath))
            .ToDictionaryAsync(state => state.FilePath, state => state.IssueId, StringComparer.Ordinal, cancellationToken);

        // Files whose tags name their issue (see ComicVineTagId); then the other regular issues:
        // the volume their names give (Annuals and Specials are not matched).
        var taggedOf = files
            .Where(file => !filled.ContainsKey(file.FilePath) && file.Metadata?.ComicVineIssueId is not null)
            .ToDictionary(file => file.FilePath, file => file.Metadata!.ComicVineIssueId!.Value, StringComparer.Ordinal);
        var volumeOfFile = new Dictionary<string, (string Key, int Year, int Number)>(StringComparer.Ordinal);
        var seriesOfVolume = new Dictionary<(string Key, int Year, int Number), string>();
        foreach (var file in files)
        {
            var metadata = file.Metadata;
            if (filled.ContainsKey(file.FilePath) || metadata is null
                || string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue)
                || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                continue;
            }

            var (startYear, volumeNumber) = Run.VolumeOf(metadata);
            var key = (SeriesKey(metadata.Series), startYear, volumeNumber);
            if (key.Item1.Length > 0)
            {
                volumeOfFile[file.FilePath] = key;
                seriesOfVolume.TryAdd(key, metadata.Series);
            }
        }

        var volumes = await StoredVolumesAsync(db, seriesOfVolume, cancellationToken);
        var issueIds = filled.Values.Concat(taggedOf.Values).Distinct().ToList();
        var volumeIds = volumes.Values.Select(volume => volume.Id).OfType<int>().Distinct().ToList();
        var issues = await db.ComicVineIssues.AsNoTracking()
            .Where(issue => issueIds.Contains(issue.Id) || volumeIds.Contains(issue.VolumeId))
            .ToListAsync(cancellationToken);
        var issueById = issues.ToDictionary(issue => issue.Id);
        var issuesOfVolume = issues.ToLookup(issue => issue.VolumeId);

        // Names for the volumes of filled issues, which their files may no longer name.
        var namedVolumes = issues.Select(issue => issue.VolumeId).Distinct().ToList();
        var volumeNames = (await db.ComicVineVolumeLinks.AsNoTracking()
                .Where(link => link.VolumeId != null && namedVolumes.Contains(link.VolumeId.Value) && link.VolumeName != null)
                .Select(link => new { VolumeId = link.VolumeId!.Value, link.VolumeName })
                .ToListAsync(cancellationToken))
            .GroupBy(link => link.VolumeId)
            .ToDictionary(group => group.Key, group => group.First().VolumeName);

        var result = new List<IssueComicVineLink>();
        foreach (var file in files)
        {
            if (filled.TryGetValue(file.FilePath, out var issueId))
            {
                if (issueById.TryGetValue(issueId, out var issue))
                {
                    result.Add(new IssueComicVineLink(
                        file.FilePath, issue.Id, issue.IssueNumber, issue.Name, issue.SiteUrl,
                        issue.VolumeId, volumeNames.GetValueOrDefault(issue.VolumeId), VolumeSiteUrl(issue.VolumeId), Filled: true));
                }

                continue;
            }

            // A tag is trusted when its issue is stored and has the file's number.
            if (taggedOf.TryGetValue(file.FilePath, out var taggedId)
                && issueById.TryGetValue(taggedId, out var tagged)
                && (string.IsNullOrWhiteSpace(file.Metadata!.Issue) || SameNumber(tagged.IssueNumber, file.Metadata.Issue)))
            {
                result.Add(new IssueComicVineLink(
                    file.FilePath, tagged.Id, tagged.IssueNumber, tagged.Name, tagged.SiteUrl,
                    tagged.VolumeId, volumeNames.GetValueOrDefault(tagged.VolumeId), VolumeSiteUrl(tagged.VolumeId), Filled: false));
                continue;
            }

            if (!volumeOfFile.TryGetValue(file.FilePath, out var key) || volumes[key].Id is not { } volumeId)
            {
                continue;
            }

            var matched = issuesOfVolume[volumeId].FirstOrDefault(candidate => SameNumber(candidate.IssueNumber, file.Metadata!.Issue!));
            result.Add(new IssueComicVineLink(
                file.FilePath, matched?.Id, matched?.IssueNumber, matched?.Name, matched?.SiteUrl,
                volumeId, volumes[key].Name ?? volumeNames.GetValueOrDefault(volumeId), VolumeSiteUrl(volumeId), Filled: false));
        }

        return result;
    }

    /// <summary>
    /// The stored ComicVine volume of each series volume (series key, start year, volume number),
    /// from the links issue-details runs made; a volume whose files name neither a start year nor
    /// a number is the series' own ComicVine match, read afresh. Id is null when the volume is not
    /// linked. Asks ComicVine nothing.
    /// </summary>
    private async Task<Dictionary<(string Key, int Year, int Number), (int? Id, string? Name, string? Publisher)>> StoredVolumesAsync(
        ComicMaintainerDbContext db,
        IReadOnlyDictionary<(string Key, int Year, int Number), string> seriesOfVolume,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Key, int Year, int Number), (int? Id, string? Name, string? Publisher)>();
        if (seriesOfVolume.Count == 0)
        {
            return result;
        }

        var seriesKeys = seriesOfVolume.Keys.Select(key => key.Key).Distinct().ToList();
        var links = (await db.ComicVineVolumeLinks.AsNoTracking()
                .Where(link => seriesKeys.Contains(link.SeriesKey))
                .ToListAsync(cancellationToken))
            .ToDictionary(link => (link.SeriesKey, link.StartYear, link.VolumeNumber));

        foreach (var ((key, startYear, volumeNumber), series) in seriesOfVolume)
        {
            links.TryGetValue((key, startYear, volumeNumber), out var link);
            int? volumeId = link?.VolumeId;
            var name = link?.VolumeName;
            if (startYear == 0 && volumeNumber == 0
                && await _seriesCache.GetByTitleAsync(series, cancellationToken) is { } record
                && string.Equals(record.Source, "ComicVine", StringComparison.OrdinalIgnoreCase)
                && record.LookupStatus is "success" or "manual_match"
                && int.TryParse(record.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesVolume))
            {
                // Files that name neither belong to the series' own match, read afresh.
                volumeId = seriesVolume;
                name ??= record.CanonicalTitle;
            }

            result[(key, startYear, volumeNumber)] = (volumeId, name, link?.Publisher);
        }

        return result;
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

        // Names of the volumes of issues read by their id this run.
        private readonly Dictionary<int, string> _volumeNames = new();
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
            if (file.IsDuplicate || metadata is null)
            {
                return (Outcome.Skipped, null);
            }

            var regular = IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is null;
            if (metadata.ComicVineIssueId is null
                && (string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue) || !regular))
            {
                return (Outcome.Skipped, null);
            }

            ComicVineIssueEntity? issue = null;
            string? synopsis;
            ComicVineFileStateEntity? state;
            var replacesTag = false;
            var fromTag = false;
            int? rejectedTag = null;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Read under the gate: files of a job are filled in parallel, and RecordAppliedAsync
                // writes the states under it.
                state = _states.GetValueOrDefault(file.FilePath);

                // Tags that name the issue need no matching, and fill Annuals and Specials too. A tag
                // this application wrote (the file was filled from that issue) is only its own earlier
                // match: the issue is matched again, so a volume number or start year the user set since
                // takes effect, and the tag follows the new match.
                if (metadata.ComicVineIssueId is { } taggedId)
                {
                    if (metadata.ComicVineIssueMatchedByName)
                    {
                        replacesTag = true;
                    }
                    else if (state?.RejectedTagId == taggedId)
                    {
                        // ComicVine did not give it before: matched by name again, without asking.
                        rejectedTag = taggedId;
                    }
                    else
                    {
                        var (check, tagged) = await TaggedIssueAsync(file, taggedId, regular, cancellationToken);
                        switch (check)
                        {
                            case TagCheck.Trusted:
                                issue = tagged;
                                fromTag = true;
                                break;
                            case TagCheck.Unavailable:
                                return (Outcome.NoVolume, "No ComicVine requests were left to read the issue its tags name; the next run continues");
                            case TagCheck.Wrong:
                                replacesTag = true;
                                break;
                            case TagCheck.Unknown:
                                rejectedTag = taggedId;
                                break;
                        }
                    }
                }

                if (issue is null)
                {
                    if (string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue) || !regular)
                    {
                        return (Outcome.Skipped, null);
                    }

                    var (startYear, volumeNumber) = VolumeOf(metadata);
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
                }

                if (issue.DetailsUtc is null && RequestsLeft > 0)
                {
                    await FetchDetailsAsync(issue, cancellationToken);
                }

                synopsis = string.IsNullOrWhiteSpace(metadata.Series) ? null : await SeriesSynopsisAsync(metadata.Series, cancellationToken);
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
            var plan = ComicVineIssueFill.For(metadata, issue, synopsis, previous, replacesTag && metadata.ComicVineIssueId != issue.Id, matchedByName: !fromTag);
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

            await RecordAppliedAsync(file.FilePath, issue.Id, rejectedTag, cancellationToken);
            return (outcome, null);
        }

        public async Task<CoverCandidates> CoverCandidatesAsync(ComicFile file, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata;
            if (metadata?.ComicVineIssueId is { } taggedId
                && !metadata.ComicVineIssueMatchedByName
                && await TaggedCoverCandidatesAsync(file, taggedId, cancellationToken) is { } tagged)
            {
                return tagged;
            }

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Series))
            {
                return NoCovers("The issue has no series name to look up on ComicVine");
            }

            if (IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
            {
                return NoCovers("Annuals, Specials, Giant-Size issues and One-Shots are not matched to ComicVine yet");
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
                return Covers(volumeId, link.VolumeName, link.VolumeName ?? SeriesTitle(metadata.Series), issues, matched, metadata.Issue);
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

        /// <summary>
        /// The covers of the volume of the issue the file's tags name, with that issue chosen; null
        /// when the tags cannot be trusted (see <see cref="TaggedIssueAsync"/>), so the issue is matched
        /// instead. A tag this application wrote is matched again, as when filling.
        /// </summary>
        private async Task<CoverCandidates?> TaggedCoverCandidatesAsync(ComicFile file, int taggedId, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata!;
            var regular = IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is null;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var (check, issue) = await TaggedIssueAsync(file, taggedId, regular, cancellationToken);
                if (check == TagCheck.Unavailable)
                {
                    return NoCovers("No ComicVine requests were left to read the issue its tags name; try again later");
                }

                if (check != TagCheck.Trusted || issue is null)
                {
                    return null;
                }

                // A volume stored for another series key is listed through its link, so its listing
                // date is kept; otherwise a link that is not stored is listed afresh each time.
                var link = _links.Values.FirstOrDefault(candidate => candidate.VolumeId == issue.VolumeId)
                           ?? new ComicVineVolumeLinkEntity { VolumeId = issue.VolumeId, VolumeName = _volumeNames.GetValueOrDefault(issue.VolumeId) };
                var issues = await IssuesAsync(link, cancellationToken);
                var matched = issues.FirstOrDefault(candidate => candidate.Id == issue.Id) ?? issue;
                return Covers(issue.VolumeId, link.VolumeName, link.VolumeName ?? "its volume", issues, matched, metadata.Issue);
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

        private static CoverCandidates Covers(
            int volumeId,
            string? volumeName,
            string volumeTitle,
            IEnumerable<ComicVineIssueEntity> issues,
            ComicVineIssueEntity? matched,
            string? number)
        {
            var covers = issues
                .Where(issue => issue.CoverUrl is not null)
                .OrderBy(issue => decimal.TryParse(issue.IssueNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : decimal.MaxValue)
                .ThenBy(issue => issue.IssueNumber, StringComparer.OrdinalIgnoreCase)
                .Select(issue => new CoverCandidate(issue.Id, issue.IssueNumber, issue.Name, issue.CoverDate, issue.CoverUrl!, issue.CoverThumbUrl, issue.SiteUrl))
                .ToList();
            var reason = matched is null
                ? $"ComicVine's {volumeTitle} has no issue #{number}; pick its cover from the others"
                : matched.CoverUrl is null ? "ComicVine has no cover for this issue" : null;
            return new CoverCandidates(volumeId, volumeName, matched?.Id, reason, covers);
        }

        /// <summary>What a tag naming a ComicVine issue turned out to be (see <see cref="TaggedIssueAsync"/>).</summary>
        private enum TagCheck
        {
            /// <summary>The issue, with the file's number: the file is filled from it.</summary>
            Trusted,

            /// <summary>
            /// ComicVine did not give the issue: matched by name instead, but the tag is kept, as nothing
            /// shows it is wrong.
            /// </summary>
            Unknown,

            /// <summary>The issue has another number than the file (tags copied from another file): matched by name, and replaced.</summary>
            Wrong,

            /// <summary>The issue could not be read now (no requests left, or ComicVine not configured): the file waits for the next run.</summary>
            Unavailable,
        }

        /// <summary>
        /// The ComicVine issue the file's tags name (see <see cref="ComicVineTagId"/>), read from
        /// ComicVine when it is not stored with its details yet, and what to make of the tag (see
        /// <see cref="TagCheck"/>). A trusted regular issue's volume is linked to the file's series
        /// volume when that has no ComicVine volume yet, so the series' other issues are found in it.
        /// </summary>
        private async Task<(TagCheck Check, ComicVineIssueEntity? Issue)> TaggedIssueAsync(ComicFile file, int issueId, bool regular, CancellationToken cancellationToken)
        {
            var metadata = file.Metadata!;
            var issue = await StoredIssueAsync(issueId, cancellationToken);
            if (issue?.DetailsUtc is null)
            {
                if (RequestsLeft <= 0 || !owner._client.IsConfigured)
                {
                    _stoppedEarly |= RequestsLeft <= 0;
                    return (TagCheck.Unavailable, null);
                }

                var fetched = await owner._client.GetIssueAsync(issueId, cancellationToken);
                _requests++;
                if (fetched is null)
                {
                    return (TagCheck.Unknown, null);
                }

                issue = await StoreIssueAsync(fetched, issue, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(metadata.Issue) && !SameNumber(issue.IssueNumber, metadata.Issue))
            {
                return (TagCheck.Wrong, issue);
            }

            if (regular && !string.IsNullOrWhiteSpace(metadata.Series))
            {
                await LinkTaggedVolumeAsync(metadata, issue, cancellationToken);
            }

            return (TagCheck.Trusted, issue);
        }

        /// <summary>Stores an issue read by its id, with its details, over <paramref name="stored"/> when it was listed before.</summary>
        private async Task<ComicVineIssueEntity> StoreIssueAsync(ComicVineIssue fetched, ComicVineIssueEntity? stored, CancellationToken cancellationToken)
        {
            var summary = fetched.Summary;
            var now = DateTime.UtcNow;
            var issue = stored ?? new ComicVineIssueEntity { Id = summary.Id };
            issue.VolumeId = fetched.VolumeId;
            issue.IssueNumber = Truncate(summary.IssueNumber, 50)!;
            issue.Name = Truncate(summary.Name, 512);
            issue.CoverDate = Truncate(summary.CoverDate, 10);
            issue.Summary = Truncate(summary.Summary, 4000);
            issue.SiteUrl = Truncate(summary.SiteUrl, 512);
            issue.CoverUrl = Truncate(summary.CoverUrl, 512);
            issue.CoverThumbUrl = Truncate(summary.CoverThumbUrl, 512);
            issue.ListedUtc = now;
            issue.Writers = fetched.Details.Writers.Count > 0 ? Truncate(string.Join(", ", fetched.Details.Writers), 1024) : null;
            issue.StoryArcsJson = fetched.Details.StoryArcs.Count > 0 ? ComicVineIssueFill.StoryArcsJson(fetched.Details.StoryArcs, 4000) : null;
            issue.DetailsUtc = now;
            if (fetched.VolumeName is { } volumeName)
            {
                _volumeNames[fetched.VolumeId] = Truncate(volumeName, 512)!;
            }

            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (stored is null)
            {
                db.ComicVineIssues.Add(issue);
            }
            else
            {
                db.ComicVineIssues.Update(issue);
            }

            await db.SaveChangesAsync(cancellationToken);

            // A volume listed earlier in this run gets the issue too.
            if (_issuesByVolume.TryGetValue(issue.VolumeId, out var listed))
            {
                listed.RemoveAll(candidate => candidate.Id == issue.Id);
                listed.Add(issue);
            }

            return issue;
        }

        /// <summary>
        /// Links the file's series volume to the volume of the issue its tags name, when it has no
        /// ComicVine volume yet: a tagger's id is stronger evidence than a search. A volume known
        /// neither by start year nor number goes by the series' own match instead, and a volume
        /// already linked is kept.
        /// </summary>
        private async Task LinkTaggedVolumeAsync(ComicMetadata metadata, ComicVineIssueEntity issue, CancellationToken cancellationToken)
        {
            var (startYear, volumeNumber) = VolumeOf(metadata);
            var key = (SeriesKey(metadata.Series!), startYear, volumeNumber);
            if (key.Item1.Length == 0 || (startYear == 0 && volumeNumber == 0)
                || (_links.TryGetValue(key, out var existing) && existing.VolumeId is not null))
            {
                return;
            }

            var link = existing ?? new ComicVineVolumeLinkEntity { SeriesKey = key.Item1, StartYear = startYear, VolumeNumber = volumeNumber };
            link.VolumeId = issue.VolumeId;
            link.VolumeName = _volumeNames.GetValueOrDefault(issue.VolumeId);
            link.Publisher = null;
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
        }

        private static CoverCandidates NoCovers(string reason) => new(null, null, null, reason, []);

        /// <summary>
        /// What names the file's volume: its start year, else its volume number (a year written in
        /// <c>&lt;Volume&gt;</c> counts as the start year); both 0 when it knows neither.
        /// </summary>
        internal static (int StartYear, int VolumeNumber) VolumeOf(ComicMetadata metadata)
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
                // Only a volume listed in full: one linked through a tag holds just that issue so far.
                var tooShort = link?.VolumeId is { } linkedVolume && highestHeld > 0 && link.IssuesListedUtc is not null
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
                var seriesMatch = startYear > 0 ? null : (await SeriesMatchAsync(series, cancellationToken))?.SourceId;
                volume = startYear > 0
                    ? VolumeOrdinal.Suggest(volumes, title, startYear)?.Match
                    : VolumeOrdinal.Numbered(volumes, title, volumeNumber, seriesMatch, highestHeld);

                // The ranked search can leave a much-used name's volume out: list every volume of
                // that exact name, oldest first, and look again.
                // A request is left for listing the volume's issues. A volume found by its number is
                // counted among the name's volumes, so only a complete list may add to them: the oldest
                // 500 of thousands would count the wrong ones.
                if (volume is null && RequestsLeft > 1)
                {
                    var named = await owner._volumeSearch.ListVolumesNamedAsync(title, Math.Min(RequestsLeft - 1, NamedListRequests), cancellationToken);
                    _requests += named.Requests;
                    var known = volumes.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
                    var added = named.Volumes.Where(candidate => !known.Contains(candidate.SourceId)).ToList();
                    if (added.Count > 0 && (startYear > 0 || named.Complete))
                    {
                        var all = volumes.Concat(added).ToList();
                        volume = startYear > 0
                            ? VolumeOrdinal.Suggest(all, title, startYear)?.Match
                            : VolumeOrdinal.Numbered(all, title, volumeNumber, seriesMatch, highestHeld);
                    }
                }
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

            // An incomplete list is not stamped, so the next run lists the volume again; nor is a
            // link that is not stored (a tagged Annual's volume).
            if (complete && link.Id != 0)
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

        private async Task RecordAppliedAsync(string filePath, int issueId, int? rejectedTagId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                var known = _states.TryGetValue(filePath, out var state);
                state ??= new ComicVineFileStateEntity { FilePath = filePath };
                state.IssueId = issueId;
                state.AppliedUtc = DateTime.UtcNow;
                state.RejectedTagId = rejectedTagId;
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
