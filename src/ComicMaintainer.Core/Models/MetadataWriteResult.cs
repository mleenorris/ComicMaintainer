namespace ComicMaintainer.Core.Models;

/// <summary>
/// What a metadata write did to a comic file.
/// </summary>
public enum MetadataWriteOutcome
{
    /// <summary>Nothing was written; the reason has been logged.</summary>
    Failed,

    /// <summary>The archive's ComicInfo.xml was rewritten in place.</summary>
    Written,

    /// <summary>
    /// The archive already held exactly these values, so it was left untouched.
    /// </summary>
    Unchanged,

    /// <summary>
    /// A CBR/RAR/ZIP archive was written as a new CBZ next to it and the original removed, so the
    /// file now lives at <see cref="MetadataWriteResult.FilePath"/>.
    /// </summary>
    ConvertedToCbz,

    /// <summary>
    /// The archive is not rewritten (a CBR/RAR/ZIP while converting to CBZ is turned off), so the
    /// edit was recorded in the database only.
    /// </summary>
    KeptInDatabase,

    /// <summary>
    /// The archive is not rewritten (a CBR/RAR/ZIP while converting to CBZ is turned off) and
    /// the caller did not ask for the edit to be recorded in the database.
    /// </summary>
    NotWritable
}

/// <summary>
/// The outcome of writing metadata to one comic file and where the file is afterwards.
/// </summary>
/// <param name="Outcome">What was done.</param>
/// <param name="FilePath">
/// The file's path after the write: the path that was written, or the new CBZ's path when the
/// archive was converted.
/// </param>
public sealed record MetadataWriteResult(MetadataWriteOutcome Outcome, string FilePath)
{
    /// <summary>True unless the write failed. A file kept as it is counts as a success.</summary>
    public bool Succeeded => Outcome is not MetadataWriteOutcome.Failed;

    /// <summary>True when the archive on disk was changed (rewritten or converted).</summary>
    public bool ArchiveChanged => Outcome is MetadataWriteOutcome.Written or MetadataWriteOutcome.ConvertedToCbz;

    public static MetadataWriteResult Failed(string filePath) => new(MetadataWriteOutcome.Failed, filePath);
}
