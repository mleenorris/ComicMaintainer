using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The library root's folder name is never a series candidate for a loose file in it.
/// </summary>
public class SeriesNameResolverLibraryRootTests
{
    private static readonly string Root = Path.Join(Path.GetTempPath(), "watched_dir");

    private readonly Mock<IExternalSeriesMetadataService> _external = new();

    private SeriesNameResolver CreateResolver() =>
        new(
            new TestOptionsMonitor<AppSettings>(new AppSettings
            {
                WatchedDirectory = Root,
                DuplicateDirectory = Path.Join(Root, "duplicates")
            }),
            Mock.Of<ILogger<SeriesNameResolver>>(),
            cache: null,
            _external.Object);

    [Fact]
    public async Task ResolveAsync_LooseFileWithoutSeries_HasNoFolderCandidateAndNoLookup()
    {
        var result = await CreateResolver().ResolveAsync(Path.Join(Root, "001.cbz"), metadata: null, mutateCache: false);

        Assert.Null(result.FolderSeries);
        Assert.Empty(result.Candidates);
        Assert.Equal(SeriesNameResolutionStep.UnknownSentinel, result.WinningStep);
        _external.Verify(e => e.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_LooseFileInTheDuplicatesRoot_HasNoFolderCandidate()
    {
        var result = await CreateResolver().ResolveAsync(
            Path.Join(Root, "duplicates", "Batman 001.cbz"),
            new ComicMetadata { Series = "Batman" },
            mutateCache: false);

        Assert.Null(result.FolderSeries);
        Assert.Equal(new[] { "Batman" }, result.Candidates);
        Assert.Equal("Batman", result.ResolvedSeries);
        _external.Verify(e => e.LookupSeriesAsync("Batman", It.IsAny<CancellationToken>()), Times.Once);
        _external.Verify(e => e.LookupSeriesAsync(It.Is<string>(s => s.Contains("watched") || s.Contains("duplicates")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_FileInASeriesFolder_KeepsTheFolderCandidate()
    {
        var result = await CreateResolver().ResolveAsync(Path.Join(Root, "Saga", "001.cbz"), metadata: null, mutateCache: false);

        Assert.Equal("Saga", result.FolderSeries);
        Assert.Equal(SeriesNameResolutionStep.FolderName, result.WinningStep);
        Assert.Equal("Saga", result.ResolvedSeries);
    }
}
