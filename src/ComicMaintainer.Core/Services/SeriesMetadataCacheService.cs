using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Persisted external-metadata cache + user-managed alias store.
/// </summary>
public class SeriesMetadataCacheService : ISeriesMetadataCacheService, IChangeVersionSource
{
    private long _changeVersion;

    /// <inheritdoc />
    /// <remarks>Bumped after every persisted change to the series metadata cache.</remarks>
    public long ChangeVersion => Interlocked.Read(ref _changeVersion);

    private void MarkChanged() => Interlocked.Increment(ref _changeVersion);

    /// <summary>
    /// Longest time a title index is reused even though the cache reports no
    /// change, as a safety net for changes made to the table outside this
    /// service.
    /// </summary>
    internal static readonly TimeSpan TitleIndexMaxAge = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _titleIndexLock = new(1, 1);
    private TitleIndex? _titleIndex;
    private int _titleIndexBuilds;

    /// <summary>How many times the title index has been built (diagnostics and tests).</summary>
    internal int TitleIndexBuildCount => Volatile.Read(ref _titleIndexBuilds);

    /// <summary>Series key of a title to the key of the record that owns it, as of <paramref name="Version"/>.</summary>
    private sealed record TitleIndex(long Version, DateTime BuiltUtc, IReadOnlyDictionary<string, string> OwnerKeyByTitleKey);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IExternalSeriesMetadataService _externalMetadata;
    private readonly ISeriesImageStore _imageStore;
    private readonly ISeriesFolderCoverWriter _folderCoverWriter;
    private readonly ISeriesArchiveCoverWriteQueue _archiveCoverQueue;
    private readonly ISeriesArchiveCoverWriter _archiveCoverWriter;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesMetadataCacheService> _logger;

    public SeriesMetadataCacheService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IExternalSeriesMetadataService externalMetadata,
        ISeriesImageStore imageStore,
        ISeriesFolderCoverWriter folderCoverWriter,
        ISeriesArchiveCoverWriteQueue archiveCoverQueue,
        ISeriesArchiveCoverWriter archiveCoverWriter,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesMetadataCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _externalMetadata = externalMetadata;
        _imageStore = imageStore;
        _folderCoverWriter = folderCoverWriter;
        _archiveCoverQueue = archiveCoverQueue;
        _archiveCoverWriter = archiveCoverWriter;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Cache records are keyed by <see cref="SeriesKey.Normalize"/>.</remarks>
    public string NormalizeKey(string? value) => SeriesKey.Normalize(value);

    public async Task<IReadOnlyList<SeriesMetadataCacheRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.SeriesMetadataCache.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<SeriesMetadataCacheRecord?> GetAsync(string normalizedKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.AsNoTracking()
            .FirstOrDefaultAsync(e => e.NormalizedKey == normalizedKey, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<SeriesMetadataCacheRecord?> GetByTitleAsync(string seriesTitle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken, tracked: false);
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>
    /// Resolves the cache entity for a free-form series title. Tries an exact
    /// normalized-key lookup first, then falls back to every record's known
    /// titles (canonical title, user-selected name, provider/user aliases, and
    /// localized titles) through the in-memory title index. The fallback is
    /// what keeps a series editable after a manual match: applying a match
    /// leaves the record keyed by the original folder title while the UI now
    /// refers to the series by its matched canonical title, so an exact-key
    /// lookup on that new title would otherwise miss the record.
    /// </summary>
    /// <param name="tracked">
    /// False for read-only callers, so the entity is not change-tracked.
    /// </param>
    private async Task<SeriesMetadataCacheEntity?> ResolveEntityByTitleAsync(
        ComicMaintainerDbContext db,
        string seriesTitle,
        CancellationToken cancellationToken,
        bool tracked = true)
    {
        var entities = tracked ? db.SeriesMetadataCache : db.SeriesMetadataCache.AsNoTracking();
        var key = NormalizeKey(seriesTitle);
        var entity = await entities.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        // Exact-key miss: the record may be keyed by an older title (typically
        // the original folder name) while the caller refers to the series by
        // its matched canonical title or one of its aliases. Exact key is
        // always preferred above.
        var titleIndex = await GetTitleIndexAsync(cancellationToken);
        return titleIndex.TryGetValue(key, out var ownerKey)
            ? await entities.FirstOrDefaultAsync(e => e.NormalizedKey == ownerKey, cancellationToken)
            : null;
    }

    /// <summary>
    /// Returns the title index: for the series key of every title a record is
    /// known by, the key of the record that owns it. Built from one untracked
    /// read of the table and reused until the cache changes
    /// (<see cref="ChangeVersion"/>) or <see cref="TitleIndexMaxAge"/> passes,
    /// so resolving a series name for each processed file no longer loads and
    /// JSON-parses the whole table.
    /// </summary>
    /// <remarks>
    /// A single logical series can be described by several cache records
    /// (e.g. a folder refresh issues a separate lookup for the canonical title
    /// AND each alias, leaving one authoritative "success" record and one or
    /// more stale "not_found" siblings). The library renders the series using
    /// the most authoritative record (see
    /// <c>SeriesLibraryService.ResolveRecordForGroup</c>). The index mirrors
    /// that ranking so mutations (e.g. pinning a series name) land on the SAME
    /// record the library displays — otherwise the change is written to a
    /// stale sibling and the displayed name never updates.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> GetTitleIndexAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _titleIndex);
        if (IsCurrent(current))
        {
            return current!.OwnerKeyByTitleKey;
        }

        await _titleIndexLock.WaitAsync(cancellationToken);
        try
        {
            current = _titleIndex;
            if (IsCurrent(current))
            {
                return current!.OwnerKeyByTitleKey;
            }

            // Read the version before the rows: a change committed meanwhile
            // leaves the index one version behind, so the next lookup rebuilds.
            var version = ChangeVersion;
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entities = await db.SeriesMetadataCache.AsNoTracking().ToListAsync(cancellationToken);
            var owners = new Dictionary<string, SeriesMetadataCacheEntity>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                foreach (var titleKey in TitleKeysOf(entity))
                {
                    if (!owners.TryGetValue(titleKey, out var owner) || IsMoreAuthoritative(entity, owner))
                    {
                        owners[titleKey] = entity;
                    }
                }
            }

            var index = new TitleIndex(
                version,
                DateTime.UtcNow,
                owners.ToDictionary(pair => pair.Key, pair => pair.Value.NormalizedKey, StringComparer.Ordinal));
            Volatile.Write(ref _titleIndex, index);
            Interlocked.Increment(ref _titleIndexBuilds);
            return index.OwnerKeyByTitleKey;
        }
        finally
        {
            _titleIndexLock.Release();
        }

        bool IsCurrent(TitleIndex? index)
            => index is not null
               && index.Version == ChangeVersion
               && DateTime.UtcNow - index.BuiltUtc < TitleIndexMaxAge;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> represents a series better than
    /// <paramref name="current"/>: a higher-ranked lookup status, then the more
    /// recent lookup, then the more recent update. On a full tie the record
    /// seen first is kept.
    /// </summary>
    private static bool IsMoreAuthoritative(SeriesMetadataCacheEntity candidate, SeriesMetadataCacheEntity current)
    {
        var byStatus = RankLookupStatus(candidate.LookupStatus).CompareTo(RankLookupStatus(current.LookupStatus));
        if (byStatus != 0) return byStatus > 0;
        var byLookup = (candidate.LastLookupUtc ?? DateTime.MinValue).CompareTo(current.LastLookupUtc ?? DateTime.MinValue);
        if (byLookup != 0) return byLookup > 0;
        return candidate.UpdatedAt > current.UpdatedAt;
    }

    /// <summary>
    /// Ranks a cache record's <c>LookupStatus</c> by how authoritative it is
    /// for representing a series. Higher rank wins when several records share a
    /// title. Kept in sync with <c>SeriesLibraryService.RankLookupStatus</c> so
    /// title-based resolution selects the same record the library displays.
    /// </summary>
    private static int RankLookupStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return 0;
        }

