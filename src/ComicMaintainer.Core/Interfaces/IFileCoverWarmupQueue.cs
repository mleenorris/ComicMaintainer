namespace ComicMaintainer.Core.Interfaces;

/// <summary>
/// Covers to generate in the background before anyone asks for them: those of files that are new
/// to the library or have just changed, whose first library view would otherwise generate every
/// cover it shows at once.
/// </summary>
public interface IFileCoverWarmupQueue
{
    /// <summary>
    /// Asks for the cover of <paramref name="filePath"/> to be generated in the background if it is
    /// not cached. Never blocks; a file already waiting is not queued twice, and nothing is queued
    /// while the queue is full.
    /// </summary>
    void Enqueue(string filePath);
}
