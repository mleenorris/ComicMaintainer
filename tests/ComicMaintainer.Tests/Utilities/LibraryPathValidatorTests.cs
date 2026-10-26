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
    public void IsWithin_NeverTreatsTheFileSystemRootAsAContainer()
    {
        // Allowing "/" would make the containment check meaningless, so it
        // fails closed rather than matching every path.
        var fileSystemRoot = Path.GetPathRoot(_library)!;

        Assert.False(LibraryPathValidator.IsWithin(Path.Combine(_realLibrary, "Issue 001.cbz"), fileSystemRoot));
        Assert.False(LibraryPathValidator.TryResolveWithin(Path.Combine(_library, "Issue 001.cbz"), out _, fileSystemRoot));
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
    public void TryResolveWithin_KeepsACyclicLinkAsWrittenInsteadOfFailing()
    {
        // A link cycle cannot be resolved to any target, so it cannot point
        // anywhere else either; the link path itself is checked.
        var first = Path.Combine(_library, "loop-a");
        var second = Path.Combine(_library, "loop-b");
        File.CreateSymbolicLink(first, second);
        File.CreateSymbolicLink(second, first);

        Assert.Equal(Path.Combine(_realLibrary, "loop-a"), LibraryPathValidator.ResolveRealPath(first));
        Assert.True(LibraryPathValidator.TryResolveWithin(first, out var resolved, _library));
        Assert.Equal(Path.Combine(_realLibrary, "loop-a"), resolved);
        Assert.False(LibraryPathValidator.TryResolveWithin(first, out _, _outside));
    }
}
