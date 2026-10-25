using System.Text.RegularExpressions;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

public partial class SeriesLibraryService : ISeriesLibraryService
{
    // Unicode-aware: keep any Unicode letter (\p{L}) or number (\p{N}) so
    // non-ASCII titles (CJK, accented Latin, Cyrillic, etc.) produce rich,
    // distinguishable keys instead of collapsing to a bare digit when every
    // letter gets stripped. Pure-ASCII titles still produce the same output
    // as the previous [^a-z0-9]+ sanitizer.
    private static readonly Regex SeriesKeySanitizer = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    // Series-level filters that operate on the grouped/series record (rather
    // than on individual files in the file store). These are applied AFTER
    // BuildGroupsAsync so the file-store call sees a no-op file filter.
    private const string ProviderMatchedFilter = "matched";
    private const string ProviderUnmatchedFilter = "unmatched";
    private const string MissingIssuesFilter = "missing";

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesMetadataCacheService _metadataCache;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly ILogger<SeriesLibraryService> _logger;
    private readonly ISeriesNameResolver? _seriesNameResolver;
    private readonly IUserContextAccessor? _userContext;

    /// <summary>
    /// Safety net on top of version-based invalidation: a snapshot is never
    /// served for longer than this, so any change that slips past the change
    /// counters (e.g. an out-of-band database edit) self-heals quickly.
    /// </summary>
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound on cached summary views per snapshot (filter/search/sort combos).</summary>
    private const int MaxSummaryViewsPerSnapshot = 32;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<GroupsCacheKey, GroupsSnapshot> _snapshotCache = new();
    private readonly SemaphoreSlim _snapshotBuildLock = new(1, 1);

    public SeriesLibraryService(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ISeriesMetadataCacheService metadataCache,
        IOptionsMonitor<AppSettings> settings,
        ILogger<SeriesLibraryService> logger,
        ISeriesNameResolver? seriesNameResolver = null,
        IUserContextAccessor? userContext = null)
    {
        _userContext = userContext;
        _fileStore = fileStore;
        _processor = processor;
        _metadataCache = metadataCache;
        _settings = settings;
        _logger = logger;
        _seriesNameResolver = seriesNameResolver;
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

    /// <summary>
    /// Computes the public series id used in <c>#/series/{id}</c> deep links and
    /// id-based API lookups. When the series carries an explicit pinned
    /// (user-selected) name, the id is the slug of that name so the URL matches
    /// the displayed title; otherwise it falls back to the union-find
    /// <paramref name="representative"/> key. The pinned slug is rejected (and
    /// the representative used instead) when it would be ambiguous — e.g. empty,
    /// "unknown-series", or digit-only — to avoid degenerate ids.
    /// </summary>
    private static string ComputePublicSeriesId(string? pinnedName, string representative)
    {
        if (string.IsNullOrWhiteSpace(pinnedName))
        {
            return representative;
        }

        var slug = NormalizeKey(pinnedName);
        return IsAmbiguousNormalizedKey(slug) ? representative : slug;
    }

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds()
            : new DateTimeOffset(value).ToUnixTimeSeconds();

    private static string? BuildExternalImageUrl(SeriesAccumulator accumulator)
    {
        if (!accumulator.HasExternalImage || string.IsNullOrEmpty(accumulator.ImageNormalizedKey))
        {
            return null;
        }
        // Image URL is rendered as a relative path so the front-end can prefix
        // it with the configured BasePath via apiUrl(), the same way every
        // other endpoint reference is built.
        return $"/api/series-images/{Uri.EscapeDataString(accumulator.ImageNormalizedKey)}";
    }
}

