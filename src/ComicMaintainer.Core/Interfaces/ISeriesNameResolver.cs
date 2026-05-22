using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Resolves the canonical/preferred series name that should be written to a
/// comic file's <c>&lt;Series&gt;</c> tag.
///
/// <para>
/// The resolution priority is a single, ordered list. The first step to
/// produce a non-empty title wins; documenting it explicitly in one place
/// guarantees that the normalize pipeline, the metadata audit, the
/// file-naming audit, the library-scan stale-retag pass, and the per-file
/// diagnostic endpoint never disagree about what the expected series is.
/// </para>
///
/// <list type="number">
///   <item><b>UserCanonical</b> — A <see cref="SeriesMetadataCacheRecord"/>
///     reachable from any candidate key (file's <c>&lt;Series&gt;</c>,
///     folder name, or any existing alias) with
///     <see cref="SeriesMetadataCacheRecord.IsUserCanonical"/>=true.
///     Wins unconditionally — represents an explicit user decision.</item>
///   <item><b>MatchedCache</b> — A cache record reachable from any candidate
///     key whose <see cref="SeriesMetadataCacheRecord.LookupStatus"/>
///     indicates a successful or manual match. Routed through
///     <see cref="Services.SeriesDisplayTitleResolver"/> so the per-series
///     <see cref="SeriesMetadataCacheRecord.PreferredLanguage"/> → global
///     default → <see cref="SeriesMetadataCacheRecord.CanonicalTitle"/>
///     priority decides which string is emitted.</item>
///   <item><b>LocalizedTitleBackref</b> — When the file's
///     <c>&lt;Series&gt;</c> is already a localized variant that doesn't
///     index directly under the cache, fall back to the folder's record and
///     pick its resolved display title when the file's <c>&lt;Series&gt;</c>
///     appears in that record's
///     <see cref="SeriesMetadataCacheRecord.LocalizedTitles"/>.</item>
///   <item><b>ExternalLookup</b> — Live external provider lookup wrapped in a
///     transient cache-record. Only the global default preferred-language
///     applies (no per-series override exists yet).</item>
///   <item><b>ExistingMetadata</b> — Preserve the file's existing
///     <c>&lt;Series&gt;</c> value rather than overwriting a meaningful name
///     with the folder name when no better information is available.</item>
///   <item><b>FolderName</b> — The immediate parent directory's name,
///     normalized via <see cref="Services.ComicFileProcessor.NormalizeSeriesName"/>.
///     </item>
///   <item><b>UnknownSentinel</b> — Final fallback when nothing else
///     produced a value. Emits <c>"Unknown Series"</c>.</item>
/// </list>
/// </summary>
public interface ISeriesNameResolver
{
    /// <summary>
    /// Resolve the expected series name for the given file + (optional)
    /// existing metadata. Returns the resolution outcome including the
    /// winning step and a human-readable explanation suitable for the
    /// diagnostic endpoint and audit-finding details.
    /// </summary>
    /// <param name="filePath">Absolute path to the comic archive.</param>
    /// <param name="metadata">
    /// The metadata extracted from the file's ComicInfo.xml, if any. When
    /// null the resolver falls back to the folder name.
    /// </param>
    /// <param name="mutateCache">
    /// When true (the default), the resolver may persist side-effects
    /// such as adding the folder name as a user-alias on the matched
    /// cache record. Set false when running from read-only contexts such
    /// as the diagnostic endpoint to avoid mutating shared state during a
    /// preview.
    /// </param>
    Task<SeriesNameResolution> ResolveAsync(
        string filePath,
        ComicMetadata? metadata,
        bool mutateCache = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a single <see cref="ISeriesNameResolver.ResolveAsync"/>
/// invocation. Contains the resolved series title and the explanation of
/// which priority step produced it.
/// </summary>
public sealed class SeriesNameResolution
{
    /// <summary>The resolved series name (suitable for writing into ComicInfo.xml).</summary>
    public string ResolvedSeries { get; init; } = string.Empty;

    /// <summary>Which priority step in <see cref="ISeriesNameResolver"/> produced the value.</summary>
    public SeriesNameResolutionStep WinningStep { get; init; }

    /// <summary>
    /// Title fragments considered as candidate lookup keys (file's
    /// <c>&lt;Series&gt;</c> and the folder-derived name, deduplicated).
    /// </summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Folder-derived series name, before any resolution. Always populated
    /// when a parent directory is present.
    /// </summary>
    public string? FolderSeries { get; init; }

    /// <summary>
    /// Normalized cache key that matched, when <see cref="WinningStep"/> is
    /// <see cref="SeriesNameResolutionStep.UserCanonical"/>,
    /// <see cref="SeriesNameResolutionStep.MatchedCache"/>, or
    /// <see cref="SeriesNameResolutionStep.LocalizedTitleBackref"/>.
    /// </summary>
    public string? MatchedCacheKey { get; init; }

    /// <summary>
    /// Effective language code applied by
    /// <see cref="Services.SeriesDisplayTitleResolver"/> (per-series
    /// preference, falling back to the global default). Null when no
    /// language preference was applied (e.g. user-canonical override).
    /// </summary>
    public string? AppliedLanguage { get; init; }

    /// <summary>
    /// Human-readable, single-sentence explanation suitable for surfacing
    /// in audit-finding details and the diagnostic endpoint.
    /// </summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Discrete priority steps in <see cref="ISeriesNameResolver"/>. The order
/// of enumeration members reflects the resolution order (lower ordinal =
/// higher priority).
/// </summary>
public enum SeriesNameResolutionStep
{
    /// <summary>User-overridden canonical title (IsUserCanonical=true).</summary>
    UserCanonical = 1,

    /// <summary>Cache record from a successful/manual match.</summary>
    MatchedCache = 2,

    /// <summary>Cache record reached via a localized-title back-reference.</summary>
    LocalizedTitleBackref = 3,

    /// <summary>Live external provider lookup (no cached match yet).</summary>
    ExternalLookup = 4,

    /// <summary>File already had a non-empty &lt;Series&gt; value.</summary>
    ExistingMetadata = 5,

    /// <summary>Folder name (parent directory).</summary>
    FolderName = 6,

    /// <summary>"Unknown Series" sentinel — no other source produced a value.</summary>
    UnknownSentinel = 7
}
