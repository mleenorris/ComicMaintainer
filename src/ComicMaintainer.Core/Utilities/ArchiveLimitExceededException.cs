namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Thrown when an archive, or one of its entries, is larger than the limits in
/// <see cref="ArchiveEntryReader"/> allow: too many entries, an entry that would
/// expand past the per-entry size cap, or an entry whose compression ratio looks
/// like a decompression bomb. The archive is refused before (or, when its
/// headers understate the size, while) the data is decompressed, so a hostile
/// file cannot exhaust the server's memory.
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> (the archive could not be read safely),
/// so code that already treats an unreadable archive as a per-file failure
/// handles an oversized one the same way. The message names the entry and the
/// limit and is safe to return to clients.
/// </remarks>
public sealed class ArchiveLimitExceededException : IOException
{
    public ArchiveLimitExceededException(string message)
        : base(message)
    {
    }
}
