using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public sealed class LibraryPathValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;
    private readonly string _outside;
    private readonly string _realLibrary;
    private readonly string _realOutside;

    public LibraryPathValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm-library-path-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_root, "library");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_outside);
        _realLibrary = LibraryPathValidator.ResolveRealPath(_library);
        _realOutside = LibraryPathValidator.ResolveRealPath(_outside);
    }

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveWithin_RejectsABlankPath(string? path)
    {
        Assert.False(LibraryPathValidator.TryResolveWithin(path, out var resolved, _library));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_RejectsAPathTheRuntimeCannotParse()
    {
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "bad\0name.cbz"), out var resolved, _library));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_AcceptsTheAllowedDirectoryItself()
    {
        Assert.True(LibraryPathValidator.TryResolveWithin(_library + Path.DirectorySeparatorChar, out var resolved, _library));
        Assert.Equal(_realLibrary, resolved);
    }

    [Fact]
    public void TryResolveWithin_AcceptsExistingAndNotYetCreatedChildren()
    {
        var existing = Path.Combine(_library, "Series", "Issue 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "comic");

        Assert.True(LibraryPathValidator.TryResolveWithin(existing, out var resolvedExisting, _library));
        Assert.Equal(Path.Combine(_realLibrary, "Series", "Issue 001.cbz"), resolvedExisting);

        Assert.True(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "New", "Issue 002.cbz"), out var resolvedNew, _library));
        Assert.Equal(Path.Combine(_realLibrary, "New", "Issue 002.cbz"), resolvedNew);
    }

    [Fact]
    public void TryResolveWithin_RejectsASiblingThatOnlySharesTheRootPrefix()
    {
        var sibling = Path.Combine(_root, "library-other", "Issue 001.cbz");

        Assert.False(LibraryPathValidator.TryResolveWithin(sibling, out var resolved, _library));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_RejectsDotDotSegmentsThatLeaveTheRoot()
    {
        var escaping = _library + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "outside" + Path.DirectorySeparatorChar + "x.cbz";

        Assert.False(LibraryPathValidator.TryResolveWithin(escaping, out _, _library));
    }

    [Fact]
    public void TryResolveWithin_SkipsBlankAllowedDirectoriesAndKeepsLooking()
    {
        var path = Path.Combine(_library, "Issue 001.cbz");

        Assert.True(LibraryPathValidator.TryResolveWithin(path, out var resolved, null, string.Empty, "   ", _outside, _library));
        Assert.Equal(Path.Combine(_realLibrary, "Issue 001.cbz"), resolved);
    }

    [Fact]
    public void TryResolveWithin_RejectsEverythingWhenNoDirectoryIsAllowed()
    {
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "Issue 001.cbz"), out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void IsWithin_FailsClosedForTheUnixRootButNotForADriveRoot()
    {
        // Allowing "/" would make the containment check meaningless, so it
        // fails closed rather than matching every path. A Windows drive root
        // such as C:\ is a usable library root, so it contains its paths.
        var fileSystemRoot = Path.GetPathRoot(_library)!;
        var isDriveRoot = OperatingSystem.IsWindows();

        Assert.Equal(isDriveRoot, LibraryPathValidator.IsWithin(Path.Join(_realLibrary, "Issue 001.cbz"), fileSystemRoot));
        Assert.Equal(isDriveRoot, LibraryPathValidator.TryResolveWithin(Path.Join(_library, "Issue 001.cbz"), out _, fileSystemRoot));
    }

    [Theory]
    [InlineData("/comics", "/comics", true)]
    [InlineData("/comics", "/comics/", true)]
    [InlineData("/comics/", "/comics", true)]
    [InlineData("/comics", "/comics/Saga/Saga 001.cbz", true)]
    [InlineData("/comics", "/comics2/Saga 001.cbz", false)]
    [InlineData("/comics", "/comic", false)]
    [InlineData("/comics", "/elsewhere/comics/Saga 001.cbz", false)]
    [InlineData("/", "/comics/Saga 001.cbz", false)]
    [InlineData("//", "/comics/Saga 001.cbz", false)]
    public void IsSameOrUnder_ContainsOnlyTheDirectoryAndItsChildren(string directory, string fullPath, bool expected)
    {
        Assert.Equal(expected, LibraryPathValidator.IsSameOrUnder(Native(fullPath), Native(directory)));
    }

    [Fact]
    public void IsSameOrUnder_LetsADriveOrShareRootContainItsPaths()
    {
        // A library may live at a drive root (D:\) or a UNC share (\\server\comics); only a
        // directory made of separators alone, such as "/", fails closed.
        var sep = Path.DirectorySeparatorChar;

        Assert.True(LibraryPathValidator.IsSameOrUnder($"D:{sep}Saga{sep}Saga 001.cbz", $"D:{sep}"));
        Assert.False(LibraryPathValidator.IsSameOrUnder($"E:{sep}Saga{sep}Saga 001.cbz", $"D:{sep}"));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(LibraryPathValidator.IsSameOrUnder(@"\\server\comics\Saga\Saga 001.cbz", @"\\server\comics\"));
            Assert.True(LibraryPathValidator.IsSameOrUnder(@"\\server\comics", @"\\server\comics\"));
            Assert.False(LibraryPathValidator.IsSameOrUnder(@"\\server\comics2\Saga 001.cbz", @"\\server\comics\"));
        }
    }

    [Fact]
    public void IsSameOrUnder_IgnoresCaseOnlyOnWindows()
    {
        Assert.Equal(
            OperatingSystem.IsWindows(),
            LibraryPathValidator.IsSameOrUnder(Native("/COMICS/Saga 001.cbz"), Native("/comics")));
    }

    [Fact]
    public void IsLexicallyWithin_NormalizesThePathButNotTheRootsLinks()
    {
        // Unlike TryResolveWithin, a link is judged by where it is, not where it leads.
        var linkedRoot = Path.Join(_outside, "library-link");
        Directory.CreateSymbolicLink(linkedRoot, _library);
        var escape = Path.Join(_library, "escape");
        Directory.CreateSymbolicLink(escape, _outside);

        Assert.True(LibraryPathValidator.IsLexicallyWithin(Path.Join(escape, "secret.cbz"), _library));
        Assert.False(LibraryPathValidator.IsLexicallyWithin(Path.Join(_library, "Issue 001.cbz"), linkedRoot));
        Assert.True(LibraryPathValidator.IsLexicallyWithin(Path.Join(_library, "Series", "..", "Issue 001.cbz"), _library));
        Assert.False(LibraryPathValidator.IsLexicallyWithin(Path.Join(_library, "..", "outside", "x.cbz"), _library));
    }

    [Fact]
    public void IsLexicallyWithin_ChecksEveryConfiguredRootAndSkipsBlankOnes()
    {
        var path = Path.Join(_library, "Issue 001.cbz");

        Assert.True(LibraryPathValidator.IsLexicallyWithin(path, null, string.Empty, "   ", _outside, _library));
        Assert.False(LibraryPathValidator.IsLexicallyWithin(path, null, string.Empty, _outside));
        Assert.False(LibraryPathValidator.IsLexicallyWithin(path));
    }

    [Fact]
    public void IsLexicallyWithin_FailsClosedForTheUnixRootButNotForADriveRoot()
    {
        // The same rule as IsWithin: "/" contains nothing, a drive root such as C:\ its paths.
        var fileSystemRoot = Path.GetPathRoot(_library)!;

        Assert.Equal(
            OperatingSystem.IsWindows(),
            LibraryPathValidator.IsLexicallyWithin(Path.Join(_library, "Issue 001.cbz"), fileSystemRoot));
    }

    [Fact]
    public void IsLexicallyWithin_ThrowsForAPathTheRuntimeCannotParse()
    {
        Assert.Throws<ArgumentException>(() => LibraryPathValidator.IsLexicallyWithin(string.Empty, _library));
        Assert.Throws<ArgumentException>(() => LibraryPathValidator.IsLexicallyWithin(Path.Join(_library, "bad\0name.cbz"), _library));
    }

    [Fact]
    public void TryResolveWithin_RejectsADirectoryLinkThatPointsOutsideTheRoot()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.cbz"), "x");
        var link = Path.Combine(_library, "escape");
        Directory.CreateSymbolicLink(link, _outside);

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out _, _library));
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(link, "secret.cbz"), out _, _library));
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(link, "not-yet-created.cbz"), out _, _library));
    }

    [Fact]
    public void TryResolveWithin_RejectsAFileLinkThatPointsOutsideTheRoot()
    {
        var target = Path.Combine(_outside, "secret.cbz");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_library, "innocent.cbz");
        File.CreateSymbolicLink(link, target);

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out var resolved, _library));
        Assert.Equal(string.Empty, resolved);
        Assert.Equal(Path.Combine(_realOutside, "secret.cbz"), LibraryPathValidator.ResolveRealPath(link));
    }

    [Fact]
    public void TryResolveWithin_RejectsADanglingLinkThatPointsOutsideTheRoot()
    {
        // Writing through the link would create the file outside the library,
        // so a link whose target does not exist yet must still be followed.
        var link = Path.Combine(_library, "pending.cbz");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "created-later.cbz"));

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out _, _library));
    }

    [Fact]
    public void TryResolveWithin_ReturnsThePhysicalPathOfALinkIntoTheRoot()
    {
        var target = Path.Combine(_library, "Series", "Issue 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "comic");
        var link = Path.Combine(_outside, "shortcut.cbz");
        File.CreateSymbolicLink(link, target);

        Assert.True(LibraryPathValidator.TryResolveWithin(link, out var resolved, _library));
        Assert.Equal(Path.Combine(_realLibrary, "Series", "Issue 001.cbz"), resolved);
    }

    [Fact]
    public void TryResolveWithin_ResolvesALinkedAllowedDirectory()
    {
        var linkedRoot = Path.Combine(_outside, "library-link");
        Directory.CreateSymbolicLink(linkedRoot, _library);

        Assert.True(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "Issue 001.cbz"), out var direct, linkedRoot));
        Assert.Equal(Path.Combine(_realLibrary, "Issue 001.cbz"), direct);

        Assert.True(LibraryPathValidator.TryResolveWithin(Path.Combine(linkedRoot, "Issue 001.cbz"), out var viaLink, linkedRoot));
        Assert.Equal(Path.Combine(_realLibrary, "Issue 001.cbz"), viaLink);
    }

    [Fact]
    public void TryResolveWithin_RejectsAFileLinkWhoseTargetGoesThroughADirectoryLinkOutsideTheRoot()
    {
        // library/a.cbz -> library/esc/secret.cbz looks contained, but
        // library/esc -> outside, so the file actually read is outside/secret.cbz.
        File.WriteAllText(Path.Combine(_outside, "secret.cbz"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_library, "esc"), _outside);
        var link = Path.Combine(_library, "a.cbz");
        File.CreateSymbolicLink(link, Path.Combine(_library, "esc", "secret.cbz"));

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out var resolved, _library));
        Assert.Equal(string.Empty, resolved);
        Assert.Equal(Path.Combine(_realOutside, "secret.cbz"), LibraryPathValidator.ResolveRealPath(link));
    }

    [Fact]
    public void TryResolveWithin_RejectsChainedRelativeLinksThatEndOutsideTheRoot()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.cbz"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_library, "esc"), _outside);
        File.CreateSymbolicLink(Path.Combine(_library, "c.cbz"), Path.Combine("esc", "secret.cbz"));
        var link = Path.Combine(_library, "b.cbz");
        File.CreateSymbolicLink(link, "c.cbz");
        var pending = Path.Combine(_library, "pending.cbz");
        File.CreateSymbolicLink(pending, Path.Combine("esc", "created-later.cbz"));

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out _, _library));
        Assert.Equal(Path.Combine(_realOutside, "secret.cbz"), LibraryPathValidator.ResolveRealPath(link));
        Assert.False(LibraryPathValidator.TryResolveWithin(pending, out _, _library));
        Assert.Equal(Path.Combine(_realOutside, "created-later.cbz"), LibraryPathValidator.ResolveRealPath(pending));
    }

    [Fact]
    public void TryResolveWithin_ResolvesDotDotInALinkTargetFromWhereTheLinkReallyLeads()
    {
        // The OS applies ".." after following library/deep -> outside/deep, so
        // "deep/../secret.cbz" is outside/secret.cbz, not library/secret.cbz.
        var deep = Path.Combine(_outside, "deep");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(_outside, "secret.cbz"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_library, "deep"), deep);
        var link = Path.Combine(_library, "d.cbz");
        File.CreateSymbolicLink(link, Path.Combine("deep", "..", "secret.cbz"));

        Assert.False(LibraryPathValidator.TryResolveWithin(link, out _, _library));
        Assert.Equal(Path.Combine(_realOutside, "secret.cbz"), LibraryPathValidator.ResolveRealPath(link));
    }

    [Fact]
    public void TryResolveWithin_ReturnsThePhysicalPathOfLinksThatStayInsideTheRoot()
    {
        var series = Path.Combine(_library, "Series");
        var issue = Path.Combine(series, "Issue 001.cbz");
        Directory.CreateDirectory(series);
        Directory.CreateDirectory(Path.Combine(_library, "Other"));
        File.WriteAllText(issue, "comic");
        var expected = Path.Combine(_realLibrary, "Series", "Issue 001.cbz");
        var fileSystemRoot = Path.GetPathRoot(issue)!;
        Directory.CreateSymbolicLink(Path.Combine(_library, "series-link"), series);
        var links = new[]
        {
            // Absolute target through an inside directory link.
            (Path.Combine(_library, "absolute.cbz"), Path.Combine(_library, "series-link", "Issue 001.cbz")),
            // Relative target with "." and ".." segments, chained to the link above.
            (Path.Combine(_library, "Other", "relative.cbz"), Path.Combine("..", ".", "absolute.cbz")),
            // ".." above the file system root stays at the root.
            (Path.Combine(_library, "rooted.cbz"), Path.Combine(fileSystemRoot, "..", Path.GetRelativePath(fileSystemRoot, issue))),
        };

        foreach (var (link, target) in links)
        {
            File.CreateSymbolicLink(link, target);

            Assert.True(LibraryPathValidator.TryResolveWithin(link, out var resolved, _library), link);
            Assert.Equal(expected, resolved);
        }
    }

    [Fact]
    public void TryResolveWithin_FollowsUpToFortyLinksLikeTheOperatingSystem()
    {
        var issue = Path.Combine(_library, "Issue 001.cbz");
        File.WriteAllText(issue, "comic");
        File.CreateSymbolicLink(Path.Combine(_library, "hop0"), "Issue 001.cbz");
        for (var hop = 1; hop <= 40; hop++)
        {
            File.CreateSymbolicLink(Path.Combine(_library, $"hop{hop}"), $"hop{hop - 1}");
        }

        Assert.True(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "hop39"), out var resolved, _library));
        Assert.Equal(Path.Combine(_realLibrary, "Issue 001.cbz"), resolved);
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "hop40"), out resolved, _library));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_RejectsLinkLoopsInsteadOfHanging()
    {
        // A loop never reaches a target, so the OS cannot open it either; it is
        // rejected rather than checked "as written".
        var self = Path.Combine(_library, "self");
        File.CreateSymbolicLink(self, "self");
        var first = Path.Combine(_library, "loop-a");
        var second = Path.Combine(_library, "loop-b");
        File.CreateSymbolicLink(first, second);
        File.CreateSymbolicLink(second, first);

        foreach (var path in new[] { self, first, Path.Combine(first, "child.cbz") })
        {
            Assert.False(LibraryPathValidator.TryResolveWithin(path, out var resolved, _library), path);
            Assert.Equal(string.Empty, resolved);
            Assert.Throws<IOException>(() => LibraryPathValidator.ResolveRealPath(path));
        }

        // A looping allowed directory contains nothing.
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "Issue 001.cbz"), out _, first));
    }

    [Fact]
    public void ResolveRealPath_LimitsTheTotalNumberOfLinksNotJustTheChainLength()
    {
        // Each fan-N link expands to fan-(N+1) twice: no chain is longer than 31
        // links, but resolving fan-0 fully would follow about 2^31 of them.
        Directory.CreateDirectory(Path.Combine(_library, "Series"));
        Directory.CreateSymbolicLink(Path.Combine(_library, "fan-30"), "Series");
        for (var level = 29; level >= 0; level--)
        {
            var next = $"fan-{level + 1}";
            Directory.CreateSymbolicLink(Path.Combine(_library, $"fan-{level}"), Path.Combine(next, "..", next));
        }

        Assert.Throws<IOException>(() => LibraryPathValidator.ResolveRealPath(Path.Combine(_library, "fan-0")));
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "fan-0"), out _, _library));
    }
}
