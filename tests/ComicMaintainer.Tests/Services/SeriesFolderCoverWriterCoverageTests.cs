using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class SeriesFolderCoverWriterCoverageTests : IDisposable
{
    private const string Key = "series-a";
    private static readonly byte[] CoverBytes = [0xFF, 0xD8, 0xFF, 0x01, 0x02];

    private readonly string _root;
    private readonly string _source;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly AppSettings _settings = new() { WriteCoverToSeriesFolder = true };

    public SeriesFolderCoverWriterCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cm-folder-cover-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source-cover.jpg");
        File.WriteAllBytes(_source, CoverBytes);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // ---- WriteAsync guards ---------------------------------------------------

    [Fact]
    public async Task WriteAsync_FeatureDisabledWithoutForce_DoesNothing()
    {
        _settings.WriteCoverToSeriesFolder = false;
        var folder = CreateFolder("Saga");
        SetupFolders(folder);

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.False(File.Exists(Path.Combine(folder, "cover.jpg")));
        _library.Verify(
            l => l.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteAsync_BlankKey_DoesNothing(string key)
    {
        await CreateWriter().WriteAsync(key, _source, "image/jpeg");

        _library.Verify(
            l => l.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing-cover.jpg")]
    public async Task WriteAsync_BlankOrMissingSource_DoesNothing(string source)
    {
        var path = source.Length > 1 ? Path.Combine(_root, source) : source;

        await CreateWriter().WriteAsync(Key, path, "image/jpeg");

        _library.Verify(
            l => l.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("")]
    [InlineData(null)]
    public async Task WriteAsync_UnsupportedContentType_DoesNothing(string? contentType)
    {
        var folder = CreateFolder("Saga");
        SetupFolders(folder);

        await CreateWriter().WriteAsync(Key, _source, contentType!);

        Assert.Empty(Directory.GetFileSystemEntries(folder));
        _library.Verify(
            l => l.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteAsync_NoUsableFolders_DoesNothing()
    {
        _library
            .Setup(l => l.GetFoldersForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = "" },
                new() { Directory = "   " },
            });

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal([_source], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task WriteAsync_LibraryThrows_IsSwallowed()
    {
        _library
            .Setup(l => l.GetFoldersForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("library unavailable"));

        await CreateWriter().WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal([_source], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task WriteAsync_LibraryNotRegistered_IsSwallowed()
    {
        var writer = new SeriesFolderCoverWriter(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options(),
            Mock.Of<ILogger<SeriesFolderCoverWriter>>());

        await writer.WriteAsync(Key, _source, "image/jpeg");

        Assert.Equal([_source], Directory.GetFileSystemEntries(_root));
    }

    // ---- WriteAsync per-folder behaviour ---------------------------------------

    [Fact]
    public async Task WriteAsync_MixedFolders_SkipsMissingAndReplacesOtherCoverFormats()
    {
        var missing = Path.Combine(_root, "Gone");
        var withOldCover = CreateFolder("Saga Vol 1");
        File.WriteAllBytes(Path.Combine(withOldCover, "cover.png"), [1]);
        File.WriteAllText(Path.Combine(withOldCover, "notes.txt"), "keep");
        var trailingSeparator = CreateFolder("Saga Vol 2") + Path.DirectorySeparatorChar;
        SetupFolders(missing, withOldCover, trailingSeparator);

        await CreateWriter().WriteAsync(Key, _source, "  IMAGE/JPEG ");

        Assert.False(Directory.Exists(missing));
        Assert.Equal(CoverBytes, File.ReadAllBytes(Path.Combine(withOldCover, "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(withOldCover, "cover.png")));
        Assert.True(File.Exists(Path.Combine(withOldCover, "notes.txt")));
        Assert.Equal(CoverBytes, File.ReadAllBytes(Path.Combine(trailingSeparator, "cover.jpg")));
    }

    [Fact]
    public async Task WriteAsync_DestinationBlocked_LogsCleansTempAndContinues()
    {
        var blocked = CreateFolder("Blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "cover.webp"));
        var healthy = CreateFolder("Healthy");
        SetupFolders(blocked, healthy);

        await CreateWriter().WriteAsync(Key, _source, "image/webp");

        Assert.True(Directory.Exists(Path.Combine(blocked, "cover.webp")));
        Assert.False(File.Exists(Path.Combine(blocked, "cover.webp.tmp")));
        Assert.Equal(CoverBytes, File.ReadAllBytes(Path.Combine(healthy, "cover.webp")));
    }

    [Fact]
    public async Task WriteAsync_CanceledToken_ThrowsBeforeWritingAnyFolder()
    {
        var folder = CreateFolder("Saga");
        SetupFolders(folder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateWriter().WriteAsync(Key, _source, "image/png", cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    // ---- RemoveAsync -----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RemoveAsync_BlankKey_DoesNothing(string key)
    {
        await CreateWriter().RemoveAsync(key);

        _library.Verify(
            l => l.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_NoFolders_DoesNothing()
    {
        _library
            .Setup(l => l.GetFoldersForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SeriesFolderDto>());

        await CreateWriter().RemoveAsync(Key);

        Assert.True(File.Exists(_source));
    }

    [Fact]
    public async Task RemoveAsync_RemovesEveryManagedCoverAndSkipsMissingFolders()
    {
        var first = CreateFolder("Saga Vol 1");
        File.WriteAllBytes(Path.Combine(first, "cover.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(first, "cover.png"), [2]);
        File.WriteAllBytes(Path.Combine(first, "cover.webp"), [3]);
        File.WriteAllBytes(Path.Combine(first, "cover.gif"), [4]);
        File.WriteAllBytes(Path.Combine(first, "issue-1.cbz"), [5]);
        var second = CreateFolder("Saga Vol 2");
        File.WriteAllBytes(Path.Combine(second, "cover.png"), [6]);
        SetupFolders(Path.Combine(_root, "Gone"), first, second);

        await CreateWriter().RemoveAsync(Key);

        Assert.Equal(
            new[] { "cover.gif", "issue-1.cbz" },
            Directory.GetFiles(first).Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(Directory.GetFileSystemEntries(second));
    }

    [Fact]
    public async Task RemoveAsync_CanceledToken_Throws()
    {
        var folder = CreateFolder("Saga");
        File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), [1]);
        SetupFolders(folder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateWriter().RemoveAsync(Key, cts.Token));

        Assert.True(File.Exists(Path.Combine(folder, "cover.jpg")));
    }

    // ---- helpers -----------------------------------------------------------------

    private SeriesFolderCoverWriter CreateWriter()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _library.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new SeriesFolderCoverWriter(scopeFactory, Options(), Mock.Of<ILogger<SeriesFolderCoverWriter>>());
    }

    private IOptionsMonitor<AppSettings> Options()
    {
        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(_settings);
        return options.Object;
    }

    private void SetupFolders(params string[] folders)
    {
        _library
            .Setup(l => l.GetFoldersForNormalizedKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(folders.Select(f => new SeriesFolderDto { Directory = f, FileCount = 1 }).ToList());
    }

    private string CreateFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
