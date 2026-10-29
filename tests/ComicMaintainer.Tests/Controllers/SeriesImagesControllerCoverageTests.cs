using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public sealed class SeriesImagesControllerCoverageTests : IDisposable
{
    private const long MaxUploadBytes = 20L * 1024 * 1024;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly Mock<ISeriesMetadataCacheService> _cache = new();
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<ISeriesImageStore> _imageStore = new();
    private readonly Mock<IExternalSeriesMetadataService> _external = new();
    private readonly Mock<IComicProcessorService> _processor = new();
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();

    public SeriesImagesControllerCoverageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "series_images_cov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private SeriesImagesController CreateController(string? ifNoneMatch = null)
    {
        var http = new DefaultHttpContext();
        if (ifNoneMatch is not null)
        {
            http.Request.Headers[HeaderNames.IfNoneMatch] = ifNoneMatch;
        }

        return new SeriesImagesController(
            _cache.Object,
            _library.Object,
            _imageStore.Object,
            _external.Object,
            _processor.Object,
            NullLogger<SeriesImagesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static SeriesMetadataCacheRecord ImageRecord(string? file = "batman-abc.png", string? contentType = "image/png", string? status = "downloaded") =>
        new()
        {
            NormalizedKey = "batman",
            CanonicalTitle = "Batman",
            LocalImageFile = file,
            ImageContentType = contentType,
            ImageStatus = status
        };

    private string WriteImage(string name = "batman-abc.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, PngSignature);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        return path;
    }

    private static string ExpectedEtag(string path)
    {
        var info = new FileInfo(path);
        return $"W/\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
    }

    private static string? ErrorOf(object? value) => value is null ? null : JObject.FromObject(value)["error"]?.ToString();

    private static object? Prop(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    private void SetupLibrary(params SeriesLibraryDto[] series) =>
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), -1,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesLibraryResult { Series = series.ToList() });

    private sealed class JobCapture
    {
        public string? OperationName;
        public List<string>? Items;
        public Func<string, CancellationToken, Task<bool>>? ItemOperation;
        public string? FailureMessage;
        public CancellationToken Token;
    }

    private JobCapture CaptureJob(Guid jobId)
    {
        var capture = new JobCapture();
        _processor.Setup(p => p.RunCustomBatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<Func<string, CancellationToken, Task<bool>>>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, Func<string, CancellationToken, Task<bool>>, string, Func<CancellationToken, Task>?, CancellationToken>(
                (name, items, op, failure, _, token) =>
                {
                    capture.OperationName = name;
                    capture.Items = items.ToList();
                    capture.ItemOperation = op;
                    capture.FailureMessage = failure;
                    capture.Token = token;
                })
            .ReturnsAsync(jobId);
        return capture;
    }

    // ---- Get ----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Get_BlankKey_ReturnsBadRequest(string key)
    {
        var result = await CreateController().Get(key, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Series key is required", bad.Value);
        _cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Get_UnknownSeries_ReturnsNotFound()
    {
        var result = await CreateController().Get("batman", _cts.Token);

        Assert.IsType<NotFoundResult>(result);
        _cache.Verify(c => c.GetAsync("batman", _cts.Token), Times.Once);
        _imageStore.Verify(s => s.ResolveAbsolutePath(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "image/png", "downloaded")]
    [InlineData("", "image/png", "downloaded")]
    [InlineData("batman-abc.png", null, "downloaded")]
    [InlineData("batman-abc.png", "", "user")]
    [InlineData("batman-abc.png", "image/png", "failed")]
    [InlineData("batman-abc.png", "image/png", null)]
    public async Task Get_RecordWithoutServableImage_ReturnsNotFound(string? file, string? contentType, string? status)
    {
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageRecord(file, contentType, status));

        var result = await CreateController().Get("batman", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _imageStore.Verify(s => s.ResolveAbsolutePath(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Get_ImageFileOutsideStore_ReturnsNotFound()
    {
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>())).ReturnsAsync(ImageRecord());
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-abc.png")).Returns((string?)null);

        var result = await CreateController().Get("batman", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _imageStore.Verify(s => s.ResolveAbsolutePath("batman-abc.png"), Times.Once);
    }

    [Theory]
    [InlineData("downloaded")]
    [InlineData("USER")]
    public async Task Get_CachedImage_StreamsPhysicalFileWithValidators(string status)
    {
        var path = WriteImage();
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageRecord(status: status));
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-abc.png")).Returns(path);
        var controller = CreateController();

        var result = await controller.Get("batman", CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(path, file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.False(file.EnableRangeProcessing);
        Assert.Equal(ExpectedEtag(path), controller.Response.Headers[HeaderNames.ETag].ToString());
        Assert.Equal("private, max-age=300", controller.Response.Headers[HeaderNames.CacheControl].ToString());
    }

    [Fact]
    public async Task Get_MatchingIfNoneMatch_Returns304WithEtag()
    {
        var path = WriteImage();
        var etag = ExpectedEtag(path);
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>())).ReturnsAsync(ImageRecord());
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-abc.png")).Returns(path);
        var controller = CreateController(ifNoneMatch: etag);

        var result = await controller.Get("batman", CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
        Assert.Equal(etag, controller.Response.Headers[HeaderNames.ETag].ToString());
    }

    [Fact]
    public async Task Get_IfNoneMatchWithoutWeakPrefix_StillReturns304()
    {
        var path = WriteImage();
        var etag = ExpectedEtag(path);
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>())).ReturnsAsync(ImageRecord());
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-abc.png")).Returns(path);
        var controller = CreateController(ifNoneMatch: etag["W/".Length..]);

        var result = await controller.Get("batman", CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Get_StaleIfNoneMatch_ReturnsFreshFile()
    {
        var path = WriteImage();
        _cache.Setup(c => c.GetAsync("batman", It.IsAny<CancellationToken>())).ReturnsAsync(ImageRecord());
        _imageStore.Setup(s => s.ResolveAbsolutePath("batman-abc.png")).Returns(path);
        var controller = CreateController(ifNoneMatch: "\"0-0\"");

        var result = await controller.Get("batman", CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(ExpectedEtag(path), controller.Response.Headers[HeaderNames.ETag].ToString());
    }

    // ---- Upload -------------------------------------------------------------------------

    private static FormFile PngUpload(string? contentType = "image/png")
    {
        var stream = new MemoryStream(PngSignature);
        var file = new FormFile(stream, 0, stream.Length, "file", "cover.png") { Headers = new HeaderDictionary() };
        if (contentType is not null)
        {
            file.ContentType = contentType;
        }
        return file;
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Upload_BlankTitle_ReturnsBadRequest(string title)
    {
        var result = await CreateController().Upload(title, PngUpload(), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Series title is required", bad.Value);
    }

    [Fact]
    public async Task Upload_MissingFile_ReturnsBadRequest()
    {
        var result = await CreateController().Upload("Batman", null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File is required", bad.Value);
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsBadRequest()
    {
        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "cover.png");

        var result = await CreateController().Upload("Batman", empty, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File is required", bad.Value);
    }

    [Fact]
    public async Task Upload_OversizedFile_ReturnsBadRequestWithoutReadingIt()
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(MaxUploadBytes + 1);

        var result = await CreateController().Upload("Batman", file.Object, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("File too large", bad.Value);
        file.Verify(f => f.OpenReadStream(), Times.Never);
        _cache.Verify(c => c.SetUserImageAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_ValidImage_PersistsUploadedBytesAsUserImage()
    {
        var record = ImageRecord(status: "user");
        byte[]? uploaded = null;
        _cache.Setup(c => c.SetUserImageAsync("Batman", It.IsAny<Stream>(), "image/png", _cts.Token))
            .Callback<string, Stream, string, CancellationToken>((_, stream, _, _) =>
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                uploaded = copy.ToArray();
            })
            .ReturnsAsync(record);

        var result = await CreateController().Upload("Batman", PngUpload(), _cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(record, ok.Value);
        Assert.Equal(PngSignature, uploaded);
    }

    [Fact]
    public async Task Upload_MissingContentType_PassesEmptyContentType()
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(PngSignature.Length);
        file.SetupGet(f => f.ContentType).Returns((string)null!);
        file.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(PngSignature));
        var record = ImageRecord(status: "user");
        _cache.Setup(c => c.SetUserImageAsync("Batman", It.IsAny<Stream>(), string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await CreateController().Upload("Batman", file.Object, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(record, ok.Value);
        _cache.Verify(c => c.SetUserImageAsync("Batman", It.IsAny<Stream>(), string.Empty, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_ValidationFailure_ReturnsBadRequestWithMessage()
    {
        _cache.Setup(c => c.SetUserImageAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unsupported image type"));

        var result = await CreateController().Upload("Batman", PngUpload("text/plain"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Unsupported image type", ErrorOf(bad.Value));
    }

    [Fact]
    public async Task Upload_UnexpectedFailure_Returns500()
    {
        _cache.Setup(c => c.SetUserImageAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateController().Upload("Batman", PngUpload(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error saving series image", obj.Value);
    }

    [Fact]
    public async Task Upload_StreamWithAsynchronousDispose_IsDisposedBeforeResponding()
    {
        var stream = new AsyncDisposeTrackingStream(PngSignature);
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(PngSignature.Length);
        file.SetupGet(f => f.ContentType).Returns("image/png");
        file.Setup(f => f.OpenReadStream()).Returns(stream);
        var record = ImageRecord(status: "user");
        _cache.Setup(c => c.SetUserImageAsync("Batman", stream, "image/png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var result = await CreateController().Upload("Batman", file.Object, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(record, ok.Value);
        Assert.True(stream.DisposedAsync);
    }

    /// <summary>Upload stream whose asynchronous dispose genuinely yields before completing.</summary>
    private sealed class AsyncDisposeTrackingStream : MemoryStream
    {
        public AsyncDisposeTrackingStream(byte[] data) : base(data)
        {
        }

        public bool DisposedAsync { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await Task.Yield();
            await base.DisposeAsync();
            DisposedAsync = true;
        }
    }

    // ---- SearchCandidates ---------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchCandidates_BlankQuery_ReturnsBadRequest(string query)
    {
        var result = await CreateController().SearchCandidates(query, 10, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Query is required", bad.Value);
        _external.Verify(e => e.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100, 25)]
    public async Task SearchCandidates_ReturnsOnlyResultsWithImages(int requestedLimit, int expectedLimit)
    {
        _external.Setup(e => e.SearchSeriesAsync("Batman", expectedLimit, _cts.Token))
            .ReturnsAsync(new List<ExternalSeriesMetadata>
            {
                new() { CanonicalTitle = "Batman", Source = "ComicVine", ImageUrl = "https://img/batman.jpg", ThumbnailUrl = "https://img/batman_t.jpg" },
                new() { CanonicalTitle = "Batman Beyond", Source = "ComicVine", ImageUrl = null },
                new() { CanonicalTitle = "Batman Adventures", Source = "MangaDex", ImageUrl = "   " },
                new() { CanonicalTitle = "Batman (2016)", Source = "AniList", ImageUrl = "https://img/b2016.jpg" }
            });

        var result = await CreateController().SearchCandidates("Batman", requestedLimit, _cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var json = JObject.FromObject(ok.Value!);
        Assert.Equal("Batman", json["query"]!.ToString());
        var candidates = Assert.IsType<JArray>(json["candidates"]);
        Assert.Equal(2, candidates.Count);
        Assert.Equal("ComicVine", candidates[0]["source"]!.ToString());
        Assert.Equal("Batman", candidates[0]["canonical_title"]!.ToString());
        Assert.Equal("https://img/batman.jpg", candidates[0]["image_url"]!.ToString());
        Assert.Equal("https://img/batman_t.jpg", candidates[0]["thumbnail_url"]!.ToString());
        Assert.Equal("Batman (2016)", candidates[1]["canonical_title"]!.ToString());
        Assert.Equal(JTokenType.Null, candidates[1]["thumbnail_url"]!.Type);
        _external.Verify(e => e.SearchSeriesAsync("Batman", expectedLimit, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task SearchCandidates_ProviderFailure_Returns500()
    {
        _external.Setup(e => e.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        var result = await CreateController().SearchCandidates("Batman", 10, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error searching provider image candidates", obj.Value);
    }

    [Fact]
    public async Task SearchCandidates_CallerCancelled_LetsTheCancellationThrough()
    {
        _cts.Cancel();
        _external.Setup(e => e.SearchSeriesAsync("Batman", 10, _cts.Token))
            .ThrowsAsync(new OperationCanceledException(_cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateController().SearchCandidates("Batman", 10, _cts.Token));
    }

    [Fact]
    public async Task SearchCandidates_CancellationTheCallerDidNotAskFor_Returns500()
    {
        _external.Setup(e => e.SearchSeriesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timed out"));

        var result = await CreateController().SearchCandidates("Batman", 10, _cts.Token);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error searching provider image candidates", obj.Value);
    }

    // ---- ApplyFromProvider --------------------------------------------------------------

    [Fact]
    public async Task ApplyFromProvider_BlankTitle_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyFromProvider(
            " ", new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "https://img/a.jpg" }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Series title is required", bad.Value);
    }

    [Fact]
    public async Task ApplyFromProvider_MissingRequest_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyFromProvider("Batman", null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Image URL is required", bad.Value);
    }

    [Fact]
    public async Task ApplyFromProvider_BlankImageUrl_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyFromProvider(
            "Batman", new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "  " }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Image URL is required", bad.Value);
        _cache.Verify(c => c.ApplyExternalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyFromProvider_DownloadsImageForSeries()
    {
        var record = ImageRecord();
        _cache.Setup(c => c.ApplyExternalImageAsync("Batman", "https://img/a.jpg", "ComicVine", _cts.Token))
            .ReturnsAsync(record);

        var result = await CreateController().ApplyFromProvider(
            "Batman",
            new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "https://img/a.jpg", Source = "ComicVine" },
            _cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task ApplyFromProvider_UserUploadedImageInPlace_ReturnsConflict()
    {
        _cache.Setup(c => c.ApplyExternalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Series has a User-Uploaded image; clear it first"));

        var result = await CreateController().ApplyFromProvider(
            "Batman", new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "https://img/a.jpg" }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("Series has a User-Uploaded image; clear it first", ErrorOf(conflict.Value));
    }

    [Fact]
    public async Task ApplyFromProvider_RejectedUrl_ReturnsBadRequestWithMessage()
    {
        _cache.Setup(c => c.ApplyExternalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("URL resolves to a private address"));

        var result = await CreateController().ApplyFromProvider(
            "Batman", new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "http://10.0.0.1/a.jpg" }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("URL resolves to a private address", ErrorOf(bad.Value));
    }

    [Fact]
    public async Task ApplyFromProvider_UnexpectedFailure_Returns500()
    {
        _cache.Setup(c => c.ApplyExternalImageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var result = await CreateController().ApplyFromProvider(
            "Batman", new SeriesImagesController.ApplyFromProviderRequest { ImageUrl = "https://img/a.jpg" }, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error applying provider image", obj.Value);
    }

    // ---- ApplyCurrentToAll / ApplyCurrentToSelected -------------------------------------

    [Fact]
    public async Task ApplyCurrentToAll_UsesDisplayTitleFallbackAndRunsInlineEmbedPerSeries()
    {
        SetupLibrary(
            new SeriesLibraryDto { Title = "batman folder", CanonicalTitle = "Batman", HasExternalImage = true },
            new SeriesLibraryDto { Title = "BATMAN", CanonicalTitle = "", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Saga", CanonicalTitle = "   ", HasExternalImage = true },
            new SeriesLibraryDto { Title = "", CanonicalTitle = "", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Flash", CanonicalTitle = "Flash", HasExternalImage = false });
        var jobId = Guid.NewGuid();
        var job = CaptureJob(jobId);
        _cache.Setup(c => c.ReapplyImageArtifactsAsync("Saga", true, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().ApplyCurrentToAll(_cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(jobId.ToString(), Prop(ok.Value!, "job_id"));
        Assert.Equal(2, Prop(ok.Value!, "total_items"));
        Assert.Equal(3, Prop(ok.Value!, "totalSeries"));
        Assert.Equal(1, Prop(ok.Value!, "skippedSeries"));
        Assert.Equal("ApplySeriesCoversAllJob", job.OperationName);
        Assert.Equal(new[] { "Batman", "Saga" }, job.Items);
        Assert.Equal("Cover embed failed", job.FailureMessage);
        Assert.Equal(_cts.Token, job.Token);

        // The per-item operation must force an inline (awaited) archive embed so
        // the job's progress tracks real work.
        using var itemCts = new CancellationTokenSource();
        Assert.True(await job.ItemOperation!("Saga", itemCts.Token));
        _cache.Verify(c => c.ReapplyImageArtifactsAsync("Saga", true, itemCts.Token), Times.Once);
    }

    [Fact]
    public async Task ApplyCurrentToAll_LibraryFailure_Returns500()
    {
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController().ApplyCurrentToAll(CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error updating series covers", obj.Value);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_NullRequest_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyCurrentToSelected(null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("No series specified", bad.Value);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_NullSeries_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest { Series = null! }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("No series specified", bad.Value);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_OnlyBlankTitles_ReturnsBadRequest()
    {
        var result = await CreateController().ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest { Series = new() { "", "  " } }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _library.Verify(l => l.GetSeriesAsync(
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_MatchesByTitleAndAliasAndDedupes()
    {
        SetupLibrary(
            new SeriesLibraryDto { Title = "Spider-Man (2018)", CanonicalTitle = "The Amazing Spider-Man", HasExternalImage = true },
            new SeriesLibraryDto { Title = "Shingeki", CanonicalTitle = "Attack on Titan", Aliases = new() { "Shingeki no Kyojin" }, HasExternalImage = true },
            new SeriesLibraryDto { Title = "Flash", CanonicalTitle = "Flash", Aliases = new() { "Speedster" }, HasExternalImage = false });
        var jobId = Guid.NewGuid();
        var job = CaptureJob(jobId);

        var result = await CreateController().ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest
            {
                Series = new()
                {
                    "spider-man (2018)", "attack on titan", "SHINGEKI NO KYOJIN", "Shingeki no Kyojin", "Speedster", "Unknown", " "
                }
            },
            _cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(jobId.ToString(), Prop(ok.Value!, "job_id"));
        Assert.Equal(3, Prop(ok.Value!, "total_items"));
        Assert.Equal(5, Prop(ok.Value!, "totalSeries"));
        Assert.Equal(2, Prop(ok.Value!, "skippedSeries"));
        Assert.Equal("ApplySeriesCoversSelectedJob", job.OperationName);
        Assert.Equal(new[] { "spider-man (2018)", "attack on titan", "SHINGEKI NO KYOJIN" }, job.Items);
        Assert.Equal("Cover embed failed", job.FailureMessage);
        Assert.Equal(_cts.Token, job.Token);
    }

    [Fact]
    public async Task ApplyCurrentToSelected_LibraryFailure_Returns500()
    {
        _library.Setup(l => l.GetSeriesAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await CreateController().ApplyCurrentToSelected(
            new SeriesImagesController.ApplyCurrentSelectedRequest { Series = new() { "Batman" } }, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error updating series covers", obj.Value);
    }

    // ---- Clear --------------------------------------------------------------------------

    [Fact]
    public async Task Clear_BlankKey_ReturnsBadRequest()
    {
        var result = await CreateController().Clear(" ", CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Series key is required", bad.Value);
        _cache.Verify(c => c.ClearImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clear_UnknownSeries_ReturnsNotFound()
    {
        _cache.Setup(c => c.ClearImageAsync("batman", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesMetadataCacheRecord?)null);

        var result = await CreateController().Clear("batman", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Clear_ExistingSeries_ReturnsUpdatedRecord()
    {
        var record = ImageRecord(file: null, contentType: null, status: null);
        _cache.Setup(c => c.ClearImageAsync("batman", _cts.Token)).ReturnsAsync(record);

        var result = await CreateController().Clear("batman", _cts.Token);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(record, ok.Value);
    }

    [Fact]
    public async Task Clear_UnexpectedFailure_Returns500()
    {
        _cache.Setup(c => c.ClearImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));

        var result = await CreateController().Clear("batman", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
        Assert.Equal("Error clearing series image", obj.Value);
    }
}
