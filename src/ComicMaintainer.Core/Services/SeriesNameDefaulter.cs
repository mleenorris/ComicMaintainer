using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Computes the value of <see cref="SeriesMetadataCacheEntity.SeriesName"/>
/// from the record's "inputs" (canonical title, localized titles, per-series
/// preferred language, global default language).
/// <para>
/// Single write-side helper introduced in PR 1 of 3 of the series-name
/// overhaul. The defaulter is the only code path that should mutate
/// <see cref="SeriesMetadataCacheEntity.SeriesName"/>,
/// <see cref="SeriesMetadataCacheEntity.SeriesNameSource"/> or
/// <see cref="SeriesMetadataCacheEntity.SeriesNameLanguage"/> outside of an
/// explicit user pick (which sets <see cref="SeriesNameSource.UserSelected"/>
/// directly and is sticky).
/// </para>
/// <para>
/// Rules:
/// <list type="number">
///   <item>If <see cref="SeriesMetadataCacheEntity.SeriesNameSource"/> is
///         <see cref="SeriesNameSource.UserSelected"/> and the existing
///         <see cref="SeriesMetadataCacheEntity.SeriesName"/> is non-empty,
///         leave it alone. User picks are sticky.</item>
///   <item>Otherwise compute the default exactly the same way the legacy
///         <see cref="SeriesDisplayTitleResolver"/> chain would, capturing
///         whether the canonical title or a language-matched localized
///         title won, and update the three fields accordingly.</item>
/// </list>
/// </para>
/// </summary>
public static class SeriesNameDefaulter
{
    /// <summary>
    /// Recompute <see cref="SeriesMetadataCacheEntity.SeriesName"/> and
    /// friends from the entity's current inputs. Safe to call on every
    /// mutation; user picks are preserved.
    /// </summary>
    /// <param name="entity">The cache entity being written.</param>
    /// <param name="globalDefaultLanguage">
    /// Snapshot of <c>AppSettings.DefaultPreferredLanguage</c> at the time
    /// of the mutation. May be null/empty.
    /// </param>
    /// <param name="localizedTitles">
    /// The parsed localized-title list for the entity. The caller already
    /// has this in scope at every mutation point (it's derived from
    /// <see cref="SeriesMetadataCacheEntity.LocalizedTitlesJson"/>), so we
    /// take it as a parameter rather than re-parsing here.
    /// </param>
    public static void Recompute(
        SeriesMetadataCacheEntity entity,
        string? globalDefaultLanguage,
        IReadOnlyList<LocalizedTitle> localizedTitles)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Sticky: never overwrite a user pick. PR 2 will add an explicit
        // "reset" path that flips Source back to LanguageDefault before
        // calling Recompute.
        if (entity.SeriesNameSource == SeriesNameSource.UserSelected
            && !string.IsNullOrWhiteSpace(entity.SeriesName))
        {
            return;
        }

        // Legacy override paths still in effect during PR 1. PR 3 removes
        // these branches once the legacy override fields are dropped.
        if (entity.IsUserCanonical && !string.IsNullOrWhiteSpace(entity.CanonicalTitle))
        {
            entity.SeriesName = entity.CanonicalTitle;
            entity.SeriesNameSource = SeriesNameSource.UserSelected;
            entity.SeriesNameLanguage = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entity.PinnedLocalizedTitle))
        {
            entity.SeriesName = entity.PinnedLocalizedTitle;
            entity.SeriesNameSource = SeriesNameSource.UserSelected;
            entity.SeriesNameLanguage = null;
            return;
        }

        var preferred = SeriesLanguagePreference.Normalize(entity.PreferredLanguage)
                        ?? SeriesLanguagePreference.Normalize(globalDefaultLanguage);

        if (!string.IsNullOrWhiteSpace(preferred) && localizedTitles is { Count: > 0 })
        {
            foreach (var localized in localizedTitles)
            {
                if (string.IsNullOrWhiteSpace(localized?.Title))
                {
                    continue;
                }
                if (SeriesLanguagePreference.Matches(preferred, localized!.Language))
                {
                    entity.SeriesName = localized.Title;
                    entity.SeriesNameSource = SeriesNameSource.LanguageDefault;
                    entity.SeriesNameLanguage = localized.Language;
                    return;
                }
            }
        }

        // Final fallback: canonical title, or — for a brand-new unmatched
        // row whose canonical title also happens to be empty — the
        // normalized key. PR 2 will distinguish "FolderName" (no match yet)
        // from "LanguageDefault" (matched but no language hit) more
        // precisely; for PR 1 we use LanguageDefault whenever the record
        // has a non-empty canonical, FolderName otherwise.
        if (!string.IsNullOrWhiteSpace(entity.CanonicalTitle))
        {
            entity.SeriesName = entity.CanonicalTitle;
            entity.SeriesNameSource = SeriesNameSource.LanguageDefault;
            entity.SeriesNameLanguage = null;
        }
        else
        {
            entity.SeriesName = entity.NormalizedKey;
            entity.SeriesNameSource = SeriesNameSource.FolderName;
            entity.SeriesNameLanguage = null;
        }
    }
}
