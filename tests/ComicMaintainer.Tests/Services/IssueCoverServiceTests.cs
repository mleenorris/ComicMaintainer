using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// The covers the picker shows. ComicVine refuses to show its images on other sites, so the
/// server fetches the stored cover of an issue it listed and serves it to the page.
/// </summary>
public sealed class IssueCoverServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;
    private readonly Mock<ISeriesImageStore> _images = new();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
        await using var db = new ComicMaintainerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        db.ComicVineIssues.AddRange(
            new ComicVineIssueEntity { Id = 101, VolumeId = 1, IssueNumber = "1", ListedUtc = DateTime.UtcNow, CoverUrl = "https://cv.example/101.jpg", CoverThumbUrl = "https://cv.example/101-m.jpg" },
            new ComicVineIssueEntity { Id = 102, VolumeId = 1, IssueNumber = "2", ListedUtc = DateTime.UtcNow, CoverUrl = "https://cv.example/102.jpg" },
            new ComicVineIssueEntity { Id = 103, VolumeId = 1, IssueNumber = "3", ListedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        _images.Setup(i => i.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => (System.Text.Encoding.UTF8.GetBytes(url), "image/jpeg"));
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    private IssueCoverService CreateService() => new(
        new Factory(_options),
        Mock.Of<IFileStoreService>(),
        _images.Object,
        Mock.Of<IIssueCoverWriter>(),
        NullLogger<IssueCoverService>.Instance);

    private static string Text(IssueCoverImage? image) => System.Text.Encoding.UTF8.GetString(image!.Bytes!);

    [Fact]
    public async Task GetCandidateImageAsync_FetchesTheSmallCopy_OrTheFullOneWhenThereIsNoSmallCopy()
    {
        var service = CreateService();

        Assert.Equal("https://cv.example/101-m.jpg", Text(await service.GetCandidateImageAsync(101, thumbnail: true)));
        Assert.Equal("https://cv.example/101.jpg", Text(await service.GetCandidateImageAsync(101, thumbnail: false)));
        Assert.Equal("https://cv.example/102.jpg", Text(await service.GetCandidateImageAsync(102, thumbnail: true)));
        Assert.Equal("image/jpeg", (await service.GetCandidateImageAsync(102, thumbnail: true))!.ContentType);
    }

    [Fact]
    public async Task GetCandidateImageAsync_WithoutAStoredCover_IsNull()
    {
        var service = CreateService();

        Assert.Null(await service.GetCandidateImageAsync(103, thumbnail: true));
        Assert.Null(await service.GetCandidateImageAsync(999, thumbnail: true));
        _images.Verify(i => i.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCandidateImageAsync_AFailedDownload_SaysWhy()
    {
        _images.Setup(i => i.FetchImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Response status code does not indicate success: 403 (Forbidden)."));

        var image = await CreateService().GetCandidateImageAsync(101, thumbnail: true);

        Assert.Null(image!.Bytes);
        Assert.Equal("The cover could not be downloaded: Response status code does not indicate success: 403 (Forbidden).", image.Error);
    }

    private sealed class Factory(DbContextOptions<ComicMaintainerDbContext> options) : IDbContextFactory<ComicMaintainerDbContext>
    {
        public ComicMaintainerDbContext CreateDbContext() => new(options);
    }
}
