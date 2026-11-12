namespace ComicMaintainer.Core.Models;

/// <summary>
/// A tracked file whose cached archive metadata is missing or no longer matches the file
/// (see <c>IFileStoreService.GetFilesNeedingMetadataScanAsync</c>), with the stored size and
/// modification time it had when it was selected. The scan's result is recorded only while
/// the row still has those values, so a file that changed during the read is read again.
/// </summary>
public sealed record ArchiveMetadataScanCandidate(int Id, string FilePath, long FileSize, DateTime LastModified);

/// <summary>Outcome of one pass of the background archive-metadata scan.</summary>
/// <param name="Recorded">Files whose metadata was read and cached.</param>
/// <param name="Unreadable">Files whose archive could not be read; skipped until they change.</param>
/// <param name="Skipped">Files that changed (or were edited) while being read; read again by a later pass.</param>
public sealed record ArchiveMetadataScanResult(int Recorded, int Unreadable, int Skipped);
