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

    private readonly IMetronClient _metron;
    private readonly ICoverFingerprintService _fingerprints;

    /// <summary>How often a running job reads a link again, to pick up a volume the user chose meanwhile.</summary>
    internal TimeSpan LinkRecheck { get; init; } = TimeSpan.FromSeconds(30);

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
        IMetronClient metron,
        ICoverFingerprintService fingerprints,
        ILogger<ComicVineIssueDetailsService> logger)
    {
        _metron = metron;
        _fingerprints = fingerprints;
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
        var groups = await VolumeGroupsAsync(filePaths, cancellationToken);
        if (groups.Count == 0)
        {
            return [];
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await StoredVolumesAsync(db, groups.ToDictionary(group => group.Key, group => group.Value.Series), cancellationToken);

        // What the cover year checks need: the linked volumes' stored issues, and what each file
        // was filled from (a cover year that issue wrote is not the file's own).
        var paths = groups.Values.SelectMany(group => group.Files).Select(file => file.FilePath).ToList();
        var states = await db.ComicVineFileStates.AsNoTracking()
            .Where(state => paths.Contains(state.FilePath))
            .ToDictionaryAsync(state => state.FilePath, state => state.IssueId, StringComparer.Ordinal, cancellationToken);
        var volumeIds = stored.Values.Select(volume => volume.Id).OfType<int>().Distinct().ToList();
        var filledIds = states.Values.Distinct().ToList();
        var catalog = await db.ComicVineIssues.AsNoTracking()
            .Where(issue => volumeIds.Contains(issue.VolumeId) || filledIds.Contains(issue.Id))
            .ToListAsync(cancellationToken);
        var issueById = catalog.ToDictionary(issue => issue.Id);
        var issuesOfVolume = catalog.ToLookup(issue => issue.VolumeId);

        var result = new List<SeriesVolumeLink>();
        foreach (var ((key, startYear, volumeNumber), (_, files)) in groups)
        {
            var volume = stored[(key, startYear, volumeNumber)];
            var conflicts = new List<YearConflict>();
            if (volume.Id is { } linked)
            {
                foreach (var file in files)
                {
                    var metadata = file.Metadata!;
                    if (VolumeEvidence.KnownYear(metadata.Year) is not { } year || string.IsNullOrWhiteSpace(metadata.Issue)
                        || issuesOfVolume[linked].FirstOrDefault(issue => SameNumber(issue.IssueNumber, metadata.Issue)) is not { } listed
                        || VolumeEvidence.CoverYear(listed.CoverDate) is not { } listedYear
                        || Math.Abs(listedYear - year) <= 1
                        || (states.TryGetValue(file.FilePath, out var filledFrom) && issueById.TryGetValue(filledFrom, out var filled)
                            && VolumeEvidence.CoverYear(filled.CoverDate) == year))
                    {
                        continue;
                    }

                    conflicts.Add(new YearConflict(file.FilePath, metadata.Issue, year, listedYear));
                }
            }

            result.Add(new SeriesVolumeLink(
                volumeNumber,
                startYear,
                files.Count,
                volume.Id,
                volume.Id is null ? null : volume.Name,
                volume.Id is null ? null : volume.Publisher,
                volume.Id is { } id ? VolumeSiteUrl(id) : null)
            {
                Decision = volume.Link?.Decision,
                SeriesKey = key,
                Searched = volume.Link is not null,
                ReviewReason = volume.Link?.ReviewReason,
                Candidates = ReadCandidates(volume.Link?.CandidatesJson),
                Conflicts = conflicts.OrderBy(conflict => conflict.FilePath, StringComparer.Ordinal).ToList(),
                MixedYears = startYear == 0 && volumeNumber == 0 ? MixedYears(files) : [],
            });
        }

        return result
            .OrderBy(volume => volume.VolumeNumber == 0 && volume.StartYear == 0 ? 1 : 0)
            .ThenBy(volume => volume.StartYear == 0 ? int.MaxValue : volume.StartYear)
            .ThenBy(volume => volume.VolumeNumber)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ChooseVolumeOutcome> ChooseVolumeAsync(
        IReadOnlyCollection<string> filePaths,
        string? seriesKey,
        int startYear,
        int volumeNumber,
        int? volumeId,
        CancellationToken cancellationToken = default)
    {
        var groups = (await VolumeGroupsAsync(filePaths, cancellationToken))
            .Where(entry => entry.Key.Year == startYear && entry.Key.Number == volumeNumber
                            && (seriesKey is null || entry.Key.Key == seriesKey))
            .ToList();
        if (groups.Count != 1 || (volumeId is { } invalid && invalid <= 0))
        {
            return ChooseVolumeOutcome.NoSuchSeriesVolume;
        }

        var key = groups[0].Key.Key;
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.ComicVineVolumeLinks.FirstOrDefaultAsync(
            entry => entry.SeriesKey == key && entry.StartYear == startYear && entry.VolumeNumber == volumeNumber,
            cancellationToken);

        // A volume that was weighed keeps its name and publisher; another is checked on ComicVine
        // and named from it (when ComicVine cannot be asked, it is kept unnamed).
        string? name = null;
        string? publisher = null;
        if (volumeId is { } chosen)
        {
            var candidate = ReadCandidates(link?.CandidatesJson).FirstOrDefault(entry => entry.VolumeId == chosen);
            (name, publisher) = (candidate?.Name, candidate?.Publisher);
            if (candidate is null)
            {
                try
                {
                    var volume = await _volumeSearch.GetVolumeAsync(chosen, cancellationToken);
                    if (volume is null && _client.IsConfigured)
                    {
                        return ChooseVolumeOutcome.NoSuchComicVineVolume;
                    }

                    (name, publisher) = (volume?.CanonicalTitle, volume?.Publisher);
                }
                catch (ExternalMetadataUnavailableException ex)
                {
                    _logger.LogInformation("ComicVine could not name volume {VolumeId}: {Reason}", chosen, LoggingHelper.SanitizeForLog(ex.Message));
                }
            }
        }

        if (link is null)
        {
            link = new ComicVineVolumeLinkEntity { SeriesKey = key, StartYear = startYear, VolumeNumber = volumeNumber };
            db.ComicVineVolumeLinks.Add(link);
        }

        if (link.VolumeId != volumeId)
        {
            link.IssuesListedUtc = null;
        }

        link.VolumeId = volumeId;
        link.VolumeName = Truncate(name, 512);
        link.Publisher = Truncate(publisher, 256);
        link.Decision = ComicVineVolumeDecision.Manual;
        link.ReviewReason = null;
        link.LinkedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ChooseVolumeOutcome.Chosen;
    }

    /// <summary>
    /// The regular issues of each series volume among <paramref name="filePaths"/> (Annuals and
    /// Specials are volumes of their own on ComicVine, and duplicates are left out), with a series
    /// name to find its link by.
    /// </summary>
    private async Task<Dictionary<(string Key, int Year, int Number), (string Series, List<ComicFile> Files)>> VolumeGroupsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(string Key, int Year, int Number), (string Series, List<ComicFile> Files)>();
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
            if (key.Item1.Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = (metadata.Series, []);
                groups[key] = group;
            }

            group.Files.Add(file);
        }

        return groups;
    }

    /// <summary>
    /// Cover years across which the issue numbers of <paramref name="files"/> repeat by three years
    /// or more (#1 of 2011 and #1 of 2016): issues of several volumes, filed as one.
    /// </summary>
    private static IReadOnlyList<int> MixedYears(IEnumerable<ComicFile> files)
    {
        var years = new SortedSet<int>();
        foreach (var number in files
                     .Where(file => !string.IsNullOrWhiteSpace(file.Metadata?.Issue) && VolumeEvidence.KnownYear(file.Metadata.Year) is not null)
                     .GroupBy(file => IssueNumberParser.StripLeadingZeros(file.Metadata!.Issue!.Trim()), StringComparer.OrdinalIgnoreCase))
        {
            var held = number.Select(file => file.Metadata!.Year!.Value).Distinct().ToList();
            if (held.Count > 1 && held.Max() - held.Min() >= 3)
            {
                years.UnionWith(held);
            }
        }

        return years.Take(10).ToList();
    }

    private static IReadOnlyList<VolumeChoice> ReadCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<VolumeChoice>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The weighed volumes as stored with a link: the fitting ones first, best first, six at most,
    /// fewer when they do not fit the column.
    /// </summary>
    private static string? WriteCandidates(IReadOnlyList<VolumeCandidate> ranked)
    {
        var shown = ranked.Where(candidate => candidate.Identity >= VolumeEvidence.MinimumScore)
            .Concat(ranked.Where(candidate => candidate.Identity < VolumeEvidence.MinimumScore))
            .ToList();
        for (var count = Math.Min(shown.Count, 6); count > 0; count--)
        {
            var json = JsonSerializer.Serialize(
                shown.Take(count).Select(candidate => new VolumeChoice(
                    int.TryParse(candidate.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null,
                    candidate.Volume.CanonicalTitle,
                    candidate.Volume.StartYear,
                    candidate.Volume.Publisher,
                    candidate.Volume.TotalChapters,
                    candidate.Score,
                    candidate.Reasons,
                    int.TryParse(candidate.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var site) ? VolumeSiteUrl(site) : null)).ToList(),
                JsonOptions);
            if (json.Length <= 8000)
            {
                return json;
            }
        }

        return null;
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
    private async Task<Dictionary<(string Key, int Year, int Number), (int? Id, string? Name, string? Publisher, ComicVineVolumeLinkEntity? Link)>> StoredVolumesAsync(
        ComicMaintainerDbContext db,
        IReadOnlyDictionary<(string Key, int Year, int Number), string> seriesOfVolume,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Key, int Year, int Number), (int? Id, string? Name, string? Publisher, ComicVineVolumeLinkEntity? Link)>();
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
            if (startYear == 0 && volumeNumber == 0 && link?.Decision != ComicVineVolumeDecision.Manual
                && await _seriesCache.GetByTitleAsync(series, cancellationToken) is { } record
                && string.Equals(record.Source, "ComicVine", StringComparison.OrdinalIgnoreCase)
                && record.LookupStatus is "success" or "manual_match"
                && int.TryParse(record.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesVolume))
            {
                // Files that name neither belong to the series' own match, read afresh.
                volumeId = seriesVolume;
                name ??= record.CanonicalTitle;
            }

            result[(key, startYear, volumeNumber)] = (volumeId, name, link?.Publisher, link);
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

        // When each link was last read again from the database (see LinkAsync).
        private readonly Dictionary<(string Key, int Year, int Number), DateTime> _linksCheckedUtc = new();

        // Issue listings read while weighing volumes, by volume, and whether each is complete.
        private readonly Dictionary<int, (ComicVineResult<IReadOnlyList<ComicVineIssueSummary>> Listing, bool Complete)> _weighedListings = new();
        private readonly Dictionary<int, List<ComicVineIssueEntity>> _issuesByVolume = new();
        private readonly Dictionary<string, string?> _synopsisBySeries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ComicVineFileStateEntity> _states = new(StringComparer.Ordinal);
        private readonly int[] _counts = new int[Enum.GetValues<Outcome>().Length];
        private readonly HashSet<int> _listedThisRun = new();
        private readonly HashSet<(string Key, int Year, int Number)> _searchedThisRun = new();
        private Dictionary<(string Key, int Year, int Number), VolumeHoldings>? _holdings;
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
                // this application wrote after a name match is only its own earlier guess: the issue is
                // matched again, so a volume number or start year the user set since takes effect, and
                // the tag follows the new match.
                if (metadata.ComicVineIssueId is { } taggedId)
                {
                    if (IsOwnMatch(metadata, state))
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
                                await RememberRejectedTagAsync(state, taggedId, cancellationToken);
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
                            : NoVolumeReason(link, metadata.Series, startYear, volumeNumber));
                    }

                    var issues = await IssuesAsync(link, cancellationToken);
                    issue = issues.FirstOrDefault(candidate => SameNumber(candidate.IssueNumber, metadata.Issue));
                    if (issue is null)
                    {
                        return (Outcome.NoIssue, $"ComicVine's {link.VolumeName ?? SeriesTitle(metadata.Series)} (volume {link.VolumeId}) has no issue #{metadata.Issue}");
                    }

                    // An issue dated years away from ComicVine's issue of that number is another
                    // volume's: it is not filled from this one. A year an earlier fill wrote is not the file's own.
                    if (VolumeEvidence.KnownYear(metadata.Year) is { } heldYear
                        && VolumeEvidence.CoverYear(issue.CoverDate) is { } listedYear
                        && Math.Abs(listedYear - heldYear) > 1
                        && state?.IssueId != issue.Id
                        && !(state is not null && VolumeEvidence.CoverYear((await StoredIssueAsync(state.IssueId, cancellationToken))?.CoverDate) == heldYear))
                    {
                        return (Outcome.NoIssue,
                            $"Your #{metadata.Issue} is dated {heldYear}, but #{issue.IssueNumber} of ComicVine's {link.VolumeName ?? SeriesTitle(metadata.Series)} came out in {listedYear}: check the issue's volume");
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
            var plan = ComicVineIssueFill.For(
                metadata, issue, synopsis, previous, replacesTag && metadata.ComicVineIssueId != issue.Id, matchedByName: !fromTag,
                publisher: await VolumePublisherAsync(issue.VolumeId, cancellationToken));
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
                && !IsOwnMatch(metadata, _states.GetValueOrDefault(file.FilePath))
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
                        : NoVolumeReason(link, metadata.Series, startYear, volumeNumber));
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

        /// <summary>
        /// True when the file's ComicVine id is this application's own earlier name match, not a
        /// tagger's: marked as one, or only a web link naming the very issue the file was filled from
        /// (as versions before the marker wrote it).
        /// </summary>
        private static bool IsOwnMatch(ComicMetadata metadata, ComicVineFileStateEntity? state)
            => metadata.ComicVineIssueIdSource == ComicVineIdSource.NameMatch
               || (metadata.ComicVineIssueIdSource == ComicVineIdSource.WebOnly && state is not null && state.IssueId == metadata.ComicVineIssueId);

        /// <summary>
        /// Records, on a file filled before, that ComicVine does not give the issue its tags name, so
        /// it is not asked for again when the file is not filled this time.
        /// </summary>
        private async Task RememberRejectedTagAsync(ComicVineFileStateEntity? state, int taggedId, CancellationToken cancellationToken)
        {
            if (state is null || state.RejectedTagId == taggedId)
            {
                return;
            }

            state.RejectedTagId = taggedId;
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.ComicVineFileStates.Update(state);
            await db.SaveChangesAsync(cancellationToken);
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

            await SaveLinkAsync(
                new ComicVineVolumeLinkEntity
                {
                    SeriesKey = key.Item1,
                    StartYear = startYear,
                    VolumeNumber = volumeNumber,
                    VolumeId = issue.VolumeId,
                    VolumeName = _volumeNames.GetValueOrDefault(issue.VolumeId),
                    LinkedUtc = DateTime.UtcNow,
                    Decision = ComicVineVolumeDecision.Tagged,
                },
                cancellationToken);
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

        private static string NoVolumeReason(ComicVineVolumeLinkEntity? link, string series, int startYear, int volumeNumber)
        {
            if (link?.Decision == ComicVineVolumeDecision.Review)
            {
                return $"{link.ReviewReason ?? "Several ComicVine volumes could be this one"}: choose it under Volumes on the series page";
            }

            if (link?.Decision == ComicVineVolumeDecision.Manual)
            {
                return "You chose no ComicVine volume for it";
            }

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

            // A job runs for a while: a volume the user chose meanwhile is picked up, the stored link
            // being read again at most every 30 seconds.
            if (link is not null && link.Decision != ComicVineVolumeDecision.Manual
                && (!_linksCheckedUtc.TryGetValue(key, out var checkedUtc) || DateTime.UtcNow - checkedUtc >= owner.LinkRecheck))
            {
                _linksCheckedUtc[key] = DateTime.UtcNow;
                await using var read = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                if (await read.ComicVineVolumeLinks.AsNoTracking().FirstOrDefaultAsync(
                        entry => entry.SeriesKey == key.Item1 && entry.StartYear == startYear && entry.VolumeNumber == volumeNumber,
                        cancellationToken) is { Decision: ComicVineVolumeDecision.Manual } chosen)
                {
                    link = chosen;
                    _links[key] = chosen;
                }
            }

            // The user's choice is final: never searched for again.
            if (link?.Decision == ComicVineVolumeDecision.Manual)
            {
                return (link, false);
            }

            ExternalSeriesMetadata? volume;
            string? decision = null;
            string? metronReason = null;
            IReadOnlyList<VolumeCandidate> ranked = [];
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
                // A volume left for review waits for the user, or the retry period, even on a run the
                // user started: weighing it again would cost the same and decide the same.
                if (link is not null
                    && !tooShort
                    && (link.VolumeId is not null
                        || _searchedThisRun.Contains(key)
                        || ((!retryNotFound || link.Decision == ComicVineVolumeDecision.Review)
                            && DateTime.UtcNow - link.LinkedUtc < NotFoundRetry)))
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
                var seriesMatch = (await SeriesMatchAsync(series, cancellationToken))?.SourceId;
                var holdings = await HoldingsAsync(key, cancellationToken);
                ranked = VolumeEvidence.Rank(volumes, title, startYear, volumeNumber, seriesMatch, holdings);
                var (choice, ambiguous) = VolumeEvidence.Decide(ranked, holdings.Publishers.Count == 0);

                // The ranked search can leave a much-used name's volume out: list every volume of
                // that exact name, oldest first, and look again.
                // A request is left for listing the volume's issues. A volume found by its number is
                // counted among the name's volumes, so only a complete list may add to them: the oldest
                // 500 of thousands would count the wrong ones.
                if (choice is null && !ambiguous && RequestsLeft > 1)
                {
                    var named = await owner._volumeSearch.ListVolumesNamedAsync(title, Math.Min(RequestsLeft - 1, NamedListRequests), cancellationToken);
                    _requests += named.Requests;
                    var known = volumes.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
                    var added = named.Volumes.Where(candidate => !known.Contains(candidate.SourceId)).ToList();
                    if (added.Count > 0 && (startYear > 0 || named.Complete))
                    {
                        ranked = VolumeEvidence.Rank(volumes.Concat(added), title, startYear, volumeNumber, seriesMatch, holdings);
                        (choice, ambiguous) = VolumeEvidence.Decide(ranked, holdings.Publishers.Count == 0);
                    }
                }

                // Several fit about equally well: their issues tell them apart, if listing them is
                // affordable; then Metron's record of each, then their covers.
                if (ambiguous)
                {
                    ranked = await WeighIssuesAsync(ranked, holdings, cancellationToken);
                    (choice, ambiguous) = VolumeEvidence.Decide(ranked, holdings.Publishers.Count == 0);
                }

                if (ambiguous)
                {
                    ranked = await WeighWithMetronAsync(ranked, startYear, volumeNumber, cancellationToken);
                    (choice, ambiguous) = VolumeEvidence.Decide(ranked, holdings.Publishers.Count == 0);
                }

                if (ambiguous)
                {
                    ranked = await WeighCoversAsync(ranked, holdings, cancellationToken);
                    (choice, ambiguous) = VolumeEvidence.Decide(ranked, holdings.Publishers.Count == 0);
                }

                // A volume taken is checked against Metron's record of it: one Metron counts as another
                // volume is left for the user instead.
                if (choice is not null && await MetronContradictionAsync(choice, startYear, volumeNumber, cancellationToken) is { } contradiction)
                {
                    var started = choice.Volume.StartYear is { } year ? $" ({year.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
                    metronReason = $"ComicVine's {choice.Volume.CanonicalTitle}{started} fits best, but {contradiction}";
                    choice = null;
                    ambiguous = true;
                }

                volume = choice?.Volume;
                decision = choice is not null ? ComicVineVolumeDecision.Weighed : ambiguous ? ComicVineVolumeDecision.Review : null;
            }

            var updated = new ComicVineVolumeLinkEntity
            {
                SeriesKey = key.Item1,
                StartYear = startYear,
                VolumeNumber = volumeNumber,
                VolumeId = int.TryParse(volume?.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null,
                VolumeName = Truncate(volume?.CanonicalTitle, 512),
                Publisher = Truncate(volume?.Publisher, 256),
                LinkedUtc = DateTime.UtcNow,
                Decision = decision,
                CandidatesJson = ranked.Count > 0 ? WriteCandidates(ranked) : null,
                ReviewReason = decision == ComicVineVolumeDecision.Review ? metronReason ?? VolumeEvidence.ReviewReason(ranked, title) : null,
            };
            return (await SaveLinkAsync(updated, cancellationToken), false);
        }

        /// <summary>
        /// Stores <paramref name="link"/> as its series volume's link, over the stored one, and returns
        /// the link now stored. A choice the user made meanwhile is kept instead: this run's copy of
        /// the links is from its start, and a job runs for a while. A link another run stored first
        /// is taken as it is.
        /// </summary>
        private async Task<ComicVineVolumeLinkEntity> SaveLinkAsync(ComicVineVolumeLinkEntity link, CancellationToken cancellationToken)
        {
            var key = (link.SeriesKey, link.StartYear, link.VolumeNumber);
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            var stored = await db.ComicVineVolumeLinks.FirstOrDefaultAsync(
                entry => entry.SeriesKey == link.SeriesKey && entry.StartYear == link.StartYear && entry.VolumeNumber == link.VolumeNumber,
                cancellationToken);
            if (stored?.Decision == ComicVineVolumeDecision.Manual)
            {
                _links[key] = stored;
                return stored;
            }

            if (stored is null)
            {
                stored = new ComicVineVolumeLinkEntity { SeriesKey = link.SeriesKey, StartYear = link.StartYear, VolumeNumber = link.VolumeNumber };
                db.ComicVineVolumeLinks.Add(stored);
            }

            stored.VolumeId = link.VolumeId;
            stored.VolumeName = link.VolumeName;
            stored.Publisher = link.Publisher;
            stored.LinkedUtc = link.LinkedUtc;
            stored.IssuesListedUtc = null;
            stored.Decision = link.Decision;
            stored.CandidatesJson = link.CandidatesJson;
            stored.ReviewReason = link.ReviewReason;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another run stored the link first; any other failure is rethrown as it was.
                await using var again = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                stored = await again.ComicVineVolumeLinks.AsNoTracking().FirstOrDefaultAsync(
                    entry => entry.SeriesKey == link.SeriesKey && entry.StartYear == link.StartYear && entry.VolumeNumber == link.VolumeNumber,
                    cancellationToken) ?? throw new InvalidOperationException("The ComicVine volume link could not be stored");
            }

            _links[key] = stored;
            return stored;
        }

        /// <summary>
        /// The highest whole issue number the library has of one series volume (regular issues,
        /// not duplicates); 0 when it has none.
        /// </summary>
        private async Task<int> HighestIssueHeldAsync((string Key, int Year, int Number) key, CancellationToken cancellationToken)
            => (await HoldingsAsync(key, cancellationToken)).HighestIssue;

        /// <summary>
        /// What the library holds of one series volume (regular issues, not duplicates), the
        /// evidence its ComicVine volume is weighed against (see <see cref="VolumeEvidence"/>).
        /// </summary>
        private async Task<VolumeHoldings> HoldingsAsync((string Key, int Year, int Number) key, CancellationToken cancellationToken)
        {
            if (_holdings is null)
            {
                var files = LibraryFiles ?? (await owner._fileStore.GetAllFilesAsync(cancellationToken)).ToList();
                var collected = new Dictionary<(string Key, int Year, int Number), (HashSet<string> Publishers, List<HeldIssue> Issues, int Highest, int? Earliest)>();
                foreach (var file in files)
                {
                    var metadata = file.Metadata;
                    if (file.IsDuplicate || metadata is null || string.IsNullOrWhiteSpace(metadata.Series) || string.IsNullOrWhiteSpace(metadata.Issue)
                        || IssueFormat.Of(metadata.Format, metadata.Series, metadata.Issue, file.FileName, metadata.Title) is not null)
                    {
                        continue;
                    }

                    var (startYear, volumeNumber) = VolumeOf(metadata);
                    var group = (SeriesKey(metadata.Series), startYear, volumeNumber);
                    if (!collected.TryGetValue(group, out var held))
                    {
                        held = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], 0, null);
                    }

                    if (!string.IsNullOrWhiteSpace(metadata.Publisher))
                    {
                        held.Publishers.Add(metadata.Publisher.Trim());
                    }

                    var year = VolumeEvidence.KnownYear(metadata.Year);
                    var title = ComicVineIssueFill.IsPlaceholderTitle(metadata.Title, metadata.Series, metadata.Issue) ? null : metadata.Title;
                    held.Issues.Add(new HeldIssue(metadata.Issue, year, title, file.FilePath));
                    if (decimal.TryParse(metadata.Issue, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                        && number >= 1 && number <= 100000)
                    {
                        held.Highest = Math.Max(held.Highest, (int)number);
                    }

                    if (year is { } known && (held.Earliest is null || known < held.Earliest))
                    {
                        held.Earliest = known;
                    }

                    collected[group] = held;
                }

                _holdings = collected.ToDictionary(
                    entry => entry.Key,
                    entry => new VolumeHoldings(entry.Value.Publishers, entry.Value.Earliest, entry.Value.Highest, entry.Value.Issues));
            }

            return _holdings.GetValueOrDefault(key) ?? VolumeHoldings.None;
        }

        /// <summary>
        /// The best three of <paramref name="ranked"/> weighed again with Metron's record of each (see
        /// <see cref="VolumeEvidence.WithMetron"/>), when Metron is set up and answers.
        /// </summary>
        private async Task<IReadOnlyList<VolumeCandidate>> WeighWithMetronAsync(
            IReadOnlyList<VolumeCandidate> ranked,
            int startYear,
            int volumeNumber,
            CancellationToken cancellationToken)
        {
            if (!owner._metron.IsConfigured)
            {
                return ranked;
            }

            var weighed = new List<VolumeCandidate>();
            foreach (var candidate in ranked.Take(3))
            {
                var answer = int.TryParse(candidate.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var volumeId)
                    ? await owner._metron.SeriesOfComicVineVolumeAsync(volumeId, waitForPermit: !ListForCovers, cancellationToken)
                    : MetronAnswer.Unavailable;
                weighed.Add(answer.Series is { } series
                    ? VolumeEvidence.WithMetron(candidate, series.Volume, series.YearBegan, startYear, volumeNumber)
                    : candidate);
            }

            return VolumeEvidence.Order(weighed.Concat(ranked.Skip(3)));
        }

        /// <summary>What Metron's record of <paramref name="choice"/> says against it; null when nothing, or Metron does not answer.</summary>
        private async Task<string?> MetronContradictionAsync(VolumeCandidate choice, int startYear, int volumeNumber, CancellationToken cancellationToken)
        {
            if (!owner._metron.IsConfigured
                || !int.TryParse(choice.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var volumeId)
                || (await owner._metron.SeriesOfComicVineVolumeAsync(volumeId, waitForPermit: !ListForCovers, cancellationToken)).Series is not { } series)
            {
                return null;
            }

            return VolumeEvidence.MetronContradiction(series.Volume, series.YearBegan, startYear, volumeNumber);
        }

        /// <summary>
        /// The best three of <paramref name="ranked"/> weighed again with their covers (see
        /// <see cref="VolumeEvidence.WithCovers"/>): the covers of up to two issues held, against the
        /// covers each volume lists under the same numbers, as far as its listing is known.
        /// </summary>
        private async Task<IReadOnlyList<VolumeCandidate>> WeighCoversAsync(
            IReadOnlyList<VolumeCandidate> ranked,
            VolumeHoldings holdings,
            CancellationToken cancellationToken)
        {
            var held = holdings.Issues.Where(issue => issue.FilePath is not null).Take(2).ToList();
            if (held.Count == 0)
            {
                return ranked;
            }

            var fingerprints = new List<(HeldIssue Issue, ulong? Cover)>();
            foreach (var issue in held)
            {
                fingerprints.Add((issue, await owner._fingerprints.OfFileAsync(issue.FilePath!, cancellationToken)));
            }

            var heldCovers = fingerprints
                .Where(entry => entry.Cover.HasValue)
                .Select(entry => (entry.Issue, Cover: entry.Cover!.Value))
                .ToList();

            if (heldCovers.Count == 0)
            {
                return ranked;
            }

            var compared = new List<(VolumeCandidate Candidate, int Same, int Other, int Compared)>();
            foreach (var candidate in ranked.Take(3))
            {
                var listed = int.TryParse(candidate.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var volumeId)
                    ? await CoverUrlsAsync(volumeId, cancellationToken)
                    : [];
                var (same, other, count) = (0, 0, 0);
                foreach (var (issue, cover) in heldCovers)
                {
                    if (listed.FirstOrDefault(entry => IssueNumberParser.AreEquivalent(entry.Number, issue.Number)) is not { Url: { } url }
                        || await owner._fingerprints.OfImageAsync(url, cancellationToken) is not { } theirs)
                    {
                        continue;
                    }

                    var distance = CoverFingerprint.Distance(cover, theirs);
                    count++;
                    same += distance <= CoverFingerprint.SameCover ? 1 : 0;
                    other += distance >= CoverFingerprint.OtherCover ? 1 : 0;
                }

                compared.Add((candidate, same, other, count));
            }

            // Covers that differ count against a volume only when every volume's covers were compared:
            // otherwise the one that could not be (a variant cover, an unlisted issue) would win by default.
            var allCompared = compared.All(entry => entry.Compared > 0);
            var weighed = compared.Select(entry => VolumeEvidence.WithCovers(entry.Candidate, entry.Same, allCompared ? entry.Other : 0));
            return VolumeEvidence.Order(weighed.Concat(ranked.Skip(3)));
        }

        /// <summary>
        /// The cover of each issue of a volume, as far as its listing is known: read while weighing,
        /// else stored for a volume listed in full. A thumbnail where there is one.
        /// </summary>
        private async Task<IReadOnlyList<(string Number, string? Url)>> CoverUrlsAsync(int volumeId, CancellationToken cancellationToken)
        {
            if (_weighedListings.TryGetValue(volumeId, out var weighed))
            {
                return weighed.Listing.Value.Select(issue => (issue.IssueNumber, issue.CoverThumbUrl ?? issue.CoverUrl)).ToList();
            }

            if (!_links.Values.Any(link => link.VolumeId == volumeId && link.IssuesListedUtc is not null))
            {
                return [];
            }

            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            return (await db.ComicVineIssues.AsNoTracking()
                    .Where(issue => issue.VolumeId == volumeId)
                    .Select(issue => new { issue.IssueNumber, issue.CoverThumbUrl, issue.CoverUrl })
                    .ToListAsync(cancellationToken))
                .Select(issue => (issue.IssueNumber, issue.CoverThumbUrl ?? issue.CoverUrl))
                .ToList();
        }

        /// <summary>
        /// The best three of <paramref name="ranked"/> weighed again with their issues (see
        /// <see cref="VolumeEvidence.WithIssues"/>): those stored, else listed from ComicVine while
        /// requests are left (two pages at most each, not stored).
        /// </summary>
        private async Task<IReadOnlyList<VolumeCandidate>> WeighIssuesAsync(
            IReadOnlyList<VolumeCandidate> ranked,
            VolumeHoldings holdings,
            CancellationToken cancellationToken)
        {
            var weighed = new List<VolumeCandidate>();
            foreach (var candidate in ranked.Take(3))
            {
                if (!int.TryParse(candidate.Volume.SourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var volumeId))
                {
                    weighed.Add(candidate);
                    continue;
                }

                // Issues stored for a volume listed in full; a volume reached through a tag holds one.
                List<ListedIssue> listed = [];
                if (_links.Values.Any(link => link.VolumeId == volumeId && link.IssuesListedUtc is not null))
                {
                    await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
                    listed = await db.ComicVineIssues.AsNoTracking()
                        .Where(issue => issue.VolumeId == volumeId)
                        .Select(issue => new ListedIssue(issue.IssueNumber, issue.CoverDate, issue.Name))
                        .ToListAsync(cancellationToken);
                }

                // A request is kept for listing the volume chosen; a listing read in full is kept for it.
                if (listed.Count == 0 && RequestsLeft > 1)
                {
                    var budget = Math.Min(RequestsLeft - 1, 2);
                    var listing = await owner._client.ListVolumeIssuesAsync(volumeId, budget, cancellationToken);
                    _requests += listing.Requests;
                    _weighedListings[volumeId] = (listing, owner._client.IsConfigured && listing.Value.Count < listing.Requests * 100);
                    listed = listing.Value.Select(issue => new ListedIssue(issue.IssueNumber, issue.CoverDate, issue.Name)).ToList();
                }

                weighed.Add(listed.Count == 0 ? candidate : VolumeEvidence.WithIssues(candidate, listed, holdings));
            }

            return VolumeEvidence.Order(weighed.Concat(ranked.Skip(3)));
        }

        /// <summary>
        /// The publisher ComicVine lists for a volume, as stored with a series' link to it (an issue
        /// its tags name is in a volume some series links to once it is matched); null when none is.
        /// </summary>
        private async Task<string?> VolumePublisherAsync(int volumeId, CancellationToken cancellationToken)
        {
            await using var db = await owner._dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.ComicVineVolumeLinks.AsNoTracking()
                .Where(link => link.VolumeId == volumeId && link.Publisher != null && link.Publisher != "")
                .OrderByDescending(link => link.LinkedUtc)
                .Select(link => link.Publisher)
                .FirstOrDefaultAsync(cancellationToken);
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

            ComicVineResult<IReadOnlyList<ComicVineIssueSummary>> listing;
            bool complete;
            if (_weighedListings.Remove(volumeId, out var weighed) && weighed.Complete)
            {
                // Listed in full when the volume was weighed: no need to ask again.
                listing = weighed.Listing;
                complete = true;
                _listedThisRun.Add(volumeId);
            }
            else
            {
                if (RequestsLeft <= 0)
                {
                    _stoppedEarly = true;
                    return issues;
                }

                var budget = RequestsLeft;
                listing = await owner._client.ListVolumeIssuesAsync(volumeId, budget, cancellationToken);
                _requests += listing.Requests;
                _listedThisRun.Add(volumeId);

                // The list is complete only when ComicVine was asked and the last page was not cut
                // off by the budget (a full last page with the budget used up means more pages exist).
                complete = owner._client.IsConfigured
                           && !(listing.Requests >= budget && listing.Value.Count >= listing.Requests * 100);
                if (!complete)
                {
                    _stoppedEarly = true;
                }
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

            await db.SaveChangesAsync(cancellationToken);

            // An incomplete list is not stamped, so the next run lists the volume again; nor is a
            // link that is not stored (a tagged Annual's volume). Only the listing date is written,
            // and only while the link is still to this volume: the user may have chosen another.
            if (complete && link.Id != 0)
            {
                link.IssuesListedUtc = now;
                await db.ComicVineVolumeLinks
                    .Where(entry => entry.Id == link.Id && entry.VolumeId == volumeId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.IssuesListedUtc, now), cancellationToken);
            }

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
