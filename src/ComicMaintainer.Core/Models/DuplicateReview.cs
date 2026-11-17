namespace ComicMaintainer.Core.Models;

/// <summary>
/// Whether the name processing would give a file is taken by another file.
/// </summary>
public enum DuplicateClashStatus
{
    /// <summary>Another file has the name processing would give this one.</summary>
    Clashes,

    /// <summary>No other file has that name, so processing can give it to this one.</summary>
    Free,

    /// <summary>The name could not be worked out, for example the archive cannot be read.</summary>
    Unknown
}

/// <summary>
/// The name processing would give a file now, and the file in the way of it.
/// </summary>
/// <param name="Status">Whether the name is taken.</param>
/// <param name="TargetPath">The path processing would give the file; null when <see cref="DuplicateClashStatus.Unknown"/>.</param>
/// <param name="ClashingPath">The other file that has that name; set only when <see cref="DuplicateClashStatus.Clashes"/>.</param>
/// <param name="Reason">Why the name could not be worked out; set only when <see cref="DuplicateClashStatus.Unknown"/>.</param>
public sealed record DuplicateClash(
    DuplicateClashStatus Status,
    string? TargetPath,
    string? ClashingPath = null,
    string? Reason = null);

/// <summary>
/// The outcome of swapping a duplicate with the file that has its name.
/// </summary>
/// <param name="Succeeded">True when the duplicate now has the name and the other file is the duplicate.</param>
/// <param name="ProperPath">Where the former duplicate is now.</param>
/// <param name="DuplicatePath">Where the other file, now flagged as the duplicate, is now.</param>
/// <param name="Error">Why the swap did not happen.</param>
/// <param name="Conflict">
/// True when nothing was changed because the files are no longer as the request expected: the
/// file is not flagged as a duplicate, or its name is no longer taken.
/// </param>
public sealed record DuplicateSwapResult(
    bool Succeeded,
    string? ProperPath = null,
    string? DuplicatePath = null,
    string? Error = null,
    bool Conflict = false);

/// <summary>
/// How a duplicate re-check job records each file: a file cleared of its flag succeeds, a file
/// that still clashes fails with <see cref="StillDuplicateError"/>, and any other failure carries
/// its own reason. The job keeps only a success flag and an error per file, so the summary is
/// read back from these errors, which also survive a restart with the job.
/// </summary>
public static class DuplicateRecheck
{
    /// <summary>The job's operation name, which tells a re-check job from the others.</summary>
    public const string OperationName = "RecheckDuplicatesAsync";

    private const string StillDuplicatePrefix = "Still a duplicate of ";

    /// <summary>The error recorded for a file whose processed name <paramref name="clashingPath"/> still has.</summary>
    public static string StillDuplicateError(string clashingPath) => StillDuplicatePrefix + clashingPath;

    /// <summary>The file a re-checked file still clashes with, or null when the error says something else.</summary>
    public static string? ClashingPathOf(string? error) =>
        error is not null && error.StartsWith(StillDuplicatePrefix, StringComparison.Ordinal)
            ? error[StillDuplicatePrefix.Length..]
            : null;
}