        return status.ToLowerInvariant() switch
        {
            "manual_match" => 5,
            "manual" => 4,
            "success" => 3,
            "not_found" => 2,
            "error" => 1,
            "cleared" => 0,
            _ => 0
        };
    }

    /// <summary>
    /// The series keys of every title <paramref name="entity"/> is known by:
    /// canonical title, user-selected name, provider/user aliases and
    /// localized titles. Blank titles are skipped.
    /// </summary>
    private static HashSet<string> TitleKeysOf(SeriesMetadataCacheEntity entity)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? title)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                keys.Add(SeriesKey.Normalize(title));
            }
        }

        Add(entity.CanonicalTitle);
        Add(entity.SeriesName);
        foreach (var alias in entity.Aliases ?? Enumerable.Empty<string>()) Add(alias);
        foreach (var alias in entity.UserAliases ?? Enumerable.Empty<string>()) Add(alias);
        foreach (var localized in DeserializeLocalizedTitles(entity.LocalizedTitlesJson)) Add(localized?.Title);
        return keys;
    }

    public async Task<SeriesMetadataCacheRecord> SetUserAliasesAsync(
        string seriesTitle,
        IEnumerable<string> userAliases,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        var key = NormalizeKey(seriesTitle);
        var cleanedAliases = (userAliases ?? Enumerable.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = seriesTitle.Trim(),
                Aliases = new List<string>(),
                UserAliases = cleanedAliases,
                LookupStatus = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            entity.UserAliases = cleanedAliases;
            entity.UpdatedAt = now;
            BumpMetadataVersion(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    public async Task<SeriesMetadataCacheRecord?> RemoveUserAliasAsync(
        string normalizedKey,
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == normalizedKey, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var updated = entity.UserAliases
            .Where(a => !string.Equals(a, alias, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (updated.Count == entity.UserAliases.Count)
        {
            return ToRecord(entity);
        }

        entity.UserAliases = updated;
        entity.UpdatedAt = DateTime.UtcNow;
        BumpMetadataVersion(entity);
        await db.SaveChangesAsync(cancellationToken);
        MarkChanged();
        return ToRecord(entity);
    }

    public async Task<SeriesMetadataCacheRecord> RefreshAsync(string seriesTitle, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        var key = NormalizeKey(seriesTitle);
        var trimmedTitle = seriesTitle.Trim();

        // If the user previously chose a manual match for this series, refresh
        // must re-resolve to that same match (not perform a fresh title search
        // that might pick a different result). Only the provider that supplied
        // the match is asked, for the matched entry's id when it is known, and
        // the "manual_match" status is preserved so the series stays marked as
        // manually matched.
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        var wasManualMatch = HasStatus(entity, "manual_match");
        var previousStatus = entity?.LookupStatus;

        ExternalSeriesMetadata? lookup;
        try
        {
            lookup = await LookupForRefreshAsync(entity, key, trimmedTitle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; the provider didn't fail.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The provider could not answer, which says nothing about the
            // series: keep what is known and report the failure to the caller.
            _logger.LogWarning(ex, "External metadata refresh failed for {SeriesTitle}", LoggingHelper.SanitizeForLog(trimmedTitle));
            await RecordRefreshFailureAsync(db, entity, key, trimmedTitle, cancellationToken);
            throw;
        }

        var status = lookup is null ? "not_found" : "success";
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = lookup?.CanonicalTitle ?? trimmedTitle,
                Aliases = lookup?.Aliases?.ToList() ?? new List<string>(),
                UserAliases = new List<string>(),
                Source = lookup?.Source,
                SourceId = lookup?.SourceId,
                LastLookupUtc = now,
                LookupStatus = status,
                LocalizedTitlesJson = lookup is null ? null : SerializeLocalizedTitles(BuildLocalizedTitles(lookup)),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            if (lookup is not null)
            {
                var canonicalTitle = string.IsNullOrWhiteSpace(lookup.CanonicalTitle) ? entity.CanonicalTitle : lookup.CanonicalTitle;
                var aliases = lookup.Aliases?.ToList() ?? new List<string>();
                var localizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(lookup));
                // A manually-matched series stays manually matched after a
                // successful refresh — the lookup just updates the cached
                // fields for the user-selected match.
                var newStatus = wasManualMatch ? "manual_match" : status;
                var namesChanged = !string.Equals(entity.CanonicalTitle, canonicalTitle, StringComparison.Ordinal)
                    || !(entity.Aliases ?? new List<string>()).SequenceEqual(aliases, StringComparer.Ordinal)
                    || !string.Equals(entity.LocalizedTitlesJson, localizedTitlesJson, StringComparison.Ordinal)
                    || !string.Equals(previousStatus, newStatus, StringComparison.OrdinalIgnoreCase);

                entity.CanonicalTitle = canonicalTitle;
                entity.Aliases = aliases;
                // A refresh by title may not report the id; keep the known one
                // when the entry is still from the same provider.
                entity.SourceId = lookup.SourceId
                    ?? (string.Equals(entity.Source, lookup.Source, StringComparison.OrdinalIgnoreCase) ? entity.SourceId : null);
                entity.Source = lookup.Source;
                entity.LastLookupUtc = now;
                entity.LocalizedTitlesJson = localizedTitlesJson;
                entity.LookupStatus = newStatus;
                entity.UpdatedAt = now;
                if (namesChanged)
                {
                    // A new title, alias or match can change what is written
                    // to the series' files, so bump for the library scan to
                    // re-normalize them. An unchanged answer leaves them be.
                    BumpMetadataVersion(entity);
                }
            }
            else if (KeepsStatusOnMiss(entity, key))
            {
                // Nothing was found for this title, but that does not undo
                // what the record holds: a match made by hand, the match of
                // another of the series' names, or a user decision. Keep the
                // existing titles, source and status; only record that a
                // refresh was attempted.
                entity.LastLookupUtc = now;
                entity.UpdatedAt = now;
            }
            else
            {
                entity.LastLookupUtc = now;
                entity.LookupStatus = status;
                entity.UpdatedAt = now;
            }
        }

        // Best-effort series-image download. Failure must NEVER fail the
        // metadata refresh (the cover-image is a nice-to-have). User-uploaded
        // images are sticky and never overwritten by an external download.
        await TryDownloadImageAsync(entity, lookup, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    /// <summary>
    /// Asks the external providers about a series being refreshed.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A manual match is re-fetched from its own provider only.</item>
    /// <item>
    /// An automatic match reached through another of the series' names (a user
    /// alias, a merged folder, a provider alias) is also re-fetched from its
    /// own provider. That name was never what the match was made on: searching
    /// for it could miss, or find a different series that happens to use it.
    /// </item>
    /// <item>
    /// An automatic match refreshed by its own title (the record's key or its
    /// canonical title) is re-fetched by entry id when the id is known, which
    /// also survives a provider renaming the entry. If that entry is gone or
    /// its provider is disabled, the title decides.
    /// </item>
    /// <item>
    /// Everything else, including older matches without an id, is an ordinary
    /// strict lookup across every provider. An explicit refresh must ask, so a
    /// remembered "no match" is forgotten first.
    /// </item>
    /// </list>
    /// </remarks>
    private async Task<ExternalSeriesMetadata?> LookupForRefreshAsync(
        SeriesMetadataCacheEntity? entity,
        string key,
        string title,
        CancellationToken cancellationToken)
    {
        if (HasStatus(entity, "manual_match"))
        {
            return await LookupStoredMatchAsync(entity!, cancellationToken);
        }

        if (HasStatus(entity, "success"))
        {
            var isOwnTitle = IsOwnTitle(entity!, key);
            if (!isOwnTitle || !string.IsNullOrWhiteSpace(entity!.SourceId))
            {
                var stored = await LookupStoredMatchAsync(entity!, cancellationToken);
                if (stored is not null || !isOwnTitle)
                {
                    return stored;
                }
            }
        }

        _externalMetadata.ForgetNoMatch(title);
        return await _externalMetadata.LookupSeriesAsync(title, cancellationToken);
    }

    private static bool HasStatus(SeriesMetadataCacheEntity? entity, string status)
        => string.Equals(entity?.LookupStatus, status, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="key"/> is the record's own identity: the key
    /// it was stored under or its canonical title, as opposed to one of the
    /// other names it is known by.
    /// </summary>
    private static bool IsOwnTitle(SeriesMetadataCacheEntity entity, string key)
        => string.Equals(entity.NormalizedKey, key, StringComparison.Ordinal)
           || string.Equals(SeriesKey.Normalize(entity.CanonicalTitle), key, StringComparison.Ordinal);

    /// <summary>
    /// True when a lookup that found nothing must leave the record's status
    /// alone: it holds a manual match, a user decision (<c>manual</c>,
    /// <c>cleared</c>), or an automatic match that was refreshed through a
    /// name other than its own.
    /// </summary>
    private static bool KeepsStatusOnMiss(SeriesMetadataCacheEntity entity, string key)
        => HasStatus(entity, "manual_match")
           || HasStatus(entity, "manual")
           || HasStatus(entity, "cleared")
           || (HasStatus(entity, "success") && !IsOwnTitle(entity, key));

    /// <summary>
    /// Re-fetches a match from the provider that supplied it (by the stored
    /// entry id when known). A match without a recorded source cannot be
    /// re-fetched; it is left as it is.
    /// </summary>
    private Task<ExternalSeriesMetadata?> LookupStoredMatchAsync(
        SeriesMetadataCacheEntity entity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entity.Source) || string.IsNullOrWhiteSpace(entity.CanonicalTitle))
        {
            return Task.FromResult<ExternalSeriesMetadata?>(null);
        }

        return _externalMetadata.LookupMatchAsync(entity.Source, entity.SourceId, entity.CanonicalTitle, cancellationToken);
    }

    /// <summary>
    /// Records a refresh whose provider lookup failed. A record that holds a
    /// match or a user decision (success, manual, manual_match, cleared) is
    /// left exactly as it was: the failure says nothing about the series, and
    /// downgrading it would hide the match from the resolver and expose it to
    /// the stale-sibling cleanup. A record that had no match yet (or a series
    /// with no record) is marked <c>error</c> so the UI shows that the lookup
    /// could not be made, rather than that the series was not found.
    /// </summary>
    private async Task RecordRefreshFailureAsync(
        ComicMaintainerDbContext db,
        SeriesMetadataCacheEntity? entity,
        string key,
        string title,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            db.SeriesMetadataCache.Add(new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = title,
                Aliases = new List<string>(),
                UserAliases = new List<string>(),
                LastLookupUtc = now,
                LookupStatus = "error",
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else if (RankLookupStatus(entity.LookupStatus) <= RankLookupStatus("not_found")
                 && !string.Equals(entity.LookupStatus, "cleared", StringComparison.OrdinalIgnoreCase))
        {
            entity.LookupStatus = "error";
            entity.LastLookupUtc = now;
            entity.UpdatedAt = now;
        }
        else
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            MarkChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Recording the failure is best effort; the lookup failure itself
            // is what the caller needs to see.
            _logger.LogWarning(ex, "Failed to record the failed metadata refresh for {SeriesTitle}", LoggingHelper.SanitizeForLog(title));
        }
    }

    private async Task TryDownloadImageAsync(
        SeriesMetadataCacheEntity entity,
        ExternalSeriesMetadata? lookup,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.DownloadExternalSeriesImages) return;
        if (string.Equals(entity.ImageStatus, "user", StringComparison.OrdinalIgnoreCase))
        {
            // Respect user-uploaded image: never overwrite from refresh.
            return;
        }

        var remoteUrl = lookup?.ImageUrl;
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return;
        }

        // Skip a re-download when the URL hasn't changed and we already have
        // the file on disk — providers rotate URLs (e.g. CDN cache busting),
        // so an exact-match check is the right granularity.
        if (string.Equals(entity.RemoteImageUrl, remoteUrl, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(entity.LocalImageFile)
            && _imageStore.ResolveAbsolutePath(entity.LocalImageFile) is not null
            && string.Equals(entity.ImageStatus, "downloaded", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var result = await _imageStore.DownloadAsync(
                entity.NormalizedKey,
                remoteUrl,
                entity.LocalImageFile,
                cancellationToken);
            entity.RemoteImageUrl = remoteUrl;
            entity.LocalImageFile = result.FileName;
            entity.ImageContentType = result.ContentType;
            entity.ImageDownloadedUtc = DateTime.UtcNow;
            entity.ImageStatus = "downloaded";

            await TryWriteFolderCoverAsync(entity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(
                "Series image download failed for {SeriesKey}: {Message}",
                LoggingHelper.SanitizeForLog(entity.NormalizedKey),
                LoggingHelper.SanitizeForLog(ex.Message));
            entity.ImageStatus = "failed";
            entity.RemoteImageUrl = remoteUrl;
            // Leave LocalImageFile/ImageContentType untouched so a previously
            // good image keeps serving until the next successful refresh.
        }
    }

    /// <summary>
    /// Replace the cached series image with a user-supplied upload. Marks the
    /// record's image as <c>user</c> so future external refreshes won't
    /// overwrite it.
    /// </summary>
    public async Task<SeriesMetadataCacheRecord> SetUserImageAsync(
        string seriesTitle,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var key = NormalizeKey(seriesTitle);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = seriesTitle.Trim(),
                Aliases = new List<string>(),
                UserAliases = new List<string>(),
                LookupStatus = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }

        var result = await _imageStore.SaveUserImageAsync(
            key,
            content,
            contentType,
            entity.LocalImageFile,
            cancellationToken);

        entity.LocalImageFile = result.FileName;
        entity.ImageContentType = result.ContentType;
        entity.ImageDownloadedUtc = now;
        entity.ImageStatus = "user";
        entity.RemoteImageUrl = "user-upload";
        entity.UpdatedAt = now;

        await TryWriteFolderCoverAsync(entity, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    /// <summary>
    /// Download an external-provider image URL and persist it for the series
    /// key. Creates the cache row when missing. Refuses to overwrite an
    /// existing user-uploaded image (throws InvalidOperationException so the
    /// caller can return a clean 409).
    /// </summary>
    public async Task<SeriesMetadataCacheRecord> ApplyExternalImageAsync(
        string seriesTitle,
        string remoteImageUrl,
        string? source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }
        if (string.IsNullOrWhiteSpace(remoteImageUrl))
        {
            throw new ArgumentException("Remote image URL is required", nameof(remoteImageUrl));
        }

        var key = NormalizeKey(seriesTitle);
        var trimmedTitle = seriesTitle.Trim();

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = trimmedTitle,
                Aliases = new List<string>(),
                UserAliases = new List<string>(),
                LookupStatus = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else if (string.Equals(entity.ImageStatus, "user", StringComparison.OrdinalIgnoreCase))
        {
            // The image store / cache contract treats user uploads as sticky;
            // a fetch-from-provider action must not silently clobber them.
            throw new InvalidOperationException(
                "A user-uploaded image is already set for this series. Clear it before fetching from a provider.");
        }

        // ISeriesImageStore performs URL/scheme validation, SSRF guard, size
        // cap, magic-byte validation, and cleanup of the previous file.
        var result = await _imageStore.DownloadAsync(
            entity.NormalizedKey,
            remoteImageUrl,
            entity.LocalImageFile,
            cancellationToken);

        entity.RemoteImageUrl = remoteImageUrl;
        entity.LocalImageFile = result.FileName;
        entity.ImageContentType = result.ContentType;
        entity.ImageDownloadedUtc = now;
        entity.ImageStatus = "downloaded";
        // Attribute the record to the image's provider only while it has no
        // match: Source (with SourceId, when known) identifies the entry a
        // refresh re-fetches, so a cover picked from another provider must not
        // re-point a matched series.
        if (!string.IsNullOrWhiteSpace(source) && !HasStatus(entity, "manual_match") && !HasStatus(entity, "success"))
        {
            entity.Source = source;
        }
        entity.UpdatedAt = now;

        await TryWriteFolderCoverAsync(entity, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    /// <summary>
    /// Clear any cached series image (downloaded or user-uploaded). The next
    /// metadata refresh will be free to re-download an external image.
    /// </summary>
    public async Task<SeriesMetadataCacheRecord?> ClearImageAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == normalizedKey, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(entity.LocalImageFile))
        {
            _imageStore.Delete(entity.LocalImageFile);
        }
        entity.LocalImageFile = null;
        entity.ImageContentType = null;
        entity.ImageDownloadedUtc = null;
        entity.ImageStatus = "none";
        entity.RemoteImageUrl = null;
        entity.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _folderCoverWriter.RemoveAsync(entity.NormalizedKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex,
                "Failed to remove on-disk series cover for {Key}",
                LoggingHelper.SanitizeForLog(entity.NormalizedKey));
        }

        // Embedded first-archive cover removal is deferred to the background
        // queue: rewriting the archive on disk is comparatively expensive and
        // does not need to block the metadata update.
        _archiveCoverQueue.EnqueueRemove(entity.NormalizedKey);

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    public Task<bool> ReapplyImageArtifactsAsync(
        string seriesTitle,
        CancellationToken cancellationToken = default)
    {
        return ReapplyImageArtifactsAsync(seriesTitle, embedArchiveInline: false, cancellationToken);
    }

    public async Task<bool> ReapplyImageArtifactsAsync(
        string seriesTitle,
        bool embedArchiveInline,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken, tracked: false);
        if (entity is null
            || string.IsNullOrWhiteSpace(entity.LocalImageFile)
            || string.IsNullOrWhiteSpace(entity.ImageContentType)
            || string.IsNullOrWhiteSpace(entity.ImageStatus)
            || string.Equals(entity.ImageStatus, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.ImageStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourcePath = _imageStore.ResolveAbsolutePath(entity.LocalImageFile);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        await TryWriteFolderCoverAsync(entity, cancellationToken, force: true, embedArchiveInline: embedArchiveInline);
        return true;
    }

    /// <summary>
    /// Best-effort: copy the freshly-persisted cached cover image into each
    /// on-disk folder that backs the series, as <c>cover.&lt;ext&gt;</c>. The
    /// writer itself is guarded by a feature flag and swallows per-folder
    /// failures; this wrapper exists only to translate the persisted
    /// LocalImageFile into an absolute path and to ensure any unexpected
    /// throw never bubbles into the metadata update.
    /// </summary>
    private async Task TryWriteFolderCoverAsync(
        SeriesMetadataCacheEntity entity,
        CancellationToken cancellationToken,
        bool force = false,
        bool embedArchiveInline = false)
    {
        if (string.IsNullOrEmpty(entity.LocalImageFile)
            || string.IsNullOrEmpty(entity.ImageContentType))
        {
            return;
        }

        try
        {
            var sourcePath = _imageStore.ResolveAbsolutePath(entity.LocalImageFile);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return;
            }
            await _folderCoverWriter.WriteAsync(
                entity.NormalizedKey,
                sourcePath,
                entity.ImageContentType,
                force,
                cancellationToken);

            if (embedArchiveInline)
            {
                // Embed the first-archive cover synchronously so an explicit,
                // user-initiated re-apply surfaces real progress (the archive
                // rewrite is the slow part) instead of completing instantly and
                // deferring the heavy work to the background queue invisibly.
                await _archiveCoverWriter.WriteAsync(
                    entity.NormalizedKey,
                    sourcePath,
                    entity.ImageContentType,
                    force,
                    cancellationToken);
            }
            else
            {
                // Embedded first-archive cover writes are deferred to the background
                // queue: rewriting the archive on disk is comparatively expensive
                // and does not need to block the metadata update.
                _archiveCoverQueue.EnqueueWrite(
                    entity.NormalizedKey,
                    sourcePath,
                    entity.ImageContentType,
                    force);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex,
                "Failed to write on-disk series cover for {Key}",
                LoggingHelper.SanitizeForLog(entity.NormalizedKey));
        }
    }

    /// <summary>
    /// Manually adopt an externally-supplied candidate as the cached metadata
    /// for the series. Used to correct a wrong automatic match.
    /// </summary>
    public Task<SeriesMetadataCacheRecord> ApplyExternalMatchAsync(
        string seriesTitle,
        ExternalSeriesMetadata match,
        CancellationToken cancellationToken = default)
        => UpsertExternalLookupAsync(seriesTitle, match, "manual_match", cancellationToken);

    /// <summary>
    /// Persist an automatic provider lookup (e.g. the one performed by
    /// <see cref="ISeriesNameResolver"/>'s external-lookup step) so it survives
    /// across normalize calls without re-querying the provider. Recorded with
    /// status <c>success</c>.
    /// </summary>
    public Task<SeriesMetadataCacheRecord> PersistExternalLookupAsync(
        string seriesTitle,
        ExternalSeriesMetadata lookup,
        CancellationToken cancellationToken = default)
        => UpsertExternalLookupAsync(seriesTitle, lookup, "success", cancellationToken);

    private async Task<SeriesMetadataCacheRecord> UpsertExternalLookupAsync(
        string seriesTitle,
        ExternalSeriesMetadata match,
        string lookupStatus,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }
        if (match is null)
        {
            throw new ArgumentNullException(nameof(match));
        }
        if (string.IsNullOrWhiteSpace(match.CanonicalTitle))
        {
            throw new ArgumentException("Match must have a canonical title", nameof(match));
        }

        var key = NormalizeKey(seriesTitle);
        var now = DateTime.UtcNow;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = match.CanonicalTitle,
                Aliases = match.Aliases?.ToList() ?? new List<string>(),
                UserAliases = new List<string>(),
                Source = match.Source,
                SourceId = match.SourceId,
                LastLookupUtc = now,
                LookupStatus = lookupStatus,
                LocalizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(match)),
                Synopsis = SynopsisTextNormalizer.Normalize(match.Synopsis),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            // The provider canonical title is always refreshed; the user's
            // chosen display name (SeriesName) is independent and sticky.
            entity.CanonicalTitle = match.CanonicalTitle;
            entity.Aliases = match.Aliases?.ToList() ?? new List<string>();
            entity.Source = match.Source;
            entity.SourceId = match.SourceId;
            entity.LastLookupUtc = now;
            entity.LocalizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(match));
            entity.LookupStatus = lookupStatus;
            // Refresh the synopsis when the provider returned one; preserve the
            // existing synopsis when this match carries none (e.g. a manual
            // match selected from a candidate without a description).
            var normalizedSynopsis = SynopsisTextNormalizer.Normalize(match.Synopsis);
            if (!string.IsNullOrWhiteSpace(normalizedSynopsis))
            {
                entity.Synopsis = normalizedSynopsis;
            }
            entity.UpdatedAt = now;
            BumpMetadataVersion(entity);
        }

        // Try to grab the candidate's image. User-uploaded images are sticky.
        await TryDownloadImageAsync(entity, match, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    /// <summary>
    /// Clear the external-metadata fields cached for a series. Preserves
    /// user-managed aliases and a user-uploaded image; drops a
    /// provider-downloaded image.
    /// </summary>
    public async Task<SeriesMetadataCacheRecord?> ClearExternalMetadataAsync(
        string normalizedKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == normalizedKey, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // Drop provider-supplied aliases / source / lookup metadata. Keep
        // user aliases and (if set) the user's canonical-title override.
        entity.Aliases = new List<string>();
        entity.Source = null;
        entity.SourceId = null;
        entity.LastLookupUtc = null;
        entity.LookupStatus = "cleared";
        entity.LocalizedTitlesJson = null;
        entity.Synopsis = null;

        // Revert the provider-derived canonical title to the normalized key
        // so the UI shows a recognisable placeholder rather than a stale
        // provider title. The user's chosen display name (SeriesName), if
        // any, is independent and preserved.
        entity.CanonicalTitle = entity.NormalizedKey;

        // Drop any provider-downloaded image. A user-uploaded image is
        // sticky and must survive a metadata clear.
        if (!string.Equals(entity.ImageStatus, "user", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(entity.LocalImageFile))
            {
                _imageStore.Delete(entity.LocalImageFile);
            }
            entity.LocalImageFile = null;
            entity.ImageContentType = null;
            entity.ImageDownloadedUtc = null;
            entity.ImageStatus = "none";
            entity.RemoteImageUrl = null;
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        MarkChanged();
        return ToRecord(entity);
    }

    private SeriesMetadataCacheRecord ToRecord(SeriesMetadataCacheEntity entity)
    {
        var record = new SeriesMetadataCacheRecord
        {
            NormalizedKey = entity.NormalizedKey,
            CanonicalTitle = entity.CanonicalTitle,
            Aliases = entity.Aliases?.ToList() ?? new List<string>(),
            UserAliases = entity.UserAliases?.ToList() ?? new List<string>(),
            SeriesName = entity.SeriesName,
            Source = entity.Source,
            SourceId = entity.SourceId,
            LastLookupUtc = entity.LastLookupUtc,
            LookupStatus = entity.LookupStatus,
            RemoteImageUrl = entity.RemoteImageUrl,
            LocalImageFile = entity.LocalImageFile,
            ImageContentType = entity.ImageContentType,
            ImageDownloadedUtc = entity.ImageDownloadedUtc,
            ImageStatus = entity.ImageStatus,
            PreferredLanguage = entity.PreferredLanguage,
            LocalizedTitles = DeserializeLocalizedTitles(entity.LocalizedTitlesJson),
            MetadataVersion = entity.MetadataVersion,
            Synopsis = entity.Synopsis
        };

        // Compute the single authoritative name via the shared resolver so
        // the value the UI shows matches what the normalize pipeline writes
        // into ComicInfo.xml's <Series> for files reaching this record.
        record.ResolvedSeriesName = SeriesDisplayTitleResolver.Resolve(
            record, _settings.CurrentValue.DefaultPreferredLanguage);
        return record;
    }

    /// <summary>
    /// Increment the entity's <see cref="SeriesMetadataCacheEntity.MetadataVersion"/>
    /// to mark all files belonging to this series as needing a re-normalize
    /// pass on the next library scan. Should be called from every mutation
    /// path that could affect the resolved series title or language
    /// preference (canonical edits, alias changes, language preference
    /// changes, fresh matches, etc.). New entities start at 1.
    /// </summary>
    private static void BumpMetadataVersion(SeriesMetadataCacheEntity entity)
    {
        // Use unchecked + saturation on overflow rather than wrapping so a
        // bumped record never compares less-than a previously-stamped file.
        if (entity.MetadataVersion < int.MaxValue)
        {
            entity.MetadataVersion++;
        }
    }

    /// <summary>
    /// Serialize a list of <see cref="LocalizedTitle"/> values for persistence
    /// in <see cref="SeriesMetadataCacheEntity.LocalizedTitlesJson"/>. Returns
    /// null when the list is null/empty so the DB column stays sparse.
    /// </summary>
    private static string? SerializeLocalizedTitles(IEnumerable<LocalizedTitle>? titles)
    {
        if (titles is null) return null;
        var list = titles
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
            .ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    /// <summary>Inverse of <see cref="SerializeLocalizedTitles"/>; never throws.</summary>
    private static List<LocalizedTitle> DeserializeLocalizedTitles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<LocalizedTitle>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<LocalizedTitle>>(json) ?? new List<LocalizedTitle>();
        }
        catch (JsonException)
        {
            return new List<LocalizedTitle>();
        }
    }

    /// <summary>
    /// Build the localized-title list to persist on the cache row from a
    /// provider lookup. Always seeds the entry with the canonical title (and
    /// provider aliases as untagged fallbacks) when the provider didn't supply
    /// explicit language tags, so the legacy data shape still flows through
    /// the resolver consistently.
    /// </summary>
    private static List<LocalizedTitle> BuildLocalizedTitles(ExternalSeriesMetadata lookup)
    {
        if (lookup.LocalizedTitles is { Count: > 0 })
        {
            return lookup.LocalizedTitles
                .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Title))
                .Select(t => new LocalizedTitle(t.Title, t.Language))
                .ToList();
        }

        var fallback = new List<LocalizedTitle>();
        if (!string.IsNullOrWhiteSpace(lookup.CanonicalTitle))
        {
            fallback.Add(new LocalizedTitle(lookup.CanonicalTitle, null));
        }
        if (lookup.Aliases is { Count: > 0 })
        {
            fallback.AddRange(lookup.Aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => new LocalizedTitle(alias, null)));
        }
        return fallback;
    }

    /// <inheritdoc />
    public async Task<SeriesMetadataCacheRecord> SetPreferredLanguageAsync(
        string seriesTitle,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        // Throws ArgumentException for unsupported codes; null/empty means "clear".
        var normalizedLanguage = SeriesLanguagePreference.ValidateOrThrow(language);

        var key = NormalizeKey(seriesTitle);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = seriesTitle.Trim(),
                Aliases = new List<string>(),
                UserAliases = new List<string>(),
                LookupStatus = "manual",
                PreferredLanguage = normalizedLanguage,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            entity.PreferredLanguage = normalizedLanguage;
            entity.UpdatedAt = now;
            BumpMetadataVersion(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        MarkChanged();
        return ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task<SeriesMetadataCacheRecord?> SetSeriesNameAsync(
        string seriesTitle,
        string? seriesName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        var trimmed = seriesName?.Trim();
        if (entity is null)
        {
            // No cache record exists for this series. This happens for series
            // that were never matched (grouped purely from files on disk),
            // leaving them in a "bad state" where the pinned name could not be
            // set at all. Reverting to automatic is a no-op here (there is
            // nothing pinned), but pinning an explicit name must still work so
            // the user can fix the series. Create a minimal manual record to
            // attach the user-selected name to, mirroring SetUserAliasesAsync
            // and SetPreferredLanguageAsync.
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            var createdAt = DateTime.UtcNow;
            var canonical = seriesTitle.Trim();
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = NormalizeKey(seriesTitle),
                CanonicalTitle = canonical,
                Aliases = new List<string>(),
                // If the pinned name differs from the series title we are
                // keying the record by, keep the candidate pool
                // self-describing by recording it as a user alias too.
                UserAliases = string.Equals(canonical, trimmed, StringComparison.OrdinalIgnoreCase)
                    ? new List<string>()
                    : new List<string> { trimmed },
                LookupStatus = "manual",
                SeriesName = trimmed,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            db.SeriesMetadataCache.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            MarkChanged();
            return ToRecord(entity);
        }

        if (string.IsNullOrEmpty(trimmed))
        {
            // Revert to automatic resolution.
            if (entity.SeriesName is not null)
            {
                entity.SeriesName = null;
                entity.UpdatedAt = DateTime.UtcNow;
                BumpMetadataVersion(entity);
                await db.SaveChangesAsync(cancellationToken);
                MarkChanged();
            }
            return ToRecord(entity);
        }

        // The chosen name must be one the system already knows for this
        // series: its canonical title, a provider alias, a localized title,
        // or an existing user alias. If it is a brand-new custom name, add it
        // to the user-alias list so the candidate pool stays self-describing.
        var known = new List<string?> { entity.CanonicalTitle };
        known.AddRange(entity.Aliases ?? new List<string>());
        known.AddRange(entity.UserAliases ?? new List<string>());
        known.AddRange(DeserializeLocalizedTitles(entity.LocalizedTitlesJson)
            .Where(lt => lt is not null)
            .Select(lt => lt!.Title));

        var match = known
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .FirstOrDefault(k => string.Equals(k!.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

        var resolvedName = match?.Trim() ?? trimmed;
        if (match is null)
        {
            var userAliases = entity.UserAliases?.ToList() ?? new List<string>();
            if (!userAliases.Any(a => string.Equals(a, resolvedName, StringComparison.OrdinalIgnoreCase)))
            {
                userAliases.Add(resolvedName);
                entity.UserAliases = userAliases;
            }
        }

        if (!string.Equals(entity.SeriesName, resolvedName, StringComparison.Ordinal))
        {
            entity.SeriesName = resolvedName;
            entity.UpdatedAt = DateTime.UtcNow;
            BumpMetadataVersion(entity);
            await db.SaveChangesAsync(cancellationToken);
            MarkChanged();
        }

        return ToRecord(entity);
    }

    /// <summary>
    /// Removes redundant "stale sibling" cache records. A folder refresh issues
    /// a separate external lookup for a series' canonical title AND each alias,
    /// so a single logical series can accumulate extra rows — typically a
    /// <c>not_found</c> / <c>error</c> sibling keyed by an alias that is already
    /// owned (as a canonical title, alias, or localized title) by a more
    /// authoritative record. These siblings carry no unique data, yet they
    /// clutter the cache and can shadow the authoritative record's resolved
    /// name. This deletes any such sibling that (a) carries no user-specific
    /// data (no pinned name, user aliases, preferred language, or user-uploaded
    /// image), (b) has a non-positive lookup status (<c>null</c> /
    /// <c>not_found</c> / <c>error</c>), and (c) is subsumed by another, strictly
    /// more authoritative record. Returns the number of records removed.
    /// </summary>
    public async Task<int> CleanupStaleSiblingRecordsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.SeriesMetadataCache.ToListAsync(cancellationToken);
        if (entities.Count < 2)
        {
            return 0;
        }

        var titleKeys = entities.ToDictionary(e => e.NormalizedKey, TitleKeysOf, StringComparer.Ordinal);
        var toRemove = new List<SeriesMetadataCacheEntity>();
        foreach (var candidate in entities)
        {
            if (!IsDeletableStaleSibling(candidate))
            {
                continue;
            }

            // Keep the sibling only if some other, strictly more authoritative
            // record already claims its key as one of its own titles. That
            // record owns the series, so the sibling is redundant and removing
            // it cannot orphan a folder grouping.
            var subsumed = entities.Any(other =>
                !ReferenceEquals(other, candidate)
                && !string.Equals(other.NormalizedKey, candidate.NormalizedKey, StringComparison.Ordinal)
                && RankLookupStatus(other.LookupStatus) > RankLookupStatus(candidate.LookupStatus)
                && titleKeys[other.NormalizedKey].Contains(candidate.NormalizedKey));

            if (subsumed)
            {
                toRemove.Add(candidate);
            }
        }

        if (toRemove.Count == 0)
        {
            return 0;
        }

        db.SeriesMetadataCache.RemoveRange(toRemove);
        await db.SaveChangesAsync(cancellationToken);
        MarkChanged();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Cleaned up {Count} stale sibling series-metadata cache record(s): {Keys}",
                toRemove.Count,
                LoggingHelper.SanitizeForLog(string.Join(", ", toRemove.Select(e => e.NormalizedKey))));
        }

        return toRemove.Count;
    }

    /// <summary>
    /// True when a record carries no user-specific data and has a non-positive
    /// lookup status, making it a candidate for stale-sibling cleanup. User
    /// data (a pinned name, user aliases, a preferred language, or a
    /// user-uploaded image) and positive statuses (<c>success</c> /
    /// <c>manual</c> / <c>manual_match</c>) protect a record from deletion. A
    /// <c>cleared</c> status is also protected: it encodes a deliberate user
    /// decision to suppress external metadata.
    /// </summary>
    private static bool IsDeletableStaleSibling(SeriesMetadataCacheEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.SeriesName)) return false;
        if (entity.UserAliases is { Count: > 0 }) return false;
        if (!string.IsNullOrWhiteSpace(entity.PreferredLanguage)) return false;
        if (string.Equals(entity.ImageStatus, "user", StringComparison.OrdinalIgnoreCase)) return false;

        var status = entity.LookupStatus?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(status) || status == "not_found" || status == "error";
    }
}
