using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>
    /// What may be filled into the ComicInfo of <paramref name="filePath"/> from the provider
    /// match of <paramref name="series"/>, or null when there is nothing to fill: the setting is
    /// off, the series has no provider match, or the match reports nothing usable.
    /// </summary>
    /// <remarks>
    /// A Summary the user has locked is left out, even an empty one: clearing it was the
    /// user's decision. Best effort; a failure here must never fail normalizing the file.
    /// </remarks>
    private async Task<ProviderComicInfoFields?> ResolveProviderFillAsync(
        string filePath,
        string? series,
        CancellationToken cancellationToken)
    {
        if (!_settings.FillComicInfoFromProviders || string.IsNullOrWhiteSpace(series))
        {
            return null;
        }

        try
        {
            var record = await _seriesMetadataCache.GetByTitleAsync(series, cancellationToken);

            // A lookup that found nothing can be a stale sibling, keyed by this name, of the
            // record holding the series' match. The library shows that match; so do the files.
            if (record is not null && LookupFoundNothing(record))
            {
                record = await _seriesMetadataCache.GetAuthoritativeByTitleAsync(series, cancellationToken);
            }

            if (record is null || !IsProviderMatch(record))
            {
                return null;
            }

            var fill = ProviderComicInfoFields.From(record);
            if (fill.IsEmpty)
            {
                return null;
            }

            var tracked = await _fileStore.GetFileAsync(filePath, cancellationToken);
            var locked = (ComicMetadataFieldFlags)(tracked?.Metadata?.UserLockedFieldsMask ?? 0L);
            return locked.HasFlag(ComicMetadataFieldFlags.Summary) ? fill with { Summary = null } : fill;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(
                ex,
                "Could not read the provider details for {FilePath}; its ComicInfo fields are left as they are",
                LoggingHelper.SanitizePathForLog(filePath));
            return null;
        }
    }

    /// <summary>
    /// True when the record holds a provider's match. A record whose last lookup found nothing
    /// may still carry an earlier match's details, which are no longer known to be right.
    /// </summary>
    private static bool IsProviderMatch(SeriesMetadataCacheRecord record) =>
        string.Equals(record.LookupStatus, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(record.LookupStatus, "manual_match", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the record says only that a lookup came back empty. Metadata the user cleared
    /// or manages is a decision about the series, not a missing answer.
    /// </summary>
    private static bool LookupFoundNothing(SeriesMetadataCacheRecord record) =>
        string.IsNullOrEmpty(record.LookupStatus)
        || string.Equals(record.LookupStatus, "not_found", StringComparison.OrdinalIgnoreCase)
        || string.Equals(record.LookupStatus, "error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fills the empty ComicInfo fields of a file normalize has nothing to change in, so a file
    /// that was already tagged still gets the provider's details the next time it is processed.
    /// </summary>
    /// <returns>
    /// The result of the write, or null when nothing was attempted. A failed write is left for
    /// the next run: the file is correctly normalized, and missing details do not change that.
    /// </returns>
    private async Task<MetadataWriteResult?> FillFromProviderAsync(
        string filePath,
        ComicMetadata metadata,
        CancellationToken cancellationToken)
    {
        var fill = await ResolveProviderFillAsync(filePath, metadata.Series, cancellationToken);
        return fill is null
            ? null
            : await UpdateMetadataCoreAsync(filePath, metadata, ComicMetadataFieldFlags.None, cancellationToken, providerFill: fill);
    }
}
