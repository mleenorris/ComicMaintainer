using System.Security.Claims;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace ComicMaintainer.Tests.Controllers;

public sealed class ComicReaderControllerCoverageTests : IDisposable
{
    private const string OutsideError = "File path is outside the allowed directory";

    private readonly string _root;
    private readonly string _watchDir;
    private readonly string _file;
    private readonly AppSettings _settings;
    private readonly Mock<IComicReaderService> _reader = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IFileCoverCacheService> _coverCache = new();
    private readonly Mock<IReadingProgressService> _progress = new();
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly CancellationTokenSource _cts = new();

    public ComicReaderControllerCoverageTests()
    {
        _root = Path.Join(Path.GetTempPath(), "reader_ctrl_cov_" + Guid.NewGuid().ToString("N"));
        _watchDir = Path.Join(_root, "comics");
        Directory.CreateDirectory(Path.Join(_watchDir, "Saga"));
        _file = Path.Join(_watchDir, "Saga", "Saga 001.cbz");
        File.WriteAllBytes(_file, new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        _settings = new AppSettings { WatchedDirectory = _watchDir };
    }

    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort cleanup */ } catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private ComicReaderController CreateController(ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        if (user is not null)
        {
            http.User = user;
        }

        return new ComicReaderController(
            _reader.Object,
            _fileStore.Object,
            _coverCache.Object,
            NullLogger<ComicReaderController>.Instance,
            new TestOptionsMonitor<AppSettings>(_settings),
            _progress.Object,
            _library.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static ClaimsPrincipal UserWithId(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"));

    private static string? ErrorOf(object? value) => value is null ? null : JObject.FromObject(value)["error"]?.ToString();

    private static void AssertError<T>(object? result, string expected) where T : ObjectResult =>
        Assert.Equal(expected, ErrorOf(Assert.IsType<T>(result).Value));

    private static void Assert500(object? result, string expected)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
        Assert.Equal(expected, ErrorOf(obj.Value));
    }

    private List<ReadingProgress> TrackSavedProgress()
    {
        var saved = new List<ReadingProgress>();
        _progress.Setup(p => p.SaveProgressAsync(It.IsAny<ReadingProgress>(), It.IsAny<CancellationToken>()))
            .Callback<ReadingProgress, CancellationToken>((p, _) => saved.Add(p))
            .Returns(Task.CompletedTask);
        return saved;
    }

    // ---- Path validation ----------------------------------------------------------------

    [Fact]
    public async Task GetComicInfo_PathWithNulCharacter_IsRejectedAsUnsafe()
    {
        var result = await CreateController().GetComicInfo(Path.Join(_watchDir, "Saga\0.cbz"));

        AssertError<BadRequestObjectResult>(result.Result, OutsideError);
        _reader.Verify(r => r.GetPageCountAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetComicInfo_UnconfiguredWatchedDirectory_RejectsEveryPath()
    {
        _settings.WatchedDirectory = string.Empty;

        var result = await CreateController().GetComicInfo(_file);

        AssertError<BadRequestObjectResult>(result.Result, OutsideError);
        _reader.Verify(r => r.GetPageCountAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetComicInfo_DotDotTraversalOutOfWatchedDirectory_IsRejected()
    {
        var outside = Path.Join(_root, "private");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Join(outside, "secret.cbz"), new byte[] { 1 });
        var traversal = Path.Join(_watchDir, "..", "private", "secret.cbz");

        var result = await CreateController().GetComicInfo(traversal);

        AssertError<BadRequestObjectResult>(result.Result, OutsideError);
        _reader.Verify(r => r.GetPageCountAsync(It.IsAny<string>()), Times.Never);
    }

    private static readonly string[] PathEndpoints =
        ["info", "page", "cover", "pages", "adjacent", "mark-read", "save-progress", "get-progress"];

    public static TheoryData<string> Endpoints()
    {
        var data = new TheoryData<string>();
        foreach (var endpoint in PathEndpoints)
        {
            data.Add(endpoint);
        }
        return data;
    }

    public static TheoryData<string, string> UnsafePathCases()
    {
        // A differently-cased directory is only a distinct directory on a case-sensitive file system.
        var kinds = new[] { "sibling-prefix", "other-case", "file-link", "directory-link" }
            .Where(kind => kind != "other-case" || OperatingSystem.IsLinux())
            .ToArray();
        var data = new TheoryData<string, string>();
        foreach (var endpoint in PathEndpoints)
        {
            foreach (var kind in kinds)
            {
                data.Add(endpoint, kind);
            }
        }
        return data;
    }

    private async Task<object?> InvokeAsync(string endpoint, string filePath, ClaimsPrincipal? user = null)
    {
        var controller = CreateController(user);
        return endpoint switch
        {
            "info" => (await controller.GetComicInfo(filePath)).Result,
            "page" => await controller.GetPage(filePath, 1),
            "cover" => await controller.GetCover(filePath, _cts.Token),
            "pages" => (await controller.GetPages(filePath)).Result,
            "adjacent" => (await controller.GetAdjacentFile(filePath, "next")).Result,
            "mark-read" => await controller.MarkAsRead(filePath, _cts.Token),
            "save-progress" => await controller.SaveProgress(filePath, 3, _cts.Token),
            "get-progress" => (await controller.GetProgress(filePath, _cts.Token)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };
    }

    // An existing archive whose path passes a plain string-prefix check against
    // the watched directory (<root>/comics) without being inside it.
    private string CreateUnsafePath(string kind)
    {
        var privateDir = Path.Join(_root, kind == "other-case" ? "COMICS" : "comics-private");
        Directory.CreateDirectory(privateDir);
        var secret = Path.Join(privateDir, "secret.cbz");
        File.WriteAllBytes(secret, new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        switch (kind)
        {
            case "file-link":
                var fileLink = Path.Join(_watchDir, "Saga", "leak.cbz");
                File.CreateSymbolicLink(fileLink, secret);
                return fileLink;
            case "directory-link":
                var directoryLink = Path.Join(_watchDir, "linked");
                Directory.CreateSymbolicLink(directoryLink, privateDir);
                return Path.Join(directoryLink, "secret.cbz");
            default:
                return secret;
        }
    }

    private void SetupServicesFor(string physicalPath, string libraryPath)
    {
        _reader.Setup(r => r.GetPageCountAsync(physicalPath)).ReturnsAsync(24);
        _reader.Setup(r => r.GetPageAsync(physicalPath, 1)).ReturnsAsync((new byte[] { 0xFF, 0xD8 }, "image/jpeg"));
        _reader.Setup(r => r.GetPageNamesAsync(physicalPath)).ReturnsAsync(new List<string> { "001.jpg" });
        _coverCache.Setup(c => c.GetOrCreateAsync(physicalPath, _cts.Token))
            .ReturnsAsync(new FileCoverCacheEntry(new byte[] { 1 }, "image/jpeg", DateTimeOffset.UnixEpoch, "orig"));
        _library.Setup(l => l.GetAdjacentIssueAsync(libraryPath, "next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdjacentIssueResult { Found = true, HasAdjacent = false });
        _fileStore.Setup(f => f.GetReadingProgressAsync(libraryPath, _cts.Token)).ReturnsAsync(7);
    }

    [Theory]
    [MemberData(nameof(UnsafePathCases))]
    public async Task PathEndpoints_RejectPathsThatOnlyLookInsideTheWatchedDirectory(string endpoint, string kind)
    {
        var path = CreateUnsafePath(kind);

        var result = await InvokeAsync(endpoint, path, UserWithId("user-1"));

        AssertError<BadRequestObjectResult>(result, OutsideError);
        _reader.VerifyNoOtherCalls();
        _coverCache.VerifyNoOtherCalls();
        _fileStore.VerifyNoOtherCalls();
        _library.VerifyNoOtherCalls();
        _progress.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task PathEndpoints_AcceptNestedPathsInsideTheWatchedDirectory(string endpoint)
    {
        var nested = Path.Join(_watchDir, "Image", "Saga (2012)", "Saga 002.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        SetupServicesFor(LibraryPathValidator.ResolveRealPath(nested), nested);

        var result = await InvokeAsync(endpoint, nested);

        Assert.True(result is OkResult or OkObjectResult or FileContentResult, $"{endpoint}: {result}");
    }

    [Fact]
    public async Task PathEndpoints_ReadALinkedArchiveThroughItsPhysicalPathButKeepTheRequestedPathForRecords()
    {
        // A link that stays inside the watched directory is allowed; archive
        // access goes to the validated physical file, while library records
        // keep using the path the client asked for.
        var alias = Path.Join(_watchDir, "alias.cbz");
        File.CreateSymbolicLink(alias, _file);
        var physical = LibraryPathValidator.ResolveRealPath(_file);
        SetupServicesFor(physical, alias);
        var saved = TrackSavedProgress();
        var user = UserWithId("user-1");

        var info = JObject.FromObject(Assert.IsType<OkObjectResult>(await InvokeAsync("info", alias)).Value!);
        Assert.Equal("alias.cbz", info["fileName"]!.ToString());
        Assert.Equal(alias, info["filePath"]!.ToString());
        Assert.Equal(24, info["pageCount"]!.Value<int>());
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, Assert.IsType<FileContentResult>(await InvokeAsync("page", alias)).FileContents);
        Assert.Equal(new byte[] { 1 }, Assert.IsType<FileContentResult>(await InvokeAsync("cover", alias)).FileContents);
        Assert.IsType<OkObjectResult>(await InvokeAsync("pages", alias));
        Assert.IsType<OkObjectResult>(await InvokeAsync("adjacent", alias));
        Assert.IsType<OkResult>(await InvokeAsync("mark-read", alias, user));
        Assert.IsType<OkResult>(await InvokeAsync("save-progress", alias, user));
        Assert.IsType<OkObjectResult>(await InvokeAsync("get-progress", alias));

        _reader.Verify(r => r.GetPageNamesAsync(physical), Times.Once);
        _reader.Verify(r => r.GetPageCountAsync(physical), Times.Exactly(3));
        _fileStore.Verify(f => f.MarkFileReadAsync(alias, true, _cts.Token), Times.Once);
        _fileStore.Verify(f => f.SaveReadingProgressAsync(alias, 3, _cts.Token), Times.Once);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, progress => Assert.Equal(alias, progress.ContentId));
        _progress.Verify(p => p.GetProgressAsync("user-1", alias, _cts.Token), Times.Exactly(2));
    }

    // ---- GetComicInfo -------------------------------------------------------------------

    [Fact]
    public async Task GetComicInfo_ExistingArchive_ReturnsNameAndPageCount()
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(24);

        var result = await CreateController().GetComicInfo(_file);

        var json = JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);
        Assert.Equal("Saga 001.cbz", json["fileName"]!.ToString());
        Assert.Equal(_file, json["filePath"]!.ToString());
        Assert.Equal(24, json["pageCount"]!.Value<int>());
    }

    [Fact]
    public async Task GetComicInfo_ReaderFailure_Returns500()
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ThrowsAsync(new InvalidDataException("corrupt"));

        var result = await CreateController().GetComicInfo(_file);

        Assert500(result.Result, "Error reading comic file");
    }

    // ---- GetPage ------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task GetPage_PageBelowOne_ReturnsBadRequest(int page)
    {
        var result = await CreateController().GetPage(_file, page);

        AssertError<BadRequestObjectResult>(result, "Page number must be 1 or greater");
        _reader.Verify(r => r.GetPageAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GetPage_PageNotInArchive_ReturnsNotFound()
    {
        _reader.Setup(r => r.GetPageAsync(_file, 99)).ReturnsAsync(((byte[] Data, string ContentType)?)null);

        var result = await CreateController().GetPage(_file, 99);

        AssertError<NotFoundObjectResult>(result, "Page not found");
    }

    [Fact]
    public async Task GetPage_ExistingPage_StreamsImageWithPublicCacheHeader()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        _reader.Setup(r => r.GetPageAsync(_file, 3)).ReturnsAsync((bytes, "image/jpeg"));
        var controller = CreateController();

        var result = await controller.GetPage(_file, 3);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(bytes, file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("public, max-age=3600", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetPage_ReaderFailure_Returns500()
    {
        _reader.Setup(r => r.GetPageAsync(_file, 1)).ThrowsAsync(new IOException("truncated"));

        var result = await CreateController().GetPage(_file, 1);

        Assert500(result, "Error reading page");
    }

    // ---- GetCover -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetCover_MissingPath_ReturnsBadRequest(string? path)
    {
        var result = await CreateController().GetCover(path!);

        AssertError<BadRequestObjectResult>(result, "File path is required");
    }

    [Fact]
    public async Task GetCover_PathOutsideWatchedDirectory_ReturnsBadRequest()
    {
        var outside = Path.Join(_root, "elsewhere.cbz");
        File.WriteAllBytes(outside, new byte[] { 1 });

        var result = await CreateController().GetCover(outside);

        AssertError<BadRequestObjectResult>(result, OutsideError);
        _coverCache.Verify(c => c.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCover_MissingFile_ReturnsNotFound()
    {
        var result = await CreateController().GetCover(Path.Join(_watchDir, "Saga", "missing.cbz"));

        AssertError<NotFoundObjectResult>(result, "File not found");
        _coverCache.Verify(c => c.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCover_NoExtractableCover_ReturnsNotFound()
    {
        _coverCache.Setup(c => c.GetOrCreateAsync(_file, It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(FileCoverCacheEntry?));

        var result = await CreateController().GetCover(_file);

        AssertError<NotFoundObjectResult>(result, "Cover not available");
    }

    [Fact]
    public async Task GetCover_FreshCover_SetsValidatorsAndHttpDateLastModified()
    {
        var entry = new FileCoverCacheEntry(
            new byte[] { 1, 2, 3 }, "image/jpeg", new DateTimeOffset(2026, 1, 2, 5, 4, 5, TimeSpan.FromHours(2)), "orig");
        _coverCache.Setup(c => c.GetOrCreateAsync(_file, _cts.Token)).ReturnsAsync(entry);
        var controller = CreateController();

        var result = await controller.GetCover(_file, _cts.Token);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
        var headers = controller.Response.Headers;
        Assert.Equal(ComicReaderController.BuildCoverETag(entry), headers.ETag.ToString());
        Assert.Equal("public, max-age=3600", headers.CacheControl.ToString());
        Assert.Equal("Fri, 02 Jan 2026 03:04:05 GMT", headers.LastModified.ToString());
    }

    [Fact]
    public async Task GetCover_NotModified_KeepsCacheHeadersButOmitsBody()
    {
        var entry = new FileCoverCacheEntry(new byte[] { 9 }, "image/webp", DateTimeOffset.UnixEpoch, "w480");
        _coverCache.Setup(c => c.GetOrCreateAsync(_file, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        var etag = ComicReaderController.BuildCoverETag(entry);
        var controller = CreateController();
        controller.Request.Headers.IfNoneMatch = etag;

        var result = await controller.GetCover(_file);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
        Assert.Equal(etag, controller.Response.Headers.ETag.ToString());
        Assert.Equal("public, max-age=3600", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, controller.Response.Headers.LastModified.Count);
    }

    [Fact]
    public async Task GetCover_CacheFailure_Returns500()
    {
        _coverCache.Setup(c => c.GetOrCreateAsync(_file, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("cache dir unavailable"));

        var result = await CreateController().GetCover(_file);

        Assert500(result, "Error reading cover");
    }

    // ---- BuildCoverETag -----------------------------------------------------------------

    [Theory]
    [InlineData(null, "\"10-3-orig\"")]
    [InlineData("", "\"10-3-orig\"")]
    [InlineData("orig", "\"10-3-orig\"")]
    [InlineData("webp-w480_q80.v2", "\"10-3-webp-w480_q80.v2\"")]
    [InlineData("webp w480/q80\"", "\"10-3-webpw480q80\"")]
    [InlineData("wébp", "\"10-3-wbp\"")]
    public void BuildCoverETag_EncodesTicksLengthAndSanitisedVariant(string? variant, string expected)
    {
        var entry = new FileCoverCacheEntry(new byte[3], "image/jpeg", new DateTimeOffset(16, TimeSpan.Zero), variant!);

        Assert.Equal(expected, ComicReaderController.BuildCoverETag(entry));
    }

    // ---- GetPages / GetAdjacentFile -----------------------------------------------------

    [Fact]
    public async Task GetPages_ReturnsPageNamesWithCount()
    {
        _reader.Setup(r => r.GetPageNamesAsync(_file)).ReturnsAsync(new List<string> { "001.jpg", "002.jpg", "003.png" });

        var result = await CreateController().GetPages(_file);

        var json = JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);
        Assert.Equal(new[] { "001.jpg", "002.jpg", "003.png" }, json["pages"]!.ToObject<string[]>());
        Assert.Equal(3, json["count"]!.Value<int>());
    }

    [Fact]
    public async Task GetPages_ReaderFailure_Returns500()
    {
        _reader.Setup(r => r.GetPageNamesAsync(_file)).ThrowsAsync(new InvalidDataException("bad zip"));

        var result = await CreateController().GetPages(_file);

        Assert500(result.Result, "Error reading comic file");
    }

    [Fact]
    public async Task GetAdjacentFile_LibraryFailure_Returns500()
    {
        _library.Setup(l => l.GetAdjacentIssueAsync(_file, "prev", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await CreateController().GetAdjacentFile(_file, "prev");

        Assert500(result.Result, "Error finding adjacent file");
    }

    // ---- MarkAsRead ---------------------------------------------------------------------

    [Fact]
    public async Task MarkAsRead_AnonymousRequest_MarksFileWithoutPerUserProgress()
    {
        var result = await CreateController().MarkAsRead(_file, _cts.Token);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.MarkFileReadAsync(_file, true, _cts.Token), Times.Once);
        _reader.Verify(r => r.GetPageCountAsync(It.IsAny<string>()), Times.Never);
        _progress.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkAsRead_UserWithoutProgress_CreatesCompletedProgressRecord()
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(20);
        _progress.Setup(p => p.GetProgressAsync("user-1", _file, _cts.Token)).ReturnsAsync(default(ReadingProgress?));
        var saved = TrackSavedProgress();
        var before = DateTime.UtcNow;

        var result = await CreateController(UserWithId("user-1")).MarkAsRead(_file, _cts.Token);

        var after = DateTime.UtcNow;
        Assert.IsType<OkResult>(result);
        var progress = Assert.Single(saved);
        Assert.Equal("user-1", progress.UserId);
        Assert.Equal(_file, progress.ContentId);
        Assert.Equal(20, progress.TotalPages);
        Assert.Equal(20, progress.CurrentPage);
        Assert.Equal(100d, progress.PercentComplete);
        Assert.InRange(progress.LastReadAt, before, after);
        Assert.NotNull(progress.CompletedAt);
        Assert.InRange(progress.CompletedAt!.Value, before, after);
        _progress.Verify(p => p.SaveProgressAsync(progress, _cts.Token), Times.Once);
    }

    [Fact]
    public async Task MarkAsRead_UserWithExistingProgress_CompletesSameRecordWithCurrentPageCount()
    {
        var existing = new ReadingProgress { UserId = "user-1", ContentId = _file, CurrentPage = 5, TotalPages = 10 };
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(12);
        _progress.Setup(p => p.GetProgressAsync("user-1", _file, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        var saved = TrackSavedProgress();

        var result = await CreateController(UserWithId("user-1")).MarkAsRead(_file);

        Assert.IsType<OkResult>(result);
        Assert.Same(existing, Assert.Single(saved));
        Assert.Equal(12, existing.TotalPages);
        Assert.Equal(12, existing.CurrentPage);
        Assert.NotNull(existing.CompletedAt);
    }

    [Fact]
    public async Task MarkAsRead_UserWithEmptyIdClaim_SkipsPerUserProgress()
    {
        var result = await CreateController(UserWithId(string.Empty)).MarkAsRead(_file);

        Assert.IsType<OkResult>(result);
        _reader.Verify(r => r.GetPageCountAsync(It.IsAny<string>()), Times.Never);
        _progress.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkAsRead_StoreFailure_Returns500WithoutTouchingUserProgress()
    {
        _fileStore.Setup(f => f.MarkFileReadAsync(_file, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await CreateController(UserWithId("user-1")).MarkAsRead(_file);

        Assert500(result, "Error marking file as read");
        _progress.VerifyNoOtherCalls();
    }

    // ---- SaveProgress -------------------------------------------------------------------

    [Fact]
    public async Task SaveProgress_AnonymousRequest_PersistsFileProgressOnly()
    {
        var result = await CreateController().SaveProgress(_file, 4, _cts.Token);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.SaveReadingProgressAsync(_file, 4, _cts.Token), Times.Once);
        _progress.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(7, 30, 7, false)]
    [InlineData(30, 30, 30, true)]
    [InlineData(50, 30, 30, true)]
    public async Task SaveProgress_User_RecordsPageClampedToArchiveLength(int page, int totalPages, int expectedPage, bool completed)
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(totalPages);
        _progress.Setup(p => p.GetProgressAsync("user-2", _file, _cts.Token)).ReturnsAsync(default(ReadingProgress?));
        var saved = TrackSavedProgress();

        var result = await CreateController(UserWithId("user-2")).SaveProgress(_file, page, _cts.Token);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.SaveReadingProgressAsync(_file, page, _cts.Token), Times.Once);
        var progress = Assert.Single(saved);
        Assert.Equal("user-2", progress.UserId);
        Assert.Equal(_file, progress.ContentId);
        Assert.Equal(totalPages, progress.TotalPages);
        Assert.Equal(expectedPage, progress.CurrentPage);
        Assert.Equal(completed, progress.CompletedAt is not null);
    }

    [Fact]
    public async Task SaveProgress_UserOnEmptyArchive_SkipsPerUserProgress()
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(0);

        var result = await CreateController(UserWithId("user-2")).SaveProgress(_file, 1);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.SaveReadingProgressAsync(_file, 1, It.IsAny<CancellationToken>()), Times.Once);
        _progress.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveProgress_PerUserProgressFailure_DoesNotFailRequest()
    {
        _reader.Setup(r => r.GetPageCountAsync(_file)).ReturnsAsync(10);
        _progress.Setup(p => p.GetProgressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("progress db offline"));

        var result = await CreateController(UserWithId("user-2")).SaveProgress(_file, 3);

        Assert.IsType<OkResult>(result);
        _fileStore.Verify(f => f.SaveReadingProgressAsync(_file, 3, It.IsAny<CancellationToken>()), Times.Once);
        _progress.Verify(p => p.SaveProgressAsync(It.IsAny<ReadingProgress>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveProgress_StoreFailure_Returns500()
    {
        _fileStore.Setup(f => f.SaveReadingProgressAsync(_file, 2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateController(UserWithId("user-2")).SaveProgress(_file, 2);

        Assert500(result, "Error saving reading progress");
        _progress.VerifyNoOtherCalls();
    }

    // ---- GetProgress --------------------------------------------------------------------

    [Fact]
    public async Task GetProgress_ReturnsStoredPage()
    {
        _fileStore.Setup(f => f.GetReadingProgressAsync(_file, _cts.Token)).ReturnsAsync(7);

        var result = await CreateController().GetProgress(_file, _cts.Token);

        var json = JObject.FromObject(Assert.IsType<OkObjectResult>(result.Result).Value!);
        Assert.Equal(7, json["currentPage"]!.Value<int>());
    }

    [Fact]
    public async Task GetProgress_StoreFailure_Returns500()
    {
        _fileStore.Setup(f => f.GetReadingProgressAsync(_file, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db locked"));

        var result = await CreateController().GetProgress(_file);

        Assert500(result.Result, "Error getting reading progress");
    }
}
