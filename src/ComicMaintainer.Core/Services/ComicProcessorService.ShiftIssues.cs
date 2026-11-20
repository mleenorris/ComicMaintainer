using System.Collections.Concurrent;
using System.Globalization;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>Largest shift accepted: a number off by more than this is not a miscount.</summary>
    public const int MaxIssueNumberShift = 10000;

    /// <inheritdoc />
    public Task<Guid> ShiftIssueNumbersAsync(IEnumerable<string> filePaths, int offset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (offset == 0 || Math.Abs(offset) > MaxIssueNumberShift)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The shift must be between -{MaxIssueNumberShift} and {MaxIssueNumberShift}, and not 0.");
        }

        // The numbers are written in parallel, then the files are renamed one at a time (see
        // RenameShiftedAsync): renamed in parallel, Chapter 0005 could take the name Chapter 0003
        // before Chapter 0003 had moved to Chapter 0001, and be left as its duplicate.
        var shifted = new ConcurrentBag<(string FilePath, decimal Issue)>();
        var definition = new BatchJobDefinition(
            "ShiftIssueNumbersAsync",
            "shifting the issue number of",
            (filePath, token) => RunExclusiveAsync(filePath, itemToken => ShiftIssueNumberAsync(filePath, offset, shifted, itemToken), token),
            "Shift issue number failed",
            PostLoopAsync: (report, token) => RenameShiftedAsync(shifted, offset, report, token));
        return Task.FromResult(_jobRegistry.StartJob(definition, filePaths, cancellationToken));
    }

    /// <summary>
    /// The issue number <paramref name="issue"/> moved by <paramref name="offset"/>, or null when
    /// it is not a plain number (a tie-in's "1.MU", "Annual 1") or would fall below 0.
    /// </summary>
    internal static decimal? ShiftedIssue(string? issue, int offset)
    {
        if (string.IsNullOrWhiteSpace(issue)
            || !decimal.TryParse(issue.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var shifted = number + offset;
        return shifted < 0 ? null : shifted;
    }

    /// <summary>An issue number as it is written: "1", "12.5", never "1.0".</summary>
    internal static string FormatIssue(decimal issue) => issue.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the file's issue number moved by <paramref name="offset"/>, locked as the user's
    /// edit so normalize keeps it, with a generated "Chapter N" title following it. The rename
    /// comes after every number is written.
    /// </summary>
    private async Task<BatchItemResult> ShiftIssueNumberAsync(
        string filePath,
        int offset,
        ConcurrentBag<(string FilePath, decimal Issue)> shifted,
        CancellationToken cancellationToken)
    {
        var current = await GetMetadataAsync(filePath, cancellationToken);
        if (ShiftedIssue(current?.Issue, offset) is not { } issue)
        {
            return new BatchItemResult(false, string.IsNullOrWhiteSpace(current?.Issue)
                ? "The file has no issue number"
                : $"Issue number \"{current.Issue.Trim()}\" cannot be shifted by {offset.ToString(CultureInfo.InvariantCulture)}");
        }

        var update = new ComicMetadata { Issue = FormatIssue(issue) };
        var fields = ComicMetadataFieldFlags.Issue;
        if (!string.IsNullOrWhiteSpace(current!.Title) && GeneratedChapterTitlePattern.IsMatch(current.Title))
        {
            update.Title = CreateNormalizedTitle(update.Issue);
            fields |= ComicMetadataFieldFlags.Title;
        }

        var write = await UpdateMetadataCoreAsync(filePath, update, fields, cancellationToken, userEdit: true);
        if (!write.Succeeded)
        {
            return new BatchItemResult(false, write.FailureReason);
        }

        try
        {
            // The number the user chose: normalize and the tagger corrections leave it alone.
            await _fileStore.RecordUserMetadataEditAsync(write.FilePath, update, ComicMetadataFieldFlags.Issue, pendingWrite: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Shifted the issue number of {FilePath} but could not lock it", LoggingHelper.SanitizePathForLog(write.FilePath));
        }

        shifted.Add((write.FilePath, issue));
        return true;
    }

    /// <summary>
    /// Renames the shifted files one at a time, each into a name the one before it freed: the
    /// lowest new number first when the numbers went down, the highest first when they went up.
    /// A file that cannot be renamed is reported; its new number stays written.
    /// </summary>
    private async Task RenameShiftedAsync(
        IEnumerable<(string FilePath, decimal Issue)> shifted,
        int offset,
        IBatchJobReport report,
        CancellationToken cancellationToken)
    {
        var ordered = offset < 0
            ? shifted.OrderBy(file => file.Issue).ToList()
            : shifted.OrderByDescending(file => file.Issue).ToList();
        foreach (var (filePath, _) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var renamed = await RunExclusiveAsync(filePath, token => RenameFileCoreAsync(filePath, true, token), cancellationToken);
            if (!renamed.Success)
            {
                await report.ReportProblemAsync(filePath, renamed.Error ?? "The file could not be renamed after its issue number was shifted");
            }
        }
    }
}
