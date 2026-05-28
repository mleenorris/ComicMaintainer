namespace ComicMaintainer.Core.Models;

/// <summary>
/// How <see cref="SeriesMetadataCacheRecord.SeriesName"/> was determined.
/// Drives whether <see cref="Services.SeriesNameDefaulter"/> is allowed to
/// recompute the value when inputs change.
/// <para>
/// PR 1 of 3 of the series-name overhaul: introduced alongside the new
/// <c>SeriesName</c> column. PR 2 will wire user actions to set
/// <see cref="UserSelected"/>; PR 3 will drop the legacy override fields
/// (<c>PinnedLocalizedTitle</c>, per-series <c>PreferredLanguage</c>,
/// <c>IsUserCanonical</c>-as-display-flag).
/// </para>
/// </summary>
public enum SeriesNameSource
{
    /// <summary>
    /// Default for a brand-new, unmatched series — <see cref="SeriesMetadataCacheRecord.SeriesName"/>
    /// equals the folder name. Recomputed on every input change until the
    /// series is matched or the user picks an explicit name.
    /// </summary>
    FolderName = 0,

    /// <summary>
    /// Derived from the localized-title list using the per-series or global
    /// preferred language; falls back to <c>CanonicalTitle</c> when no
    /// language match exists. Recomputed on every input change.
    /// </summary>
    LanguageDefault = 1,

    /// <summary>
    /// User explicitly picked this name (a localized title, the canonical
    /// title, or one of their own aliases). Sticky: input changes never
    /// overwrite a <see cref="UserSelected"/> name; only an explicit
    /// "reset" or another user pick can change it.
    /// </summary>
    UserSelected = 2,
}
