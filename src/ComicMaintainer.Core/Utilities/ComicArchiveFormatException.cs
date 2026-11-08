namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Thrown when a file with a comic archive extension is neither a ZIP nor a RAR archive, or is
/// damaged so badly that it does not open as one (a truncated download has no ZIP central
/// directory, for example).
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/>, so code that already treats a damaged
/// archive as a per-file failure handles this the same way. The message names the file, not its
/// location, and is safe to show in job results and processing history.
/// </remarks>
public sealed class ComicArchiveFormatException : IOException
{
    public ComicArchiveFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
