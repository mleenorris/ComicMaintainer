namespace ComicMaintainer.Tests.Helpers;

/// <summary>
/// What the machine running the tests can do, each detected once per run. Tests that need a capability use the
/// attributes below, which skip them with the reason instead of letting them fail, or pass without testing
/// anything, where the capability is missing. Each capability is detected by trying it, not by the operating
/// system's name, so a Linux or macOS run, which has them all, never skips. File locking is the exception: it
/// behaves differently by platform, so each behaviour has its own test, which runs where that behaviour applies.
/// </summary>
internal static class TestCapabilities
{
    private static readonly Lazy<string?> SymbolicLinkProblem = new(DetectSymbolicLinkProblem);
    private static readonly Lazy<string?> Bash = new(FindBash);
    private static readonly Lazy<bool> ExclusiveLockBlocksMove = new(DetectExclusiveLockBlocksMove);

    /// <summary>
    /// Why this process cannot create symbolic links, or null when it can. Windows lets only elevated processes,
    /// or any process once Developer Mode is on, create them.
    /// </summary>
    public static string? WhyNoSymbolicLinks => SymbolicLinkProblem.Value;

    /// <summary>The bash that runs the container scripts, or null when there is none.</summary>
    public static string? BashPath => Bash.Value;

    /// <summary>
    /// Whether a file another handle holds open without sharing cannot be moved (Windows), rather than being locked
    /// only against other opens that ask for a lock (the advisory locks of Linux and macOS).
    /// </summary>
    public static bool ExclusiveLockBlocksMoving => ExclusiveLockBlocksMove.Value;

    private static bool DetectExclusiveLockBlocksMove()
    {
        var directory = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "lock-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var locked = Path.Join(directory, "locked");
            File.WriteAllText(locked, "probe");
            using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                File.Move(locked, Path.Join(directory, "moved"));
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        finally
        {
            TestIO.TryDeleteDirectory(directory);
        }
    }

    private static string? DetectSymbolicLinkProblem()
    {
        var directory = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Join(directory, "target");
            File.WriteAllText(target, "probe");
            var link = Path.Join(directory, "link");
            File.CreateSymbolicLink(link, target);
            var directoryLink = Path.Join(directory, "directory-link");
            Directory.CreateSymbolicLink(directoryLink, directory);
            return File.ReadAllText(link) == "probe" && Directory.Exists(directoryLink)
                ? null
                : "symbolic links created by this process do not resolve";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return $"this process cannot create symbolic links ({ex.GetType().Name}: {ex.Message.Trim()}); on Windows, " +
                "turn on Developer Mode or run the tests elevated";
        }
        finally
        {
            TestIO.TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// /bin/bash, or on Windows the bash of Git for Windows. The bash.exe in System32 is WSL's and runs scripts in a
    /// Linux distribution with paths of its own, so it is never used.
    /// </summary>
    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : File.Exists("/usr/bin/bash") ? "/usr/bin/bash" : null;
        }

        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return candidates
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Select(folder => Path.Join(folder, "Git", "bin", "bash.exe"))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>A fact that needs symbolic links; skipped, with the reason, where the process cannot create them.</summary>
internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (TestCapabilities.WhyNoSymbolicLinks is { } reason)
        {
            Skip = "Needs symbolic links, but " + reason + ".";
        }
    }
}

/// <summary>A theory that needs symbolic links; skipped, with the reason, where the process cannot create them.</summary>
internal sealed class SymbolicLinkTheoryAttribute : TheoryAttribute
{
    public SymbolicLinkTheoryAttribute()
    {
        if (TestCapabilities.WhyNoSymbolicLinks is { } reason)
        {
            Skip = "Needs symbolic links, but " + reason + ".";
        }
    }
}

/// <summary>How a file that another handle holds open without sharing behaves; see <see cref="TestCapabilities.ExclusiveLockBlocksMoving"/>.</summary>
internal enum ExclusiveLock
{
    /// <summary>The file cannot be read, moved or deleted (Windows).</summary>
    BlocksMoving,

    /// <summary>The lock is advisory: the file can still be moved (Linux and macOS).</summary>
    AllowsMoving,
}

/// <summary>A fact about a file held open without sharing; it runs only where such a lock behaves as it needs.</summary>
internal sealed class ExclusiveLockFactAttribute : FactAttribute
{
    public ExclusiveLockFactAttribute(ExclusiveLock needed)
    {
        if (TestCapabilities.ExclusiveLockBlocksMoving != (needed == ExclusiveLock.BlocksMoving))
        {
            Skip = needed == ExclusiveLock.BlocksMoving
                ? "Needs a file held open without sharing to be impossible to move, as on Windows; here the lock is advisory."
                : "Needs a file held open without sharing to stay movable (advisory locks, as on Linux and macOS); here the lock blocks moving it.";
        }
    }
}

/// <summary>A fact that runs a shell script with bash; skipped where there is no bash (Windows without Git).</summary>
internal sealed class BashFactAttribute : FactAttribute
{
    public BashFactAttribute()
    {
        if (TestCapabilities.BashPath is null)
        {
            Skip = "Needs bash to run the container scripts; on Windows, install Git for Windows.";
        }
    }
}
