using System.Text.Json;
using System.Text.RegularExpressions;
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
public class SeriesMetadataCacheService : ISeriesMetadataCacheService
{
    // Unicode-aware: keep any Unicode letter (\p{L}) or number (\p{N}) so
    // non-ASCII titles (CJK, accented Latin, Cyrillic, etc.) produce rich,
    // distinguishable keys instead of collapsing to a bare digit when every
    // letter gets stripped. Pure-ASCII titles still produce the same output
    // as the previous [^a-z0-9]+ sanitizer.
    private static readonly Regex SeriesKeySanitizer = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private readonly IDbContextFactory<ComicMaintainerDbContext> _dbContextFactory;
    private readonly IExternalSeriesMetadataService _externalMetadata;
    private readonly ISeriesImageStore _imageStore;
    private readonly ISeriesFolderCoverWriter _folderCoverWriter;
    private readonly ISeriesArchiveCoverWriteQueue _archiveCoverQueue;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesMetadataCacheService> _logger;

    public SeriesMetadataCacheService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IExternalSeriesMetadataService externalMetadata,
        ISeriesImageStore imageStore,
        ISeriesFolderCoverWriter folderCoverWriter,
        ISeriesArchiveCoverWriteQueue archiveCoverQueue,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesMetadataCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _externalMetadata = externalMetadata;
        _imageStore = imageStore;
        _folderCoverWriter = folderCoverWriter;
        _archiveCoverQueue = archiveCoverQueue;
        _settings = settings;
        _logger = logger;
    }

    public string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }

        var normalized = SeriesKeySanitizer.Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }

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
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>
    /// Resolves the cache entity for a free-form series title. Tries an exact
    /// normalized-key lookup first, then falls back to scanning every record's
    /// known titles (canonical title, user-selected name, provider/user
    /// aliases, and localized titles). The fallback is what keeps a series
    /// editable after a manual match: applying a match leaves the record keyed
    /// by the original folder title while the UI now refers to the series by
    /// its matched canonical title, so an exact-key lookup on that new title
    /// would otherwise miss the record.
    /// </summary>
    private async Task<SeriesMetadataCacheEntity?> ResolveEntityByTitleAsync(
        ComicMaintainerDbContext db,
        string seriesTitle,
        CancellationToken cancellationToken)
    {
        var key = NormalizeKey(seriesTitle);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        // Exact-key miss: the record may be keyed by an older title (typically
        // the original folder name) while the caller refers to the series by
        // its matched canonical title or one of its aliases. Resolve by
        // scanning known titles. Exact key is always preferred above.
        //
        // A single logical series can be described by several cache records
        // (e.g. a folder refresh issues a separate lookup for the canonical
        // title AND each alias, leaving one authoritative "success" record and
        // one or more stale "not_found" siblings). The library renders the
        // series using the most authoritative record (see
        // SeriesLibraryService.ResolveRecordForGroup). We must mirror that
        // ranking here so mutations (e.g. pinning a series name) land on the
        // SAME record the library displays — otherwise the change is written to
        // a stale sibling and the displayed name never updates.
        var candidates = await db.SeriesMetadataCache.ToListAsync(cancellationToken);
        return candidates
            .Where(e => EntityMatchesTitleKey(e, key))
            .OrderByDescending(e => RankLookupStatus(e.LookupStatus))
            .ThenByDescending(e => e.LastLookupUtc ?? DateTime.MinValue)
            .ThenByDescending(e => e.UpdatedAt)
            .FirstOrDefault();
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

    private bool EntityMatchesTitleKey(SeriesMetadataCacheEntity entity, string requestedKey)
    {
        if (KeyEquals(entity.CanonicalTitle, requestedKey)) return true;
        if (KeyEquals(entity.SeriesName, requestedKey)) return true;
        if (entity.Aliases is not null && entity.Aliases.Any(a => KeyEquals(a, requestedKey))) return true;
        if (entity.UserAliases is not null && entity.UserAliases.Any(a => KeyEquals(a, requestedKey))) return true;
        foreach (var localized in DeserializeLocalizedTitles(entity.LocalizedTitlesJson))
        {
            if (localized is not null && KeyEquals(localized.Title, requestedKey)) return true;
        }
        return false;
    }

    private bool KeyEquals(string? value, string requestedKey)
        => !string.IsNullOrWhiteSpace(value)
           && string.Equals(NormalizeKey(value), requestedKey, StringComparison.Ordinal);

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
        // that might pick a different result). We look up by the user-selected
        // canonical title and preserve the "manual_match" status so the
        // series stays marked as manually matched.
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
        var wasManualMatch = string.Equals(entity?.LookupStatus, "manual_match", StringComparison.OrdinalIgnoreCase);
        var lookupQuery = wasManualMatch && !string.IsNullOrWhiteSpace(entity!.CanonicalTitle)
            ? entity.CanonicalTitle
            : trimmedTitle;

        ExternalSeriesMetadata? lookup = null;
        string status;
        try
        {
            lookup = await _externalMetadata.LookupSeriesAsync(lookupQuery, cancellationToken);
            status = lookup is null ? "not_found" : "success";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External metadata refresh failed for {SeriesTitle}", LoggingHelper.SanitizeForLog(lookupQuery));
            status = "error";
        }

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
                entity.CanonicalTitle = string.IsNullOrWhiteSpace(lookup.CanonicalTitle) ? entity.CanonicalTitle : lookup.CanonicalTitle;
                entity.Aliases = lookup.Aliases?.ToList() ?? new List<string>();
                entity.Source = lookup.Source;
                entity.LastLookupUtc = now;
                entity.LocalizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(lookup));
                // A manually-matched series stays manually matched after a
                // successful refresh — the lookup just updates the cached
                // fields for the user-selected match.
                entity.LookupStatus = wasManualMatch ? "manual_match" : status;
                entity.UpdatedAt = now;
                // Successful refreshes can change canonical title, aliases,
                // or localized titles — bump so library scan re-normalizes
                // affected files.
                BumpMetadataVersion(entity);
            }
            else if (wasManualMatch)
            {
                // Lookup failed (not_found / error) for a series the user had
                // previously manually matched. Don't clobber the user's
                // selection — preserve the existing canonical title, aliases,
                // and source, and keep the manual_match status. Only record
                // that a refresh was attempted.
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
        return ToRecord(entity);
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
        catch (Exception ex)
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
        if (!string.IsNullOrWhiteSpace(source))
        {
            entity.Source = source;
        }
        entity.UpdatedAt = now;

        await TryWriteFolderCoverAsync(entity, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
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
        catch (Exception ex)
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
        return ToRecord(entity);
    }

    public async Task<bool> ReapplyImageArtifactsAsync(
        string seriesTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await ResolveEntityByTitleAsync(db, seriesTitle, cancellationToken);
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

        await TryWriteFolderCoverAsync(entity, cancellationToken, force: true);
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
        bool force = false)
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

            // Embedded first-archive cover writes are deferred to the background
            // queue: rewriting the archive on disk is comparatively expensive
            // and does not need to block the metadata update.
            _archiveCoverQueue.EnqueueWrite(
                entity.NormalizedKey,
                sourcePath,
                entity.ImageContentType,
                force);
        }
        catch (Exception ex)
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
            foreach (var alias in lookup.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    fallback.Add(new LocalizedTitle(alias, null));
                }
            }
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
                && EntityMatchesTitleKey(other, candidate.NormalizedKey));

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
