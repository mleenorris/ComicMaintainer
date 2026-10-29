using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

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

    private void SetupEntry(string variant)
    {
        _coverCache
            .Setup(c => c.GetOrCreateAsync(_file, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileCoverCacheEntry(new byte[] { 5, 6, 7 }, "image/webp", _mtime, variant));
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
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/webp", file.ContentType);
        return controller.Response.Headers.ETag.ToString();
    }

    private static int? StatusOf(IActionResult result) => result switch
    {
        StatusCodeResult s => s.StatusCode,
        FileContentResult => 200,
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
}
