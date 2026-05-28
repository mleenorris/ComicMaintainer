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
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesMetadataCacheService> _logger;

    // Memoized alias index (alias-key -> owning record) used by
    // ResolveByTitleAsync so per-file resolution can map a folder/embedded
    // <Series> alias back to the record that owns the series' settings. Lazily
    // built from the full record set; invalidated on every mutation. The
    // service is registered as a singleton so this in-memory cache is shared.
    private readonly SemaphoreSlim _aliasIndexGate = new(1, 1);
    private volatile Dictionary<string, SeriesMetadataCacheRecord>? _aliasIndex;
    private volatile bool _aliasIndexDirty = true;

    public SeriesMetadataCacheService(
        IDbContextFactory<ComicMaintainerDbContext> dbContextFactory,
        IExternalSeriesMetadataService externalMetadata,
        ISeriesImageStore imageStore,
        ISeriesFolderCoverWriter folderCoverWriter,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesMetadataCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _externalMetadata = externalMetadata;
        _imageStore = imageStore;
        _folderCoverWriter = folderCoverWriter;
        _settings = settings;
        _logger = logger;
    }

    public string NormalizeKey(string? value) => StaticNormalizeKey(value);

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

    /// <inheritdoc />
    public async Task<SeriesMetadataCacheRecord?> ResolveByTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Prefer an authoritative exact-key match (the record's own key always
        // wins over any alias claim) before consulting the alias index.
        var key = NormalizeKey(title);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var direct = await GetAsync(key, cancellationToken);
            if (direct is not null)
            {
                return direct;
            }
        }

        if (IsAmbiguousNormalizedKey(key))
        {
            // A degenerate key (e.g. a CJK-only title that collapses to a bare
            // digit) must not bridge to whichever record happened to seed that
            // key in the alias index.
            return null;
        }

        var index = await GetAliasIndexAsync(cancellationToken);
        return index.TryGetValue(key, out var record) ? record : null;
    }

    /// <summary>
    /// Returns the memoized alias index, rebuilding it from the full record set
    /// when it has been invalidated by a mutation. The index maps each
    /// normalized alias key to the record that owns it, using the same
    /// canonical &gt; user-alias &gt; provider-alias precedence as the
    /// grouping layer, then localized titles as the weakest signal.
    /// </summary>
    private async Task<Dictionary<string, SeriesMetadataCacheRecord>> GetAliasIndexAsync(CancellationToken cancellationToken)
    {
        var current = _aliasIndex;
        if (!_aliasIndexDirty && current is not null)
        {
            return current;
        }

        await _aliasIndexGate.WaitAsync(cancellationToken);
        try
        {
            current = _aliasIndex;
            if (!_aliasIndexDirty && current is not null)
            {
                return current;
            }

            // Clear the dirty flag before loading so a concurrent mutation that
            // happens during the load re-marks the index dirty and forces a
            // rebuild on the next call.
            _aliasIndexDirty = false;
            var records = await GetAllAsync(cancellationToken);
            var index = BuildAliasIndex(records);
            _aliasIndex = index;
            return index;
        }
        catch
        {
            // Ensure a failed build is retried next time.
            _aliasIndexDirty = true;
            throw;
        }
        finally
        {
            _aliasIndexGate.Release();
        }
    }

    private void InvalidateAliasIndex() => _aliasIndexDirty = true;

    /// <summary>
    /// Builds an alias-key -&gt; owning-record map. Mirrors the precedence used
    /// by the grouping layer: pass 1 record key + canonical title, pass 2 user
    /// aliases, pass 3 provider aliases, pass 4 localized titles. Each pass uses
    /// <c>TryAdd</c> so a stronger claim is never overwritten, and degenerate
    /// keys are skipped so unrelated series can't be bridged together.
    /// </summary>
    private static Dictionary<string, SeriesMetadataCacheRecord> BuildAliasIndex(IReadOnlyList<SeriesMetadataCacheRecord> records)
    {
        var index = new Dictionary<string, SeriesMetadataCacheRecord>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: record keys and canonical titles.
        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.NormalizedKey))
            {
                index.TryAdd(record.NormalizedKey, record);
            }
            if (!string.IsNullOrWhiteSpace(record.CanonicalTitle))
            {
                var canonicalKey = StaticNormalizeKey(record.CanonicalTitle);
                if (!IsAmbiguousNormalizedKey(canonicalKey))
                {
                    index.TryAdd(canonicalKey, record);
                }
            }
        }

        AddAliasPass(index, records, r => r.UserAliases);
        AddAliasPass(index, records, r => r.Aliases);
        AddAliasPass(index, records, r => r.LocalizedTitles?.Select(t => t?.Title));

        return index;
    }

    private static void AddAliasPass(
        Dictionary<string, SeriesMetadataCacheRecord> index,
        IReadOnlyList<SeriesMetadataCacheRecord> records,
        Func<SeriesMetadataCacheRecord, IEnumerable<string?>?> selector)
    {
        foreach (var record in records)
        {
            var aliases = selector(record);
            if (aliases is null) continue;
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var aliasKey = StaticNormalizeKey(alias);
                if (IsAmbiguousNormalizedKey(aliasKey)) continue;
                index.TryAdd(aliasKey, record);
            }
        }
    }

    private static string StaticNormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-series";
        }
        var normalized = SeriesKeySanitizer.Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-series" : normalized;
    }

    /// <summary>
    /// True for keys that are too short/degenerate to safely bridge series via
    /// the alias index (empty, single-character, the unknown sentinel, or
    /// digits/punctuation-only — e.g. CJK-only titles that collapse to a bare
    /// number).
    /// </summary>
    private static bool IsAmbiguousNormalizedKey(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length < 2
            || string.Equals(normalized, "unknown-series", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var ch in normalized)
        {
            if (char.IsLetter(ch))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<SeriesMetadataCacheRecord> SetUserAliasesAsync(
        string seriesTitle,
        IEnumerable<string> userAliases,
        string? canonicalTitleOverride,
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
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = string.IsNullOrWhiteSpace(canonicalTitleOverride) ? seriesTitle.Trim() : canonicalTitleOverride.Trim(),
                Aliases = new List<string>(),
                UserAliases = cleanedAliases,
                IsUserCanonical = !string.IsNullOrWhiteSpace(canonicalTitleOverride),
                LookupStatus = "manual",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            entity.UserAliases = cleanedAliases;
            if (!string.IsNullOrWhiteSpace(canonicalTitleOverride))
            {
                entity.CanonicalTitle = canonicalTitleOverride.Trim();
                entity.IsUserCanonical = true;
            }
            entity.UpdatedAt = now;
            BumpMetadataVersion(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        InvalidateAliasIndex();
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
        InvalidateAliasIndex();
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
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
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
                IsUserCanonical = false,
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
                if (!entity.IsUserCanonical)
                {
                    entity.CanonicalTitle = string.IsNullOrWhiteSpace(lookup.CanonicalTitle) ? entity.CanonicalTitle : lookup.CanonicalTitle;
                }
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

        InvalidateAliasIndex();
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
                IsUserCanonical = false,
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

        InvalidateAliasIndex();
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
                IsUserCanonical = false,
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

        InvalidateAliasIndex();
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

        await db.SaveChangesAsync(cancellationToken);

        InvalidateAliasIndex();
        return ToRecord(entity);
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
        CancellationToken cancellationToken)
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
                cancellationToken);
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
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        if (entity is null)
        {
            entity = new SeriesMetadataCacheEntity
            {
                NormalizedKey = key,
                CanonicalTitle = match.CanonicalTitle,
                Aliases = match.Aliases?.ToList() ?? new List<string>(),
                UserAliases = new List<string>(),
                IsUserCanonical = false,
                Source = match.Source,
                LastLookupUtc = now,
                LookupStatus = lookupStatus,
                LocalizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(match)),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SeriesMetadataCache.Add(entity);
        }
        else
        {
            // Preserve a user-overridden canonical title; only update the
            // provider-derived fields. A user can change the canonical title
            // separately via SetUserAliasesAsync.
            if (!entity.IsUserCanonical)
            {
                entity.CanonicalTitle = match.CanonicalTitle;
            }
            entity.Aliases = match.Aliases?.ToList() ?? new List<string>();
            entity.Source = match.Source;
            entity.LastLookupUtc = now;
            entity.LocalizedTitlesJson = SerializeLocalizedTitles(BuildLocalizedTitles(match));
            entity.LookupStatus = lookupStatus;
            entity.UpdatedAt = now;
            BumpMetadataVersion(entity);
        }

        // Try to grab the candidate's image. User-uploaded images are sticky.
        await TryDownloadImageAsync(entity, match, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        InvalidateAliasIndex();
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

        // If the canonical title was provider-derived, revert it to the
        // normalized key so the UI shows a recognisable placeholder rather
        // than a stale provider title. When the user has overridden the
        // canonical title we leave it alone.
        if (!entity.IsUserCanonical)
        {
            entity.CanonicalTitle = entity.NormalizedKey;
        }

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
        InvalidateAliasIndex();
        return ToRecord(entity);
    }

    private static SeriesMetadataCacheRecord ToRecord(SeriesMetadataCacheEntity entity)
    {
        return new SeriesMetadataCacheRecord
        {
            NormalizedKey = entity.NormalizedKey,
            CanonicalTitle = entity.CanonicalTitle,
            Aliases = entity.Aliases?.ToList() ?? new List<string>(),
            UserAliases = entity.UserAliases?.ToList() ?? new List<string>(),
            IsUserCanonical = entity.IsUserCanonical,
            Source = entity.Source,
            LastLookupUtc = entity.LastLookupUtc,
            LookupStatus = entity.LookupStatus,
            RemoteImageUrl = entity.RemoteImageUrl,
            LocalImageFile = entity.LocalImageFile,
            ImageContentType = entity.ImageContentType,
            ImageDownloadedUtc = entity.ImageDownloadedUtc,
            ImageStatus = entity.ImageStatus,
            PreferredLanguage = entity.PreferredLanguage,
            PinnedLocalizedTitle = entity.PinnedLocalizedTitle,
            LocalizedTitles = DeserializeLocalizedTitles(entity.LocalizedTitlesJson),
            MetadataVersion = entity.MetadataVersion
        };
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
                IsUserCanonical = false,
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

        InvalidateAliasIndex();
        return ToRecord(entity);
    }

    /// <inheritdoc />
    public async Task<SeriesMetadataCacheRecord?> SetPinnedLocalizedTitleAsync(
        string seriesTitle,
        string? pinnedTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesTitle))
        {
            throw new ArgumentException("Series title is required", nameof(seriesTitle));
        }

        var key = NormalizeKey(seriesTitle);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SeriesMetadataCache.FirstOrDefaultAsync(e => e.NormalizedKey == key, cancellationToken);
        if (entity is null)
        {
            // Pins are only meaningful for series the cache already knows
            // about (we need the set of LocalizedTitles to validate against).
            return null;
        }

        var trimmedPin = pinnedTitle?.Trim();
        if (string.IsNullOrEmpty(trimmedPin))
        {
            // Clear the pin.
            if (entity.PinnedLocalizedTitle is not null)
            {
                entity.PinnedLocalizedTitle = null;
                entity.UpdatedAt = DateTime.UtcNow;
                BumpMetadataVersion(entity);
                await db.SaveChangesAsync(cancellationToken);
                InvalidateAliasIndex();
            }
            return ToRecord(entity);
        }

        // Validate the pin against the record's canonical title or any of its
        // localized titles. The persisted value is the matched record-side
        // string so the pin survives case/whitespace-only differences in
        // user input.
        string? canonicalValue = null;
        if (!string.IsNullOrWhiteSpace(entity.CanonicalTitle)
            && string.Equals(entity.CanonicalTitle.Trim(), trimmedPin, StringComparison.OrdinalIgnoreCase))
        {
            canonicalValue = entity.CanonicalTitle.Trim();
        }
        else
        {
            var localized = DeserializeLocalizedTitles(entity.LocalizedTitlesJson);
            foreach (var lt in localized)
            {
                if (!string.IsNullOrWhiteSpace(lt?.Title)
                    && string.Equals(lt!.Title.Trim(), trimmedPin, StringComparison.OrdinalIgnoreCase))
                {
                    canonicalValue = lt.Title.Trim();
                    break;
                }
            }
        }

        if (canonicalValue is null)
        {
            throw new ArgumentException(
                $"Pinned title '{trimmedPin}' must match the canonical title or one of the localized titles for series '{seriesTitle}'.",
                nameof(pinnedTitle));
        }

        if (!string.Equals(entity.PinnedLocalizedTitle, canonicalValue, StringComparison.Ordinal))
        {
            entity.PinnedLocalizedTitle = canonicalValue;
            entity.UpdatedAt = DateTime.UtcNow;
            BumpMetadataVersion(entity);
            await db.SaveChangesAsync(cancellationToken);
            InvalidateAliasIndex();
        }

        return ToRecord(entity);
    }
}
