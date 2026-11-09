using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Reader.Interfaces;
using ComicMaintainer.Core.Reader.Models;
using ComicMaintainer.Core.Utilities;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.WebApi.Controllers;
using ImageMagick;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

/// <summary>The branches of the OPDS controller the real pipeline does not reach naturally.</summary>
public sealed class OpdsControllerTests : IDisposable
{
    private const int Id = 7;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";
    private const string StreamRel = "http://vaemendis.net/opds-pse/stream";

    private readonly string _root = Path.Join(Path.GetTempPath(), "OpdsController-" + Guid.NewGuid().ToString("N"));
    private readonly string _comic;
    private readonly Mock<ISeriesLibraryService> _library = new();
    private readonly Mock<IOverviewService> _overview = new();
    private readonly Mock<IFileStoreService> _fileStore = new();
    private readonly Mock<IComicReaderService> _reader = new();
    private readonly Mock<IFileCoverCacheService> _covers = new();
    private readonly Mock<IReadingProgressService> _progress = new();
    private readonly AppSettings _settings;

    public OpdsControllerTests()
    {
        Directory.CreateDirectory(_root);
        _comic = Path.Join(_root, "Saga 001.cbz");
        File.WriteAllBytes(_comic, [1, 2, 3, 4]);
        _settings = new AppSettings { WatchedDirectory = _root, OpdsEnabled = true };
        _fileStore.Setup(s => s.GetFilePathAsync(Id, It.IsAny<CancellationToken>())).ReturnsAsync(_comic);
        _fileStore.Setup(s => s.GetFileIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> paths, CancellationToken _) =>
                (IReadOnlyDictionary<string, int>)paths.ToDictionary(p => p, _ => Id, StringComparer.Ordinal));
        _progress.Setup(p => p.GetProgressForContentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ReadingProgress>());
        _reader.Setup(r => r.GetPageNamesAsync(It.IsAny<string>())).ReturnsAsync(["p1.jpg", "p2.jpg"]);
    }

    public void Dispose() => TestIO.TryDeleteDirectory(_root);

    private OpdsController CreateController(string? userId = "reader")
    {
        var controller = new OpdsController(
            _library.Object,
            _overview.Object,
            _fileStore.Object,
            _reader.Object,
            _covers.Object,
            _progress.Object,
            new TestOptionsMonitor<AppSettings>(_settings),
            NullLogger<OpdsController>.Instance);
        var identity = userId is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    private static async Task<int> StatusOf(Task<IActionResult> action) => ((IStatusCodeActionResult)await action).StatusCode!.Value;

    private static byte[] Image(MagickFormat format)
    {
        using var image = new MagickImage(MagickColors.Red, 4, 4) { Format = format };
        return image.ToByteArray();
    }

    private void SetupPage(byte[] data, string contentType, params string[] names)
    {
        _reader.Setup(r => r.GetPageNamesAsync(It.IsAny<string>())).ReturnsAsync(names.ToList());
        _reader.Setup(r => r.GetPageAsync(It.IsAny<string>(), 1)).ReturnsAsync((data, contentType));
    }

    [Fact]
    public async Task APageNamedAJpegAndThatIsOneIsPassedThroughUnchanged()
    {
        var jpeg = Image(MagickFormat.Jpeg);
        SetupPage(jpeg, "image/jpeg", "p1.jpg", "p2.jpg");

        var file = Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));

        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(jpeg, file.FileContents);
    }

    [Fact]
    public async Task AWebpPageIsConvertedToJpegBecausePseDoesNotAllowIt()
    {
        var webp = Image(MagickFormat.WebP);
        SetupPage(webp, "image/webp", "p1.webp", "p2.webp");

        var file = Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));

        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(file.FileContents).Format);
    }

    [Fact]
    public async Task InAMixedStreamEveryPageIsAJpeg()
    {
        var png = Image(MagickFormat.Png);
        SetupPage(png, "image/png", "p1.png", "p2.jpg");

        var file = Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));

        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(file.FileContents).Format);
    }

    [Fact]
    public async Task AUniformPngStreamStaysPng()
    {
        var png = Image(MagickFormat.Png);
        SetupPage(png, "image/png", "p1.png", "p2.png");

        var file = Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));

        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(png, file.FileContents);
    }

    [Fact]
    public async Task AnImageThatCannotBeConvertedIsPassedThrough()
    {
        byte[] notAnImage = [1, 2, 3];
        SetupPage(notAnImage, "image/webp", "p1.webp");

        var file = Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));

        Assert.Equal("image/webp", file.ContentType);
        Assert.Equal(notAnImage, file.FileContents);
    }

    [Fact]
    public async Task APageReadTheReaderCannotProduceIsNotFoundAndNotRecorded()
    {
        _reader.Setup(r => r.GetPageAsync(It.IsAny<string>(), 1)).ReturnsAsync(((byte[] Data, string ContentType)?)null);

        Assert.Equal(StatusCodes.Status404NotFound, await StatusOf(CreateController().Page(Id, 0, default)));
        _progress.Verify(p => p.RecordPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnArchiveOverTheLimitsAnswers422FromTheListing()
    {
        _reader.Setup(r => r.GetPageNamesAsync(It.IsAny<string>())).ThrowsAsync(new ArchiveLimitExceededException("too many entries"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, await StatusOf(CreateController().Page(Id, 0, default)));
    }

    [Fact]
    public async Task APageOverTheLimitsAnswers422AndIsNotRecorded()
    {
        _reader.Setup(r => r.GetPageAsync(It.IsAny<string>(), 1)).ThrowsAsync(new ArchiveLimitExceededException("too big"));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, await StatusOf(CreateController().Page(Id, 0, default)));
        _progress.Verify(p => p.RecordPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMissingFileIsNotFound()
    {
        File.Delete(_comic);

        Assert.Equal(StatusCodes.Status404NotFound, await StatusOf(CreateController().Page(Id, 0, default)));
        Assert.Equal(StatusCodes.Status404NotFound, await StatusOf(CreateController().Download(Id, default)));
    }

    [Fact]
    public async Task ReachingTheLastPageMarksTheIssueRead()
    {
        SetupPage([1], "image/jpeg", "p1.jpg");

        await CreateController().Page(Id, 0, default);

        _fileStore.Verify(s => s.MarkFileReadAsync(_comic, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnEarlierPageDoesNotMarkTheIssueRead()
    {
        SetupPage([1], "image/jpeg", "p1.jpg", "p2.jpg");

        await CreateController().Page(Id, 0, default);

        _fileStore.Verify(s => s.MarkFileReadAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ABusyDatabaseDoesNotCostTheClientThePage()
    {
        SetupPage([1], "image/jpeg", "p1.jpg");
        _progress.Setup(p => p.RecordPageAsync("reader", _comic, 1, 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SqliteException("busy", 5));

        Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));
    }

    [Fact]
    public async Task AnUpdateConflictDoesNotCostTheClientThePage()
    {
        SetupPage([1], "image/jpeg", "p1.jpg");
        _progress.Setup(p => p.RecordPageAsync("reader", _comic, 1, 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("conflict"));

        Assert.IsType<FileContentResult>(await CreateController().Page(Id, 0, default));
    }

    [Fact]
    public async Task ARequestWithoutAUserIdStillGetsThePageAndRecordsNothing()
    {
        SetupPage([1], "image/jpeg", "p1.jpg");

        Assert.IsType<FileContentResult>(await CreateController(userId: null).Page(Id, 0, default));
        _progress.Verify(p => p.RecordPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CoverEdgeCases()
    {
        _covers.Setup(c => c.GetValidator(It.IsAny<string>())).Returns((FileCoverValidator?)null);
        Assert.Equal(StatusCodes.Status404NotFound, await StatusOf(CreateController().Cover(Id, default)));

        _covers.Setup(c => c.GetValidator(It.IsAny<string>())).Returns(new FileCoverValidator(4, DateTimeOffset.UtcNow, "orig"));
        _covers.Setup(c => c.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((FileCoverCacheEntry?)null);
        Assert.Equal(StatusCodes.Status404NotFound, await StatusOf(CreateController().Cover(Id, default)));

        _covers.Setup(c => c.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ArchiveLimitExceededException("too big"));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, await StatusOf(CreateController().Cover(Id, default)));
    }

    [Fact]
    public async Task AWebpCoverIsServedAsJpeg()
    {
        var webp = Image(MagickFormat.WebP);
        _covers.Setup(c => c.GetValidator(It.IsAny<string>())).Returns(new FileCoverValidator(4, DateTimeOffset.UtcNow, "webp-w480-q80"));
        _covers.Setup(c => c.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileCoverCacheEntry(webp, "image/webp", DateTimeOffset.UtcNow, 4, "webp-w480-q80"));

        var file = Assert.IsType<FileContentResult>(await CreateController().Cover(Id, default));

        Assert.Equal("image/jpeg", file.ContentType);
        _progress.Verify(p => p.RecordPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnIssueWhoseArchiveIsRefusedStillAppearsWithoutAStream()
    {
        _reader.Setup(r => r.GetPageNamesAsync(It.IsAny<string>())).ThrowsAsync(new ArchiveLimitExceededException("too many entries"));
        _library.Setup(l => l.GetSeriesIssuesAsync("saga", null, 1, OpdsController.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult { Id = "saga", Title = "Saga", Page = 1, TotalPages = 1, IssueCount = 1, Issues = [new SeriesIssueDto { FilePath = _comic, FileName = "Saga 001.cbz", Issue = "1", Title = "Dawn" }] });

        var content = Assert.IsType<ContentResult>(await CreateController().Series("saga"));

        var entry = XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry").Single();
        Assert.Equal("Saga #1 - Dawn", entry.Element(Atom + "title")!.Value);
        Assert.DoesNotContain(entry.Elements(Atom + "link"), l => (string?)l.Attribute("rel") == StreamRel);
    }

    [Fact]
    public async Task AnIssueWithNoNumberFallsBackToItsFileName()
    {
        _library.Setup(l => l.GetSeriesIssuesAsync("saga", null, 1, OpdsController.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult { Id = "saga", Title = "Saga", Page = 1, TotalPages = 1, IssueCount = 1, Issues = [new SeriesIssueDto { FilePath = _comic, FileName = "Saga 001.cbz" }] });

        var content = Assert.IsType<ContentResult>(await CreateController().Series("saga"));

        Assert.Equal("Saga 001", XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry").Single().Element(Atom + "title")!.Value);
    }

    [Fact]
    public async Task AnIssueTheStoreNoLongerKnowsIsLeftOut()
    {
        _fileStore.Setup(s => s.GetFileIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
        _library.Setup(l => l.GetSeriesIssuesAsync("saga", null, 1, OpdsController.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult { Id = "saga", Title = "Saga", Page = 1, TotalPages = 1, IssueCount = 1, Issues = [new SeriesIssueDto { FilePath = _comic, FileName = "Saga 001.cbz" }] });

        var content = Assert.IsType<ContentResult>(await CreateController().Series("saga"));

        Assert.Empty(XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry"));
    }

    [Fact]
    public async Task AFileOutsideTheLibraryGetsNoStreamEvenIfAskedFor()
    {
        var outside = Path.Join(Path.GetDirectoryName(_root)!, "elsewhere.cbz");
        _library.Setup(l => l.GetSeriesIssuesAsync("saga", null, 1, OpdsController.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesIssuesResult { Id = "saga", Title = "Saga", Page = 1, TotalPages = 1, IssueCount = 1, Issues = [new SeriesIssueDto { FilePath = outside, FileName = "elsewhere.cbz" }] });

        var content = Assert.IsType<ContentResult>(await CreateController().Series("saga"));

        var entry = XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry").Single();
        Assert.DoesNotContain(entry.Elements(Atom + "link"), l => (string?)l.Attribute("rel") == StreamRel);
        _reader.Verify(r => r.GetPageNamesAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ContinueReadingSkipsACardWhoseIssueIsGoneAndWorksWithoutAUser()
    {
        _overview.Setup(o => o.GetOverviewAsync("reader", It.IsAny<CancellationToken>())).ReturnsAsync(new OverviewResult
        {
            ContinueReading = [new OverviewSeriesCard { Id = "gone", Title = "Gone", ResumeFilePath = "/nope.cbz" }],
        });
        _library.Setup(l => l.GetSeriesIssuesForIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, SeriesIssuesResult>());

        var signedIn = Assert.IsType<ContentResult>(await CreateController().ContinueReading(default));
        var anonymous = Assert.IsType<ContentResult>(await CreateController(userId: null).ContinueReading(default));

        Assert.Empty(XDocument.Parse(signedIn.Content!).Root!.Elements(Atom + "entry"));
        Assert.Empty(XDocument.Parse(anonymous.Content!).Root!.Elements(Atom + "entry"));
        _overview.Verify(o => o.GetOverviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OverviewRowsAreEmptyWithoutAUser()
    {
        var content = Assert.IsType<ContentResult>(await CreateController(userId: null).SeriesUpdates(default));

        Assert.Empty(XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry"));
    }

    [Fact]
    public async Task ASeriesWithoutACoverHasNoImageLinks()
    {
        _library.Setup(l => l.GetSeriesSummariesAsync(null, null, 1, OpdsController.PageSize, "name", "asc", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesSummaryResult { Page = 1, TotalPages = 1, TotalSeries = 1, Series = [new SeriesSummaryDto { Id = "saga", Title = "Saga", Synopsis = "A story", CoverFilePath = string.Empty }] });

        var content = Assert.IsType<ContentResult>(await CreateController().AllSeries());

        var entry = XDocument.Parse(content.Content!).Root!.Elements(Atom + "entry").Single();
        Assert.Equal("A story", entry.Element(Atom + "content")!.Value);
        Assert.DoesNotContain(entry.Elements(Atom + "link"), l => ((string?)l.Attribute("rel"))?.Contains("image", StringComparison.Ordinal) == true);
    }
}
