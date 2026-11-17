using System.Globalization;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    public Task<ProcessingPreviewItem> PreviewProcessingAsync(string filePath, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(filePath, token => PreviewFileCoreAsync(filePath, token), cancellationToken);

    /// <summary>
    /// The dry run of <see cref="ProcessFileCoreAsync"/> with <c>forceReprocess</c> on. It takes
    /// the same decisions (<see cref="PlanNormalizeAsync"/>, <see cref="RecoverMarkedChapter"/>,
    /// <see cref="ComposeComicInfoUpdateAsync"/>, <see cref="DecideRename"/>) and stops before
    /// each write: nothing here writes the archive, the database, the history or the series cache.
    /// It assumes every write would succeed, so a disk or permission failure during the real run
    /// is not foreseen.
    /// </summary>
    private async Task<ProcessingPreviewItem> PreviewFileCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolveAllowedPath(filePath, out var resolvedPath))
            {
                return PreviewFailure(filePath, "File is outside the allowed directories");
            }

            if (new FileInfo(filePath).LinkTarget is not null)
            {
                return PreviewFailure(filePath, "File symlinks are not processed");
            }

            if (!File.Exists(resolvedPath))
            {
                return PreviewFailure(filePath, "File not found");
            }

            if (!IsComicArchive(resolvedPath))
            {
                return PreviewFailure(filePath, "File is not a comic archive");
            }

            // One settings snapshot, so both phases see the same switches.
            var settings = _settings;
            var metadataRead = await ReadMetadataAtResolvedPathAsync(resolvedPath, filePath, cancellationToken);
            if (metadataRead.Metadata is not { } metadata)
            {
                return PreviewUnreadable(filePath, metadataRead.FailureReason, settings.WatcherEnableNormalize, settings.WatcherEnableRename);
            }

            var originalSeries = metadata.Series;

            // The values the rename works from: the normalized ones while normalize is on, as in
            // ProcessFileCoreAsync. A copy, so the plan's metadata is never changed here.
            var working = metadata.Clone();
            var fields = ComicMetadataFieldFlags.None;
            ProviderComicInfoFields? providerFill = null;
            if (settings.WatcherEnableNormalize)
            {
                var plan = await PlanNormalizeAsync(metadata, filePath, mutateCache: false, cancellationToken);
                working = plan.Normalized.Clone();
                fields = plan.Fields;
                providerFill = plan.ProviderFill;
            }

            var renaming = settings.WatcherEnableRename && !string.IsNullOrEmpty(working.Series);

            // The chapter recovered from the file name before the rename is written too while
            // normalize is on (EnsureChapterMetadataPreservedAsync); both writes end in this one.
            if (renaming && RecoverMarkedChapter(filePath, working) && settings.WatcherEnableNormalize)
            {
                fields |= ComicMetadataFieldFlags.Issue | ComicMetadataFieldFlags.Title;
            }

            var tagChanges = new List<ComicInfoFieldChange>();
            string? warning = null;

            // Where the file is once its tags are written: a CBR, RAR or ZIP becomes a CBZ.
            var afterWritePath = resolvedPath;
            if (fields.HasFlag(ComicMetadataFieldFlags.VolumeYear))
            {
                // Kept in the database only (see UpdateMetadataCoreAsync).
                tagChanges.Add(new ComicInfoFieldChange(
                    nameof(ComicMetadata.VolumeYear),
                    metadata.VolumeYear?.ToString(CultureInfo.InvariantCulture),
                    working.VolumeYear?.ToString(CultureInfo.InvariantCulture)));
                fields &= ~ComicMetadataFieldFlags.VolumeYear;
            }

            if (fields != ComicMetadataFieldFlags.None || providerFill is not null)
            {
                var convert = !ComicFileExtensions.IsWritableArchive(resolvedPath);
                var canConvert = convert && CanConvertToCbz(filePath);
                var update = await ComposeComicInfoUpdateAsync(
                    filePath, resolvedPath, working, fields, canConvert, userEdit: false, providerFill, cancellationToken);
                if (!ChangesNothing(update.Existing, update.Updated))
                {
                    tagChanges.AddRange(ComicInfoXml.Diff(update.Existing, update.Updated));
                    if (convert && !canConvert)
                    {
                        warning = NormalizeNotWritableMessage;
                    }
                    else if (convert)
                    {
                        afterWritePath = Path.Join(
                            Path.GetDirectoryName(resolvedPath),
                            Path.GetFileNameWithoutExtension(resolvedPath) + ".cbz");
                        if (Path.Exists(afterWritePath))
                        {
                            // ConvertToCbzAsync refuses to replace it, so normalize fails.
                            return PreviewFailure(
                                filePath,
                                $"Normalize failed: Cannot convert the archive to CBZ: {Path.GetFileName(afterWritePath)} already exists next to it.",
                                working.Series,
                                tagChanges);
                        }
                    }
                }
            }

            var newFilePath = afterWritePath == resolvedPath ? null : LogicalSibling(filePath, afterWritePath);
            var clash = ProcessingPreviewClash.None;
            string? clashWith = null;
            string? error = null;
            if (renaming)
            {
                var (decision, target) = DecideRename(working, afterWritePath, originalSeries);
                switch (decision)
                {
                    case RenameDecision.NameFailed:
                        error = $"Rename failed: {FilenameGenerationFailedMessage}";
                        break;
                    case RenameDecision.TargetTaken:
                        newFilePath = LogicalSibling(filePath, target!);
                        clash = ProcessingPreviewClash.ExistingFile;
                        clashWith = newFilePath;
                        break;
                    case RenameDecision.Rename:
                        newFilePath = LogicalSibling(filePath, target!);
                        break;
                }
            }
            else if (settings.WatcherEnableRename)
            {
                error = NoSeriesMessage(filePath, "Rename skipped: no metadata or series information");
            }

            return new ProcessingPreviewItem
            {
                FilePath = filePath,
                Series = SeriesOrFolder(filePath, working.Series),
                NewFilePath = newFilePath,
                TagChanges = tagChanges,
                Clash = clash,
                ClashWith = clashWith,
                Warning = warning,
                Error = error
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not preview processing of {FilePath}", LoggingHelper.SanitizePathForLog(filePath));
            return PreviewFailure(filePath, ex.Message);
        }
    }

    /// <summary>
    /// The preview of a file whose archive gave no metadata: processing fails for the reason the
    /// read gave, or for the phase that needed it, as <see cref="ProcessFileCoreAsync"/> reports it.
    /// With both phases turned off nothing happens to it.
    /// </summary>
    private ProcessingPreviewItem PreviewUnreadable(string filePath, string? failureReason, bool normalize, bool rename)
    {
        if (!normalize && !rename)
        {
            return new ProcessingPreviewItem { FilePath = filePath, Series = SeriesOrFolder(filePath, null) };
        }

        var reasons = new List<string>();
        if (normalize)
        {
            reasons.Add("Normalize failed: the archive could not be read");
        }

        if (rename)
        {
            reasons.Add(NoSeriesMessage(filePath, "Rename skipped: no metadata or series information"));
        }

        return PreviewFailure(filePath, failureReason ?? string.Join("; ", reasons));
    }

    /// <summary>The preview of a file processing would fail for.</summary>
    private static ProcessingPreviewItem PreviewFailure(
        string filePath,
        string error,
        string? series = null,
        IReadOnlyList<ComicInfoFieldChange>? tagChanges = null) => new()
    {
        FilePath = filePath,
        Series = SeriesOrFolder(filePath, series),
        TagChanges = tagChanges ?? [],
        Error = error
    };

    /// <summary>The series a preview files <paramref name="filePath"/> under: <paramref name="series"/>, else its folder's name.</summary>
    private static string? SeriesOrFolder(string filePath, string? series) =>
        string.IsNullOrWhiteSpace(series) ? Path.GetFileName(Path.GetDirectoryName(filePath)) : series;

    /// <summary>
    /// The library path of <paramref name="physicalPath"/>, a file in the same folder as
    /// <paramref name="filePath"/>: processing never moves a file to another folder, and the
    /// caller's path may go through a linked folder that the physical path does not.
    /// </summary>
    private static string LogicalSibling(string filePath, string physicalPath) =>
        Path.Join(Path.GetDirectoryName(filePath), Path.GetFileName(physicalPath));
}
