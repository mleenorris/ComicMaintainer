using System.Globalization;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    /// <summary>Largest shift accepted: a number off by more than this is not a miscount.</summary>
    public const int MaxIssueNumberShift = 10000;

    /// <summary>Most problems a refused shift lists.</summary>
    private const int MaxShiftProblems = 50;

    /// <inheritdoc />
    public async Task<IssueShiftPlan> PlanIssueShiftAsync(IEnumerable<string> filePaths, int offset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (offset == 0 || Math.Abs(offset) > MaxIssueNumberShift)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The shift must be between -{MaxIssueNumberShift} and {MaxIssueNumberShift}, and not 0.");
        }

        var selected = filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToList();
        var problems = new List<string>();
        var moves = new List<(string Path, string Group, decimal To)>();
        foreach (var path in selected)
        {
            var name = Path.GetFileName(path);
            var read = await ReadMetadataAsync(path, cancellationToken);
            if (read.Metadata is not { } metadata)
            {
                problems.Add($"{name}: its tags could not be read{(read.FailureReason is { } reason ? $" ({reason})" : string.Empty)}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(metadata.Issue))
            {
                problems.Add($"{name}: it has no issue number");
            }
            else if (ShiftedIssue(metadata.Issue, offset) is { } to)
            {
                moves.Add((path, ShiftGroup(path, metadata), to));
            }
            else
            {
                problems.Add(PlainIssue(metadata.Issue) is null
                    ? $"{name}: issue number \"{metadata.Issue.Trim()}\" is not a plain number"
                    : $"{name}: #{metadata.Issue.Trim()} would fall below 0");
            }
        }

        // Two of the files ending on one number of one volume would take one name.
        foreach (var clash in moves.GroupBy(move => (move.Group, move.To)).Where(group => group.Count() > 1))
        {
            problems.Add($"{string.Join(", ", clash.Select(move => Path.GetFileName(move.Path)))} would all be #{FormatIssue(clash.Key.To)}");
        }

        // A file left out keeps its number, and so its name: none of the files may move onto it.
        var targets = moves.ToLookup(move => (move.Group, move.To));
        var directories = moves.Select(move => Path.GetDirectoryName(move.Path) ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var chosen = selected.ToHashSet(StringComparer.Ordinal);
        foreach (var other in await _fileStore.GetAllFilesAsync(cancellationToken))
        {
            if (chosen.Contains(other.FilePath)
                || other.IsDuplicate
                || other.Metadata is not { } held
                || !directories.Contains(Path.GetDirectoryName(other.FilePath) ?? string.Empty)
                || PlainIssue(held.Issue) is not { } number)
            {
                continue;
            }

            foreach (var move in targets[(ShiftGroup(other.FilePath, held), number)])
            {
                problems.Add($"{Path.GetFileName(move.Path)}: #{FormatIssue(move.To)} is already {Path.GetFileName(other.FilePath)}, which is not being shifted");
            }
        }

        var ordered = (offset < 0 ? moves.OrderBy(move => move.To) : moves.OrderByDescending(move => move.To))
            .Select(move => move.Path)
            .ToList();
        return new IssueShiftPlan(ordered, problems.Take(MaxShiftProblems).ToList());
    }

    /// <inheritdoc />
    public async Task<Guid> ShiftIssueNumbersAsync(IEnumerable<string> filePaths, int offset, CancellationToken cancellationToken = default)
    {
        var plan = await PlanIssueShiftAsync(filePaths, offset, cancellationToken);
        if (!plan.CanShift)
        {
            throw new IssueShiftRefusedException(plan.Problems);
        }

        // One file at a time, in the plan's order: each takes a name the one before it freed.
        // Its number and its name change together under its lock, so a cancelled job leaves
        // every file it reached shifted and renamed, and the rest untouched.
        var run = new IssueShiftRun(offset);
        var definition = new BatchJobDefinition(
            "ShiftIssueNumbersAsync",
            "shifting the issue number of",
            (filePath, token) => RunExclusiveAsync(filePath, itemToken => ShiftIssueNumberAsync(filePath, run, itemToken), token),
            "Shift issue number failed",
            Sequential: true);
        return _jobRegistry.StartJob(definition, plan.OrderedFiles, cancellationToken);
    }

    /// <summary>The state one shift job's files share.</summary>
    internal sealed class IssueShiftRun(int offset)
    {
        public int Offset { get; } = offset;

        /// <summary>
        /// Set once a file could not be shifted: a later file may be due to take its name,
        /// so the rest are skipped rather than left as its duplicates.
        /// </summary>
        public volatile bool Stopped;
    }

    /// <summary>
    /// The issue number <paramref name="issue"/> moved by <paramref name="offset"/>, or null when
    /// it is not a plain number (a tie-in's "1.MU", "Annual 1") or would fall below 0.
    /// </summary>
    internal static decimal? ShiftedIssue(string? issue, int offset)
    {
        var shifted = PlainIssue(issue) + offset;
        return shifted < 0 ? null : shifted;
    }

    /// <summary>A plain issue number ("003", "12.5"), or null for anything else.</summary>
    private static decimal? PlainIssue(string? issue) =>
        !string.IsNullOrWhiteSpace(issue)
        && decimal.TryParse(issue.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>An issue number as it is written: "1", "12.5", never "1.0".</summary>
    internal static string FormatIssue(decimal issue) => issue.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>
    /// Files whose same issue number gives the same name: in one folder, of one volume and kind
    /// (an Annual #1 and issue #1 are named apart).
    /// </summary>
    private static string ShiftGroup(string filePath, ComicMetadata metadata) => string.Join(
        '|',
        Path.GetDirectoryName(filePath) ?? string.Empty,
        metadata.Volume?.Trim().ToUpperInvariant() ?? string.Empty,
        metadata.Format?.Trim().ToUpperInvariant() ?? string.Empty,
        metadata.VolumeYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    /// <summary>
    /// Shifts one file: writes its new number, locked as the user's edit, with a generated
    /// "Chapter N" title following it (and the series a file without ComicInfo.xml was named
    /// by), then renames it to match unless renaming is off.
    /// </summary>
    internal async Task<BatchItemResult> ShiftIssueNumberAsync(string filePath, IssueShiftRun run, CancellationToken cancellationToken)
    {
        if (run.Stopped)
        {
            return new BatchItemResult(false, "Skipped: an issue before it could not be shifted, so its new name may still be taken");
        }

        var read = await ReadMetadataAsync(filePath, cancellationToken);
        if (read.Metadata is not { } current || ShiftedIssue(current.Issue, run.Offset) is not { } issue)
        {
            run.Stopped = true;
            return new BatchItemResult(false, read.Metadata is null
                ? $"Its tags could not be read{(read.FailureReason is { } reason ? $" ({reason})" : string.Empty)}"
                : $"Issue number \"{read.Metadata.Issue?.Trim()}\" can no longer be shifted by {run.Offset.ToString(CultureInfo.InvariantCulture)}");
        }

        var update = new ComicMetadata { Issue = FormatIssue(issue) };
        var fields = ComicMetadataFieldFlags.Issue;
        if (!string.IsNullOrWhiteSpace(current.Title) && GeneratedChapterTitlePattern.IsMatch(current.Title))
        {
            update.Title = CreateNormalizedTitle(update.Issue);
            fields |= ComicMetadataFieldFlags.Title;
        }

        // A ComicInfo.xml written into an archive that had none would otherwise hold only the
        // number, and the rename would find no series.
        if (!read.FromComicInfo && !string.IsNullOrWhiteSpace(current.Series))
        {
            update.Series = current.Series;
            fields |= ComicMetadataFieldFlags.Series;
        }

        var write = await UpdateMetadataCoreAsync(filePath, update, fields, cancellationToken, userEdit: true);
        if (!write.Succeeded)
        {
            run.Stopped = true;
            return new BatchItemResult(false, write.FailureReason ?? "The new issue number could not be written");
        }

        try
        {
            // The number the user chose: normalize and the tagger corrections leave it alone.
            await _fileStore.RecordUserMetadataEditAsync(write.FilePath, update, ComicMetadataFieldFlags.Issue, pendingWrite: false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Shifted the issue number of {FilePath} but could not lock it", LoggingHelper.SanitizePathForLog(write.FilePath));
        }

        if (!_settings.WatcherEnableRename)
        {
            return true;
        }

        // Not cancelled once the number is written: the name follows it.
        var renamed = await RenameFileCoreAsync(write.FilePath, true, CancellationToken.None);
        if (!renamed.Success)
        {
            run.Stopped = true;
            return new BatchItemResult(false, $"The issue number became #{update.Issue}, but the file could not be renamed: {renamed.Error ?? "unknown reason"}");
        }

        return true;
    }
}
