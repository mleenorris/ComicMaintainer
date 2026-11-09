using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// What one <see cref="LibraryRoots"/> remembers between checks. The containment rules themselves
/// are in LibraryPathValidatorTests, whose static checks are this class with a single use.
/// </summary>
public sealed class LibraryRootsTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;
    private readonly string _outside;

    public LibraryRootsTests()
    {
        _root = Path.Join(Path.GetTempPath(), "cm-library-roots-" + Guid.NewGuid().ToString("N"));
        _library = Directory.CreateDirectory(Path.Join(_root, "library")).FullName;
        _outside = Directory.CreateDirectory(Path.Join(_root, "outside")).FullName;
    }

    public void Dispose()
    {
        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public void ManyPathsAreJudgedTheSameAsOneAtATime()
    {
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var roots = new LibraryRoots(null, " ", _library);

        var inside = new[] { Path.Join(series, "1.cbz"), Path.Join(series, "2.cbz"), Path.Join(_library, "3.cbz") };
        foreach (var path in inside)
        {
            Assert.True(roots.TryResolveWithin(path, out var resolved));
            Assert.True(LibraryPathValidator.TryResolveWithin(path, out var alone, _library));
            Assert.Equal(alone, resolved);
        }

        Assert.False(roots.TryResolveWithin(Path.Join(_outside, "1.cbz"), out _));
        Assert.False(roots.TryResolveWithin(Path.Join(series, "..", "..", "..", "outside", "2.cbz"), out _));
    }

    [Fact]
    public void AnEntryIsCheckedAgainstTheDirectoryHoldingIt()
    {
        var roots = new LibraryRoots(_library);
        var entry = Path.Join(_library, "1.cbz");

        Assert.True(roots.TryResolveEntryWithin(entry, out var first));
        Assert.True(roots.TryResolveEntryWithin(entry, out var second));
        Assert.Equal(first, second);
        Assert.Equal("1.cbz", Path.GetFileName(first));
        Assert.False(roots.TryResolveEntryWithin(Path.Join(_library, "..", "library", "1.cbz"), out _));
    }

    [SymbolicLinkFact]
    public void ALinkIsFollowedForEveryPathThroughIt()
    {
        Directory.CreateSymbolicLink(Path.Join(_library, "escape"), _outside);
        var roots = new LibraryRoots(_library);

        Assert.False(roots.TryResolveWithin(Path.Join(_library, "escape", "1.cbz"), out _));
        Assert.False(roots.TryResolveWithin(Path.Join(_library, "escape", "2.cbz"), out _));
        Assert.True(roots.TryResolveWithin(Path.Join(_library, "kept", "1.cbz"), out _));
    }

    [Fact]
    public void TheDirectoriesLeadingToAPathAreRememberedButNotThePathItself()
    {
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var knownNotLinks = new HashSet<string>(StringComparer.Ordinal);

        LibraryPathValidator.ResolveRealPath(Path.Join(series, "1.cbz"), knownNotLinks);

        Assert.Contains(knownNotLinks, path => path.EndsWith(Path.DirectorySeparatorChar + "Series", StringComparison.Ordinal));
        Assert.DoesNotContain(knownNotLinks, path => path.EndsWith("1.cbz", StringComparison.Ordinal));
    }

    [SymbolicLinkFact]
    public void ADirectoryThatBecomesALinkIsOnlyNoticedByANewInstance()
    {
        var series = Directory.CreateDirectory(Path.Join(_library, "Series")).FullName;
        var comic = Path.Join(series, "1.cbz");
        var roots = new LibraryRoots(_library);
        Assert.True(roots.TryResolveWithin(comic, out _));

        Directory.Delete(series);
        Directory.CreateSymbolicLink(series, _outside);

        // Windows and macOS finish by asking for the opened directory's path, which follows
        // the new link, so only elsewhere does the first answer stand.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.True(roots.TryResolveWithin(comic, out _));
        }

        Assert.False(new LibraryRoots(_library).TryResolveWithin(comic, out _));
    }

    [SymbolicLinkFact]
    public void AnAllowedDirectoryIsOnlyResolvedOnce()
    {
        var linkedRoot = Path.Join(_root, "linked");
        Directory.CreateSymbolicLink(linkedRoot, _library);
        var comic = Path.Join(_library, "1.cbz");
        var roots = new LibraryRoots(linkedRoot);
        Assert.True(roots.TryResolveWithin(comic, out _));

        Directory.Delete(linkedRoot);
        Directory.CreateSymbolicLink(linkedRoot, _outside);

        Assert.True(roots.TryResolveWithin(comic, out _));
        Assert.False(new LibraryRoots(linkedRoot).TryResolveWithin(comic, out _));
    }
}
