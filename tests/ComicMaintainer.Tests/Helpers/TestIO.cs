namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// File-system helpers shared by test cleanup code.
/// </summary>
internal static class TestIO
{
    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, best effort: a directory that is
    /// already gone (or was never created) is fine, and a leftover temp directory must not fail the run.
    /// </summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: covers DirectoryNotFoundException (an IOException) from a concurrent
            // delete, files still in use, and permission problems.
        }
    }
}
