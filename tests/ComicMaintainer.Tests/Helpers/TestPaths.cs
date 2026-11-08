namespace ComicMaintainer.Tests.Helpers;

/// <summary>Paths written POSIX-style in test data, as the code under test sees them on this platform.</summary>
internal static class TestPaths
{
    /// <summary>
    /// <paramref name="posixPath"/> with the platform's directory separator. Path.GetDirectoryName and the other
    /// path methods turn '/' into '\' on Windows, so the folder of "/lib/Saga/1.cbz" is "\lib\Saga" there.
    /// </summary>
    public static string Native(string posixPath) => posixPath.Replace('/', Path.DirectorySeparatorChar);
}
