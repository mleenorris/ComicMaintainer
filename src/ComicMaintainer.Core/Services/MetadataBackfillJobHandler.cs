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
                if (await BackfillFileAsync(file, cancellationToken))
                {
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
            if (options.MaxBatch <= 0 || batch.Count < options.MaxBatch)
            {
                break;
            }

            afterFilePath = batch[^1].FilePath;
        }

        return $"Backfill: {processed} files updated, {failed} failed (queue size was {processed + failed}).";
    }

    private async Task<bool> BackfillFileAsync(ComicFile file, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = BuildMetadata(file);
            var flags = (ComicMetadataFieldFlags)(file.Metadata?.UserLockedFieldsMask ?? 0L);
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
                }
            }

            if (!await _processor.UpdateMetadataAsync(file.FilePath, metadata, cancellationToken))
            {
                return false;
            }

            await _fileStore.MarkFileBackfilledAsync(file.FilePath, file.MetadataVersion, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Metadata backfill failed for {FilePath}", LoggingHelper.SanitizePathForLog(file.FilePath));
            return false;
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
