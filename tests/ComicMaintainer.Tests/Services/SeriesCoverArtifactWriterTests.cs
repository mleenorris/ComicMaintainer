using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesCoverArtifactWriterTests
{
    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesFolderCoverWriter> _folderWriter = new();
    private readonly Mock<ISeriesArchiveCoverWriter> _archiveWriter = new();

    private SeriesCoverArtifactWriter CreateWriter() => new(_cache.Object, _folderWriter.Object, _archiveWriter.Object);

    [Fact]
    public async Task WriteAsync_WritesTheFolderSidecarsAndTheFirstArchive()
    {
        using var cts = new CancellationTokenSource();

        await CreateWriter().WriteAsync("batman", "/cache/batman.png", "image/png", force: false, cts.Token);

        _folderWriter.Verify(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", false, cts.Token), Times.Once);
        _archiveWriter.Verify(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", false, cts.Token), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_RemovesTheFolderSidecarsAndTheFirstArchiveCover()
    {
        using var cts = new CancellationTokenSource();

        await CreateWriter().RemoveAsync("batman", cts.Token);

        _folderWriter.Verify(w => w.RemoveAsync("batman", cts.Token), Times.Once);
        _archiveWriter.Verify(w => w.RemoveAsync("batman", cts.Token), Times.Once);
    }

    [Fact]
    public async Task ReapplyAsync_CachedImage_ForcesBothWritersAndWaitsForThem()
    {
        _cache
            .Setup(c => c.GetCoverSourceAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesCoverSource("batman", "/cache/batman.png", "image/png"));
        var archiveEmbedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishArchiveEmbed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _archiveWriter
            .Setup(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", true, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                archiveEmbedStarted.SetResult();
                await finishArchiveEmbed.Task;
            });

        var reapply = CreateWriter().ReapplyAsync("Batman");
        await archiveEmbedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The job reporting progress must not finish before the archive has been rewritten.
        Assert.False(reapply.IsCompleted);
        _folderWriter.Verify(w => w.WriteAsync("batman", "/cache/batman.png", "image/png", true, It.IsAny<CancellationToken>()), Times.Once);

        finishArchiveEmbed.SetResult();
        Assert.True(await reapply.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReapplyAsync_NoCachedImage_ReturnsFalseAndWritesNothing()
    {
        _cache
            .Setup(c => c.GetCoverSourceAsync("Batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(SeriesCoverSource));

        Assert.False(await CreateWriter().ReapplyAsync("Batman"));

        _folderWriter.Verify(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _archiveWriter.Verify(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
