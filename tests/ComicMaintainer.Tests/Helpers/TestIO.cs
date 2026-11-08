namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// File-system helpers shared by test cleanup code.
/// </summary>
internal static class TestIO
{
    /// <summary>
    /// Asserts that <paramref name="action"/> fails because the file system refused an operation. Linux reports
    /// replacing a directory with a file (or a file locked by another handle) as an <see cref="IOException"/>, but
    /// Windows reports it as an <see cref="UnauthorizedAccessException"/>, so a test of how such a failure is handled
    /// accepts either.
    /// </summary>
    public static async Task<Exception> AssertFileSystemErrorAsync(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);

        Assert.NotNull(error);
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected an IOException or UnauthorizedAccessException, but got {error}");
        return error;
    }

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
