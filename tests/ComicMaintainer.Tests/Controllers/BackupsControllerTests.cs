using System.Reflection;
using System.Text;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.WebApi.Authorization;
using ComicMaintainer.WebApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Controllers;

public class BackupsControllerTests
{
    private const string ValidName = "comicmaintainer-20261020-040000.db";

    private readonly Mock<IDatabaseBackupService> _backups = new();
    private readonly BackupsController _controller;

    public BackupsControllerTests()
    {
        _controller = new BackupsController(_backups.Object, NullLogger<BackupsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static object? GetProp(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value);

    private static DatabaseRestoreResult RestoreResult(string source) =>
        new(source, new DatabaseBackupInfo("comicmaintainer-20261020-040500-prerestore.db", 10, DateTime.UtcNow));

    [Fact]
    public void Controller_IsAdminOnlyIncludingReads()
    {
        var attribute = typeof(BackupsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AuthorizationPolicies.CanAdminister, attribute!.Policy);
        Assert.Null(typeof(BackupsController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(
            typeof(BackupsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public void List_ReturnsBackupsAndPendingFlag()
    {
        _backups.Setup(b => b.ListBackups()).Returns(new[] { new DatabaseBackupInfo(ValidName, 42, DateTime.UtcNow) });
        _backups.SetupGet(b => b.IsRestorePending).Returns(true);

        var ok = Assert.IsType<OkObjectResult>(_controller.List().Result);

        Assert.Equal(true, GetProp(ok.Value!, "restore_pending"));
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(GetProp(ok.Value!, "backups"));
        var first = list.Cast<object>().Single();
        Assert.Equal(ValidName, GetProp(first, "name"));
        Assert.Equal(42L, GetProp(first, "size_bytes"));
    }

    [Fact]
    public async Task Create_ReturnsCreatedBackup()
    {
        _backups.Setup(b => b.CreateBackupAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseBackupInfo(ValidName, 7, DateTime.UtcNow));

        var ok = Assert.IsType<OkObjectResult>((await _controller.Create(CancellationToken.None)).Result);

        Assert.Equal(ValidName, GetProp(ok.Value!, "name"));
    }

    [Fact]
    public async Task Create_MissingDatabaseIsConflict()
    {
        _backups.Setup(b => b.CreateBackupAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database file does not exist."));

        Assert.IsType<ConflictObjectResult>((await _controller.Create(CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData("../comicmaintainer.db")]
    [InlineData("..%2Fcomicmaintainer.db")]
    [InlineData("secrets.txt")]
    public async Task NameEndpoints_RejectInvalidNamesWithoutCallingService(string name)
    {
        Assert.IsType<BadRequestObjectResult>(_controller.Download(name));
        Assert.IsType<BadRequestObjectResult>(_controller.Delete(name));
        Assert.IsType<BadRequestObjectResult>((await _controller.Restore(name, CancellationToken.None)).Result);

        _backups.Verify(b => b.OpenBackup(It.IsAny<string>()), Times.Never);
        _backups.Verify(b => b.DeleteBackup(It.IsAny<string>()), Times.Never);
        _backups.Verify(b => b.StageRestoreAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Download_StreamsBackup()
    {
        using var backup = new MemoryStream(Encoding.ASCII.GetBytes("SQLite format 3\0"));
        _backups.Setup(b => b.OpenBackup(ValidName)).Returns(backup);

        var file = Assert.IsType<FileStreamResult>(_controller.Download(ValidName));

        Assert.Equal(ValidName, file.FileDownloadName);
        Assert.Equal("application/vnd.sqlite3", file.ContentType);
    }

    [Fact]
    public void Download_MissingIsNotFound()
    {
        _backups.Setup(b => b.OpenBackup(ValidName)).Returns(default(Stream?));

        Assert.IsType<NotFoundObjectResult>(_controller.Download(ValidName));
    }

    [Fact]
    public void Delete_ReturnsNotFoundOrOk()
    {
        _backups.Setup(b => b.DeleteBackup(ValidName)).Returns(true);
        Assert.IsType<OkObjectResult>(_controller.Delete(ValidName));

        _backups.Setup(b => b.DeleteBackup(ValidName)).Returns(false);
        Assert.IsType<NotFoundObjectResult>(_controller.Delete(ValidName));
    }

    [Fact]
    public async Task Restore_ReportsRestartRequired()
    {
        _backups.Setup(b => b.StageRestoreAsync(ValidName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RestoreResult(ValidName));

        var ok = Assert.IsType<OkObjectResult>((await _controller.Restore(ValidName, CancellationToken.None)).Result);

        Assert.Equal(true, GetProp(ok.Value!, "restart_required"));
        Assert.Equal(ValidName, GetProp(ok.Value!, "source"));
    }

    [Fact]
    public async Task Restore_MapsServiceErrors()
    {
        _backups.Setup(b => b.StageRestoreAsync(ValidName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());
        Assert.IsType<NotFoundObjectResult>((await _controller.Restore(ValidName, CancellationToken.None)).Result);

        _backups.Setup(b => b.StageRestoreAsync(ValidName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("bad"));
        Assert.IsType<BadRequestObjectResult>((await _controller.Restore(ValidName, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task UploadRestore_RequiresFile()
    {
        Assert.IsType<BadRequestObjectResult>((await _controller.UploadRestore(null, CancellationToken.None)).Result);

        using var stream = new MemoryStream();
        var empty = new FormFile(stream, 0, 0, "file", "empty.db");
        Assert.IsType<BadRequestObjectResult>((await _controller.UploadRestore(empty, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task UploadRestore_StagesValidFileAndRejectsInvalid()
    {
        var bytes = Encoding.ASCII.GetBytes("SQLite format 3\0payload");
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "backup.db");

        _backups.Setup(b => b.StageRestoreFromStreamAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RestoreResult("upload"));
        var ok = Assert.IsType<OkObjectResult>((await _controller.UploadRestore(file, CancellationToken.None)).Result);
        Assert.Equal(true, GetProp(ok.Value!, "restart_required"));

        _backups.Setup(b => b.StageRestoreFromStreamAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("The file is not a SQLite database."));
        var bad = Assert.IsType<BadRequestObjectResult>((await _controller.UploadRestore(file, CancellationToken.None)).Result);
        Assert.Equal("The file is not a SQLite database.", GetProp(bad.Value!, "error"));
    }

    [Fact]
    public void UploadRestore_HasSizeLimit()
    {
        var method = typeof(BackupsController).GetMethod(nameof(BackupsController.UploadRestore))!;
        var limit = method.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(limit);
    }

    [Fact]
    public void CancelPendingRestore_ReturnsNotFoundWhenNothingPending()
    {
        _backups.Setup(b => b.CancelPendingRestore()).Returns(false);
        Assert.IsType<NotFoundObjectResult>(_controller.CancelPendingRestore());

        _backups.Setup(b => b.CancelPendingRestore()).Returns(true);
        Assert.IsType<OkObjectResult>(_controller.CancelPendingRestore());
    }
}
