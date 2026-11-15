using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public class ComicReaderControllerCoverTests : IDisposable
{
    private readonly string _watchDir;
    private readonly string _file;
    private readonly Mock<IFileCoverCacheService> _coverCache = new();
    private readonly AppSettings _settings;
    private readonly DateTimeOffset _mtime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public ComicReaderControllerCoverTests()
    {
        _watchDir = Path.Join(Path.GetTempPath(), "cover_ctrl_" + Guid.NewGuid());
        Directory.CreateDirectory(_watchDir);
        _file = Path.Join(_watchDir, "a.cbz");
        File.WriteAllBytes(_file, new byte[] { 1, 2, 3 });
        _settings = new AppSettings { WatchedDirectory = _watchDir };
        SetupEntry("webp-w480-q80");
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ignore */ }
    }

    private void SetupEntry(string variant, long sourceLength = 3)
    {
        _coverCache
            .Setup(c => c.GetValidator(_file))
            .Returns(new FileCoverValidator(sourceLength, _mtime, variant));
        _coverCache
            .Setup(c => c.OpenAsync(_file, FileCoverFormat.Configured, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FileCoverStream(new MemoryStream(new byte[] { 5, 6, 7 }), "image/webp", new FileCoverValidator(sourceLength, _mtime, variant)));
    }

    private ComicReaderController BuildController(string? ifNoneMatch = null)
    {
        var settings = new Mock<IOptionsMonitor<AppSettings>>();
        settings.Setup(s => s.CurrentValue).Returns(_settings);
        var controller = new ComicReaderController(
            Mock.Of<IComicReaderService>(),
            Mock.Of<IFileStoreService>(),
            _coverCache.Object,
            Mock.Of<ILogger<ComicReaderController>>(),
            settings.Object,
            Mock.Of<IReadingProgressService>(),
            Mock.Of<ISeriesLibraryService>());
        var http = new DefaultHttpContext();
        if (ifNoneMatch is not null)
        {
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private async Task<string> GetEtagAsync()
    {
        var controller = BuildController();
        var result = await controller.GetCover(_file);
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/webp", file.ContentType);
        return controller.Response.Headers.ETag.ToString();
    }

    private static int? StatusOf(IActionResult result) => result switch
    {
        StatusCodeResult s => s.StatusCode,
        FileStreamResult => 200,
        ObjectResult o => o.StatusCode,
        _ => null,
    };

    [Fact]
    public async Task GetCover_ReturnsImageWithVariantETag()
    {
        var etag = await GetEtagAsync();
        Assert.StartsWith("\"", etag);
        Assert.Contains("webp-w480-q80", etag);
    }

    [Fact]
    public async Task GetCover_MatchingIfNoneMatch_Returns304()
    {
        var etag = await GetEtagAsync();
        var result = await BuildController(etag).GetCover(_file);
        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_WeakIfNoneMatch_Returns304()
    {
        var etag = await GetEtagAsync();
        var result = await BuildController("W/" + etag).GetCover(_file);
        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_IfNoneMatchList_Returns304()
    {
        var etag = await GetEtagAsync();
        var result = await BuildController("\"other\", W/\"x\", " + etag).GetCover(_file);
        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_IfNoneMatchStar_Returns304()
    {
        var result = await BuildController("*").GetCover(_file);
        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_NonMatchingIfNoneMatch_Returns200()
    {
        var result = await BuildController("\"nope\"").GetCover(_file);
        Assert.Equal(200, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_VariantChange_ChangesETagAndMisses304()
    {
        var oldEtag = await GetEtagAsync();
        SetupEntry("webp-w240-q80");

        var newEtag = await GetEtagAsync();
        Assert.NotEqual(oldEtag, newEtag);

        var result = await BuildController(oldEtag).GetCover(_file);
        Assert.Equal(200, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_SourceLengthChange_ChangesETagAndMisses304()
    {
        var oldEtag = await GetEtagAsync();
        SetupEntry("webp-w480-q80", sourceLength: 4096);

        var result = await BuildController(oldEtag).GetCover(_file);

        Assert.Equal(200, StatusOf(result));
    }

    [Fact]
    public async Task GetCover_Revalidation_IsAnsweredFromTheValidatorWithoutLoadingTheCover()
    {
        var etag = ComicReaderController.BuildCoverETag(new FileCoverValidator(3, _mtime, "webp-w480-q80"));

        var result = await BuildController(etag).GetCover(_file);

        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
        _coverCache.Verify(c => c.GetValidator(_file), Times.Once);
        _coverCache.Verify(c => c.OpenAsync(It.IsAny<string>(), It.IsAny<FileCoverFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCover_ETagOfTheBytesServedMatchesTheValidatorOfTheSameVersion()
    {
        var etag = await GetEtagAsync();

        Assert.Equal(ComicReaderController.BuildCoverETag(new FileCoverValidator(3, _mtime, "webp-w480-q80")), etag);
    }

    [Fact]
    public async Task GetCover_FileChangedWhileGenerating_LabelsTheBytesWithTheVersionTheyCameFrom()
    {
        // The stat said one version; by the time the cover was generated the file
        // had been rewritten. The response must carry the newer version's tag, or
        // the browser would keep the new bytes under a tag that still matches the
        // old file.
        var newer = _mtime.AddMinutes(5);
        _coverCache.Setup(c => c.GetValidator(_file)).Returns(new FileCoverValidator(3, _mtime, "orig"));
        _coverCache.Setup(c => c.OpenAsync(_file, FileCoverFormat.Configured, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileCoverStream(new MemoryStream(new byte[] { 1 }), "image/png", new FileCoverValidator(10, newer, "orig")));
        var controller = BuildController();

        var result = await controller.GetCover(_file);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(ComicReaderController.BuildCoverETag(new FileCoverValidator(10, newer, "orig")), controller.Response.Headers.ETag.ToString());
        Assert.Equal(newer.ToString("R"), controller.Response.Headers.LastModified.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCover_IsOnlyCacheableByTheRequestingBrowser(bool revalidate)
    {
        // Covers are behind authentication: a shared cache (reverse proxy, CDN)
        // must never store them, on a 200 or on a 304.
        var etag = revalidate ? await GetEtagAsync() : null;
        var controller = BuildController(etag);

        var result = await controller.GetCover(_file);

        Assert.Equal(revalidate ? StatusCodes.Status304NotModified : 200, StatusOf(result));
        Assert.Equal("private, max-age=3600", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetCover_FileThatCannotBeStatted_ReturnsNotFoundWithoutLoadingTheCover()
    {
        _coverCache.Setup(c => c.GetValidator(_file)).Returns(default(FileCoverValidator?));

        var result = await BuildController().GetCover(_file);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("File not found", JObject.FromObject(notFound.Value!)["error"]!.ToString());
        _coverCache.Verify(c => c.OpenAsync(It.IsAny<string>(), It.IsAny<FileCoverFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCover_ArchiveOverTheReadLimits_Returns422WithTheReason()
    {
        _coverCache.Setup(c => c.OpenAsync(_file, FileCoverFormat.Configured, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArchiveLimitExceededException("Archive entry 'p1.png' is 2 GiB uncompressed, over the 100 MiB limit."));

        var result = await BuildController().GetCover(_file);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(
            "Archive entry 'p1.png' is 2 GiB uncompressed, over the 100 MiB limit.",
            JObject.FromObject(refused.Value!)["error"]!.ToString());
    }
}
