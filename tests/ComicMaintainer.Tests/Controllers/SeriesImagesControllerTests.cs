using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>
/// Tests covering <see cref="SeriesImagesController.Get"/>'s fallback chain:
/// cached image → on-disk <c>&lt;series-folder&gt;/cover.&lt;ext&gt;</c> →
/// 404. The folder-cover branch makes a manually-placed cover.jpg
/// authoritative even when the metadata cache is empty or its cached file is
/// missing on disk.
/// </summary>
public class SeriesImagesControllerTests : IDisposable
{
    // Minimal valid PNG (8-byte signature + start of IHDR). The endpoint
    // streams the bytes verbatim; only the controller-side
    // existence/path-traversal checks matter for these tests, but using a real
    // signature keeps tooling that sniffs at the response happy.
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D
    ];

    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly Mock<IExternalSeriesMetadataService> _externalMetadata = new();
    private readonly Mock<ISeriesLibraryService> _seriesLibrary = new();
    private readonly Mock<ILogger<SeriesImagesController>> _logger = new();
    private readonly string _tempRoot;

    public SeriesImagesControllerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cm-series-img-ctl-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private SeriesImagesController CreateController()
    {
        var controller = new SeriesImagesController(
            _cache.Object,
            _imageStore.Object,
            _externalMetadata.Object,
            _seriesLibrary.Object,
            _logger.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task Get_NoCacheRecord_FallsBackToOnDiskFolderCover()
    {
        // Arrange: no cached metadata for the series, but a cover.jpg exists
        // in a folder that the series library says backs this series.
        var folder = Path.Combine(_tempRoot, "series-batman");
        Directory.CreateDirectory(folder);
        var coverPath = Path.Combine(folder, "cover.jpg");
        // Use JPEG magic bytes so the served file is a valid image even
        // though the endpoint itself doesn't validate it.
        await File.WriteAllBytesAsync(coverPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });

        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _seriesLibrary.Setup(s => s.GetFoldersForNormalizedKeyAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = folder, FileCount = 1, TotalSize = 100 }
            });

        var controller = CreateController();

        // Act
        var result = await controller.Get("batman", CancellationToken.None);

        // Assert
        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(coverPath, physical.FileName);
        Assert.Equal("image/jpeg", physical.ContentType);
    }

    [Fact]
    public async Task Get_CachedRecordButFileMissing_FallsBackToFolderCover()
    {
        // Arrange: the cache record claims a cached image exists, but the
        // store returns null (file gone). The endpoint must fall through to
        // the on-disk folder cover instead of 404-ing.
        var folder = Path.Combine(_tempRoot, "series-superman");
        Directory.CreateDirectory(folder);
        var coverPath = Path.Combine(folder, "cover.png");
        await File.WriteAllBytesAsync(coverPath, PngBytes);

        _cache.Setup(c => c.GetAsync("superman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                NormalizedKey = "superman",
                CanonicalTitle = "Superman",
                LocalImageFile = "superman-abcdef.jpg",
                ImageContentType = "image/jpeg",
                ImageStatus = "downloaded"
            });
        // Cache file no longer present on disk.
        _imageStore.Setup(s => s.ResolveAbsolutePath("superman-abcdef.jpg")).Returns((string?)null);

        _seriesLibrary.Setup(s => s.GetFoldersForNormalizedKeyAsync("superman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = folder, FileCount = 1, TotalSize = 100 }
            });

        var controller = CreateController();

        // Act
        var result = await controller.Get("superman", CancellationToken.None);

        // Assert
        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(coverPath, physical.FileName);
        Assert.Equal("image/png", physical.ContentType);
    }

    [Fact]
    public async Task Get_NoCacheAndNoFolderCover_Returns404()
    {
        var folder = Path.Combine(_tempRoot, "series-empty");
        Directory.CreateDirectory(folder); // No cover.* inside.

        _cache.Setup(c => c.GetAsync("empty", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _seriesLibrary.Setup(s => s.GetFoldersForNormalizedKeyAsync("empty", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = folder, FileCount = 0, TotalSize = 0 }
            });

        var controller = CreateController();

        var result = await controller.Get("empty", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_FolderProbeNormalizesPathSegmentsSafely()
    {
        // Defense-in-depth: a folder string containing "." or ".." segments
        // is normalized via Path.GetFullPath before the cover file is
        // resolved. The StartsWith guard ensures the candidate stays inside
        // the normalized folder; combined with the hard-coded "cover" + ext
        // filename, there is no way for a malformed folder string to make the
        // endpoint serve a file outside the (normalized) reported folder.
        var folder = Path.Combine(_tempRoot, "series-norm");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), new byte[] { 0xFF, 0xD8, 0xFF });

        // "/folder/./" should resolve to "/folder/" and serve cover.jpg.
        var dottedFolder = Path.Combine(folder, ".");

        _cache.Setup(c => c.GetAsync("norm", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _seriesLibrary.Setup(s => s.GetFoldersForNormalizedKeyAsync("norm", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = dottedFolder, FileCount = 1, TotalSize = 100 }
            });

        var controller = CreateController();

        var result = await controller.Get("norm", CancellationToken.None);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        var normalizedFolder = Path.GetFullPath(dottedFolder);
        var withSep = normalizedFolder.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedFolder
            : normalizedFolder + Path.DirectorySeparatorChar;
        Assert.StartsWith(withSep, physical.FileName, StringComparison.Ordinal);
        Assert.EndsWith("cover.jpg", physical.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_FolderProbeRejectsFilenameContainingSlash()
    {
        // Defensive: even if a folder string contains separators that try to
        // pin the served file to something outside the cover.<ext> allow-list,
        // the probe only ever opens "cover.jpg|png|webp" — anything else on
        // disk in that folder must not be reachable through this endpoint.
        var folder = Path.Combine(_tempRoot, "series-no-cover");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "secret.png"), PngBytes);

        _cache.Setup(c => c.GetAsync("no-cover", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);
        _seriesLibrary.Setup(s => s.GetFoldersForNormalizedKeyAsync("no-cover", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SeriesFolderDto>
            {
                new() { Directory = folder, FileCount = 1, TotalSize = 100 }
            });

        var controller = CreateController();

        var result = await controller.Get("no-cover", CancellationToken.None);

        // No cover.<ext> in the folder — secret.png must NOT be served.
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_CachedFilePresent_PrefersCacheOverFolderCover()
    {
        // When both a cached image and an on-disk folder cover exist, the
        // cached image wins. This preserves the existing ETag/cache flow for
        // external-image downloads.
        var folder = Path.Combine(_tempRoot, "series-prefer");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), new byte[] { 0xFF, 0xD8, 0xFF });

        var cachedPath = Path.Combine(_tempRoot, "cache-prefer.png");
        await File.WriteAllBytesAsync(cachedPath, PngBytes);

        _cache.Setup(c => c.GetAsync("prefer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesMetadataCacheRecord
            {
                NormalizedKey = "prefer",
                CanonicalTitle = "Prefer",
                LocalImageFile = "cache-prefer.png",
                ImageContentType = "image/png",
                ImageStatus = "downloaded"
            });
        _imageStore.Setup(s => s.ResolveAbsolutePath("cache-prefer.png")).Returns(cachedPath);

        var controller = CreateController();

        var result = await controller.Get("prefer", CancellationToken.None);

        var physical = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(cachedPath, physical.FileName);
        Assert.Equal("image/png", physical.ContentType);
        // The folder-cover fallback path should not have been consulted.
        _seriesLibrary.Verify(
            s => s.GetFoldersForNormalizedKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
