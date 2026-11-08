using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled job that writes DB-authoritative per-file metadata back to ComicInfo.xml.
/// </summary>
public class MetadataBackfillJobHandler : IScheduledJobHandler
{
    public const string Key = "metadata-backfill";

    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly ISeriesNameResolver _seriesNameResolver;
    private readonly ILogger<MetadataBackfillJobHandler> _logger;

    public MetadataBackfillJobHandler(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        ISeriesNameResolver seriesNameResolver,
        ILogger<MetadataBackfillJobHandler> logger)
    {
        _fileStore = fileStore;
        _processor = processor;
        _seriesNameResolver = seriesNameResolver;
        _logger = logger;
    }

    public string JobKey => Key;
    public string DisplayName => "Metadata Backfill";
    public string Description => "Writes DB-authoritative per-file metadata changes back into each archive's ComicInfo.xml.";
    public ScheduledJobDefaults Defaults => new(Enabled: false, IntervalMinutes: 15, OptionsJson: """{"maxBatch":200}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        var options = ParseOptions(optionsJson);
        var processed = 0;
        var failed = 0;
        var notWritable = 0;

        // Pages through the queue in file-path order until it is drained, so a run that follows
        // edits made during an earlier run writes all of them back, not just the first batch.
        // Each page starts after the last path of the previous one, so every read is bounded by
        // maxBatch and a file that failed, or that is still queued after a write, is not read
        // again in the same run.
        string? afterFilePath = null;
        while (true)
        {
            var batch = await _fileStore.GetFilesNeedingBackfillAsync(options.MaxBatch, afterFilePath, cancellationToken);

            foreach (var file in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (await BackfillFileAsync(file, cancellationToken))
                {
                    case BackfillOutcome.Written:
                        processed++;
                        break;
                    case BackfillOutcome.NotWritable:
                        notWritable++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
            if (options.MaxBatch <= 0 || batch.Count < options.MaxBatch)
            {
                break;
            }

            afterFilePath = batch[^1].FilePath;
        }

        // A CBR/RAR/ZIP that is not converted to CBZ is not written; it stays queued, so its
        // edits are written once the setting is turned back on.
        var keptInDatabase = notWritable > 0
            ? $", {notWritable} kept in the database (CBR/RAR/ZIP not converted to CBZ)"
            : string.Empty;
        return $"Backfill: {processed} files updated, {failed} failed{keptInDatabase} (queue size was {processed + failed + notWritable}).";
    }

    private enum BackfillOutcome { Written, Failed, NotWritable }

    private async Task<BackfillOutcome> BackfillFileAsync(ComicFile file, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = BuildMetadata(file);
            var flags = (ComicMetadataFieldFlags)(file.Metadata?.UserLockedFieldsMask ?? 0L);

            // Every stored value is written, and so is every locked field, including one the
            // user cleared.
            var fields = ComicMetadataFields.Supplied(metadata) | flags;
            if (!flags.HasFlag(ComicMetadataFieldFlags.Series))
            {
                var resolution = await _seriesNameResolver.ResolveAsync(
                    file.FilePath,
                    metadata,
                    mutateCache: false,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolution.ResolvedSeries))
                {
                    metadata.Series = resolution.ResolvedSeries;
                    fields |= ComicMetadataFieldFlags.Series;
                }
            }

            // Writes the archive only: an edit is never recorded in the database again, which
            // would queue the file once more.
            var result = await _processor.WriteMetadataAsync(file.FilePath, metadata, fields, cancellationToken);
            if (result.Outcome == MetadataWriteOutcome.NotWritable)
            {
                return BackfillOutcome.NotWritable;
            }

            if (!result.Succeeded)
            {
                return BackfillOutcome.Failed;
            }

            // A converted CBR/RAR/ZIP is now a CBZ at a new path.
            await _fileStore.MarkFileBackfilledAsync(result.FilePath, file.MetadataVersion, cancellationToken);
            return BackfillOutcome.Written;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Metadata backfill failed for {FilePath}", LoggingHelper.SanitizePathForLog(file.FilePath));
            return BackfillOutcome.Failed;
        }
    }

    private static ComicMetadata BuildMetadata(ComicFile file)
    {
        return file.Metadata?.Clone() ?? new ComicMetadata();
    }

    private static MetadataBackfillOptions ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return new MetadataBackfillOptions();
        try
        {
            return JsonSerializer.Deserialize<MetadataBackfillOptions>(
                       optionsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new MetadataBackfillOptions();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new MetadataBackfillOptions();
        }
    }

    private sealed class MetadataBackfillOptions
    {
        public int MaxBatch { get; set; } = 200;
    }
}
