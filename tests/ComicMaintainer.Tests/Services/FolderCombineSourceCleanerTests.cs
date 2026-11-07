using System.Diagnostics;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers what a folder combine may delete from a source folder once its
/// tracked files have moved out (<see cref="FolderCombineSourceCleaner"/>).
/// </summary>
public sealed class FolderCombineSourceCleanerTests : IDisposable
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private readonly string _root = Path.Join(Path.GetTempPath(), $"fc-cleaner-{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _source;

    public FolderCombineSourceCleanerTests()
    {
        _library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        _source = Directory.CreateDirectory(Path.Join(_library, "Berserk (Digital)")).FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private string Write(string relativePath, string content = "x")
    {
        var path = Path.Join(_source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private IReadOnlyCollection<string> Protected(params string[] extra) => new[] { _library }.Concat(extra).ToList();

    private static bool CanWriteInto(string directory)
    {
        var probe = Path.Join(directory, "probe.tmp");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Rel(params string[] parts) => Path.Join(parts);

    /// <summary>
    /// Creates a directory link, falling back to a junction on Windows when
    /// symbolic links need a privilege the test run lacks. Returns false when
    /// neither can be made here.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        mklink.WaitForExit(30_000);
        return mklink.HasExited && mklink.ExitCode == 0 && Directory.Exists(link);
    }

    // ------------------------------------------------------------------ disposable

    [Theory]
    [InlineData("ComicInfo.xml", 0, true)]
    [InlineData("comicinfo.XML", 0, true)]
    [InlineData("cover.jpg", 0, true)]
    [InlineData("Cover.PNG", 0, true)]
    [InlineData("cover.webp", 0, true)]
    [InlineData("folder.jpeg", 0, true)]
    [InlineData(".DS_Store", 0, true)]
    [InlineData("Thumbs.db", 2, true)]
    [InlineData("desktop.ini", 1, true)]
    [InlineData("ComicInfo.xml", 1, false)]
    [InlineData("cover.jpg", 1, false)]
    [InlineData("cover.pdf", 0, false)]
    [InlineData("poster.jpg", 0, false)]
    [InlineData("series.json", 0, false)]
    [InlineData("Berserk v01.pdf", 0, false)]
    [InlineData("Berserk v02.epub", 0, false)]
    [InlineData("Berserk v03.cb7", 0, false)]
    [InlineData("Berserk v04.cbz", 0, false)]
    [InlineData(".tmp_0123456789abcdef.suwayomi-import", 0, false)]
    [InlineData("001.jpg", 0, false)]
    public void IsDisposableFile_OnlyAcceptsFolderMetadataAndTheFoldersOwnCover(string name, int depth, bool expected)
    {
        Assert.Equal(expected, FolderCombineSourceCleaner.IsDisposableFile(name, depth));
    }

    // ------------------------------------------------------------------- assess

    [Fact]
    public void Assess_OnlyDepartingComicsAndDisposableFiles_PredictsRemovalAndListsWhatIsDeleted()
    {
        var departing = new HashSet<string>(FolderCombineSourceCleaner.PathComparer)
        {
            Write("Berserk 001.cbz"),
            Write("Berserk 002.cbz")
        };
        Write("ComicInfo.xml");
        Write("cover.jpg");
        Write(Rel("Scans", ".DS_Store"));
        Directory.CreateDirectory(Path.Join(_source, "Empty", "Nested"));

        var cleanup = FolderCombineSourceCleaner.Assess(_source, departing, Nothing, Protected());

        Assert.True(cleanup.Removable);
        Assert.Null(cleanup.KeptReason);
        Assert.Equal(
            new[] { "ComicInfo.xml", "cover.jpg", Rel("Scans", ".DS_Store") }.OrderBy(f => f, StringComparer.Ordinal),
            cleanup.DisposableFiles.OrderBy(f => f, StringComparer.Ordinal));
        Assert.Equal(3, cleanup.DisposableFileCount);
        Assert.Empty(cleanup.Leftovers);
        Assert.Equal(0, cleanup.LeftoverCount);
        // A preview changes nothing.
        Assert.All(departing, path => Assert.True(File.Exists(path)));
        Assert.True(File.Exists(Path.Join(_source, "ComicInfo.xml")));
    }

    [Fact]
    public void Assess_FilesTheAppDoesNotOwn_KeepTheFolderAndAreListedAsLeftovers()
    {
        var departing = new HashSet<string>(FolderCombineSourceCleaner.PathComparer) { Write("Berserk 001.cbz") };
        Write("ComicInfo.xml");
        Write("Berserk v01.pdf");
        Write("Berserk v02.epub");
        Write("Berserk v03.cb7");
        Write("Berserk 099.cbz"); // an archive the library does not track
        Write("series.json");
        Write(Rel("Extras", "art1.jpg"));
        Write(Rel("Extras", "cover.jpg"));

        var cleanup = FolderCombineSourceCleaner.Assess(_source, departing, Nothing, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(FolderCombineSourceCleaner.LeftoverReason, cleanup.KeptReason);
        Assert.Equal(
            new[]
            {
                "Berserk 099.cbz", "Berserk v01.pdf", "Berserk v02.epub", "Berserk v03.cb7",
                Rel("Extras", "art1.jpg"), Rel("Extras", "cover.jpg"), "series.json"
            },
            cleanup.Leftovers.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(7, cleanup.LeftoverCount);
    }

    [Fact]
    public void Assess_HiddenEntriesAreSeen()
    {
        var hidden = Write(".notes.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        Write(".tmp_0123456789abcdef.suwayomi-import");

        var cleanup = FolderCombineSourceCleaner.Assess(_source, Nothing, Nothing, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(
            new[] { ".notes.txt", ".tmp_0123456789abcdef.suwayomi-import" },
            cleanup.Leftovers.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void Assess_ManyLeftovers_ListsTheFirstFewAndCountsThemAll()
    {
        for (var i = 0; i < FolderCombineSourceCleanupLimit + 5; i++)
        {
            Write($"page{i:000}.png");
        }

        var cleanup = FolderCombineSourceCleaner.Assess(_source, Nothing, Nothing, Protected());

        Assert.Equal(FolderCombineSourceCleanupLimit, cleanup.Leftovers.Count);
        Assert.Equal(FolderCombineSourceCleanupLimit + 5, cleanup.LeftoverCount);
        Assert.EndsWith("and 5 more.", FolderCombineSourceCleaner.DescribeKeptFolder(cleanup));
    }

    private const int FolderCombineSourceCleanupLimit = Core.Models.FolderCombineSourceCleanup.MaxListedEntries;

    [Fact]
    public void Assess_ANestedFolderThatIsRemovedFirst_IsNotALeftoverOfItsParent()
    {
        var nested = Directory.CreateDirectory(Path.Join(_source, "Berserk (Digital) Part 2")).FullName;
        File.WriteAllText(Path.Join(nested, "ComicInfo.xml"), "x");
        var removed = new HashSet<string>(FolderCombineSourceCleaner.PathComparer) { nested };

        var cleanup = FolderCombineSourceCleaner.Assess(_source, Nothing, removed, Protected());

        Assert.True(cleanup.Removable);
        Assert.Empty(cleanup.DisposableFiles);
    }

    [Fact]
    public void Assess_TheWatchedFolderItself_IsNeverRemovable()
    {
        var cleanup = FolderCombineSourceCleaner.Assess(_library, Nothing, Nothing, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(FolderCombineSourceCleaner.ProtectedFolderReason, cleanup.KeptReason);
    }

    [Fact]
    public void Assess_AProtectedFolderInside_IsALeftover()
    {
        var duplicates = Directory.CreateDirectory(Path.Join(_source, "Duplicates")).FullName;

        var cleanup = FolderCombineSourceCleaner.Assess(_source, Nothing, Nothing, Protected(duplicates));

        Assert.False(cleanup.Removable);
        Assert.Equal(new[] { "Duplicates" + Path.DirectorySeparatorChar }, cleanup.Leftovers);
    }

    [Fact]
    public void Assess_MissingFolder_IsReportedAsGone()
    {
        var cleanup = FolderCombineSourceCleaner.Assess(Path.Join(_library, "Gone"), Nothing, Nothing, Protected());

        Assert.True(cleanup.Removable);
        Assert.Equal(0, cleanup.LeftoverCount);
    }

    // ------------------------------------------------------------------- remove

    [Fact]
    public void Remove_OnlyDisposableEntriesLeft_DeletesThemAndTheFolder()
    {
        Write("ComicInfo.xml");
        Write("folder.png");
        Write(Rel("Scans", "Thumbs.db"));
        Directory.CreateDirectory(Path.Join(_source, "Empty", "Nested"));

        var cleanup = FolderCombineSourceCleaner.Remove(_source, Protected());

        Assert.True(cleanup.Removable);
        Assert.Equal(3, cleanup.DisposableFileCount);
        Assert.False(Directory.Exists(_source));
        Assert.True(Directory.Exists(_library));
    }

    [Fact]
    public void Remove_AnythingElseLeft_KeepsTheFolderAndDeletesNothing()
    {
        var comicInfo = Write("ComicInfo.xml");
        var cover = Write("cover.jpg");
        var pdf = Write("Berserk v01.pdf");
        var art = Write(Rel("Extras", "art1.jpg"));
        Directory.CreateDirectory(Path.Join(_source, "Empty"));

        var cleanup = FolderCombineSourceCleaner.Remove(_source, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(new[] { "Berserk v01.pdf", Rel("Extras", "art1.jpg") }, cleanup.Leftovers.OrderBy(f => f, StringComparer.Ordinal));
        Assert.All(new[] { comicInfo, cover, pdf, art }, path => Assert.True(File.Exists(path)));
        Assert.True(Directory.Exists(Path.Join(_source, "Empty")));
        var message = FolderCombineSourceCleaner.DescribeKeptFolder(cleanup);
        Assert.StartsWith("Source folder kept. ", message);
        Assert.Contains("Berserk v01.pdf", message);
        Assert.Contains(Rel("Extras", "art1.jpg"), message);
    }

    [Fact]
    public void Remove_TheWatchedFolder_KeepsItEvenWhenEmpty()
    {
        Directory.Delete(_source);

        var cleanup = FolderCombineSourceCleaner.Remove(_library, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(FolderCombineSourceCleaner.ProtectedFolderReason, cleanup.KeptReason);
        Assert.True(Directory.Exists(_library));
    }

    [Fact]
    public void Remove_AFileThatCannotBeDeleted_KeepsTheFolderWithTheError()
    {
        var comicInfo = Write("ComicInfo.xml");
        using var held = new FileStream(comicInfo, FileMode.Open, FileAccess.Read, FileShare.None);
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete an open file; elsewhere make the
            // folder read-only so the delete fails.
            File.SetUnixFileMode(_source, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            if (CanWriteInto(_source))
            {
                // Running as root: permissions are not enforced.
                File.SetUnixFileMode(_source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return;
            }
        }

        try
        {
            var cleanup = FolderCombineSourceCleaner.Remove(_source, Protected());

            Assert.False(cleanup.Removable);
            Assert.StartsWith("It could not be removed: ", cleanup.KeptReason);
            Assert.True(Directory.Exists(_source));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Remove_ALinkInside_KeepsTheFolderAndNeverTouchesTheTarget()
    {
        var outside = Directory.CreateDirectory(Path.Join(_root, "outside")).FullName;
        var outsideFile = Path.Join(outside, "ComicInfo.xml");
        File.WriteAllText(outsideFile, "x");
        var link = Path.Join(_source, "linked");
        if (!TryCreateDirectoryLink(link, outside))
        {
            return; // Links cannot be created here.
        }

        var cleanup = FolderCombineSourceCleaner.Remove(_source, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(new[] { "linked" }, cleanup.Leftovers);
        Assert.True(File.Exists(outsideFile));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void Remove_ASourceFolderThatIsALink_IsKept()
    {
        var target = Directory.CreateDirectory(Path.Join(_library, "Real")).FullName;
        File.WriteAllText(Path.Join(target, "ComicInfo.xml"), "x");
        var link = Path.Join(_library, "Alias");
        if (!TryCreateDirectoryLink(link, target))
        {
            return; // Links cannot be created here.
        }

        var cleanup = FolderCombineSourceCleaner.Remove(link, Protected());

        Assert.False(cleanup.Removable);
        Assert.Equal(FolderCombineSourceCleaner.LinkReason, cleanup.KeptReason);
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(Path.Join(target, "ComicInfo.xml")));
    }
}
