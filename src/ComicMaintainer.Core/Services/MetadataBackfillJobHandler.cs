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
        var batch = await _fileStore.GetFilesNeedingBackfillAsync(options.MaxBatch, cancellationToken);
        var processed = 0;
        var failed = 0;

        foreach (var file in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

                var success = await _processor.UpdateMetadataAsync(file.FilePath, metadata, cancellationToken);
                if (success)
                {
                    await _fileStore.MarkFileBackfilledAsync(file.FilePath, file.MetadataVersion, cancellationToken);
                    processed++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Metadata backfill failed for {FilePath}", LoggingHelper.SanitizePathForLog(file.FilePath));
            }
        }

        return $"Backfill: {processed} files updated, {failed} failed (queue size was {batch.Count}).";
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
        catch
        {
            return new MetadataBackfillOptions();
        }
    }

    private sealed class MetadataBackfillOptions
    {
        public int MaxBatch { get; set; } = 200;
    }
}
