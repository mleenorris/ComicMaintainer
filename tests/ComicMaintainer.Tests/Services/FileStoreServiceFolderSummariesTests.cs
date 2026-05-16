using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileStoreServiceFolderSummariesTests : IDisposable
{
    private readonly FileStoreService _service;
    private readonly string _testDirectory;
    private readonly IServiceProvider _serviceProvider;

    public FileStoreServiceFolderSummariesTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"folder_summary_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        var dbName = $"TestDb_{Guid.NewGuid()}";

        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = new TestOptionsMonitor<AppSettings>(settings);

        var services = new ServiceCollection();
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        _service = new FileStoreService(options, logger, factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); } catch { }
        (_serviceProvider as IDisposable)?.Dispose();
    }

    private async Task AddFileAsync(string relativePath, int size = 100)
    {
        var full = Path.Combine(_testDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, new string('a', size));
        await _service.AddFileAsync(full);
    }

    [Fact]
    public async Task ComputeFolderKey_FileInRoot_ReturnsEmptyString()
    {
        var path = Path.Combine(_testDirectory, "a.cbz");
        var key = FileStoreService.ComputeFolderKey(path, _testDirectory);
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public async Task ComputeFolderKey_FileInSubdir_ReturnsRelativeForwardSlash()
    {
        var path = Path.Combine(_testDirectory, "Batman", "Year One", "a.cbz");
        var key = FileStoreService.ComputeFolderKey(path, _testDirectory);
        Assert.Equal("Batman/Year One", key);
    }

    [Fact]
    public async Task GetFolderSummariesAsync_GroupsByFolderAndComputesAggregates()
    {
        await AddFileAsync(Path.Combine("Batman", "001.cbz"), 100);
        await AddFileAsync(Path.Combine("Batman", "002.cbz"), 200);
        await AddFileAsync(Path.Combine("Superman", "001.cbz"), 50);
        await AddFileAsync("loose.cbz", 25);

        var result = await _service.GetFolderSummariesAsync(limit: 100);

        Assert.Equal(3, result.TotalFolders);
        var batman = result.Folders.Single(f => f.Path == "Batman");
        Assert.Equal(2, batman.FileCount);
        Assert.Equal(300, batman.TotalSize);
        Assert.Equal(2, batman.UnmarkedCount);

        var root = result.Folders.Single(f => f.Path == string.Empty);
        Assert.Equal(1, root.FileCount);
    }

    [Fact]
    public async Task GetFolderSummariesAsync_AppliesSearchFilter()
    {
        await AddFileAsync(Path.Combine("Batman", "001.cbz"));
        await AddFileAsync(Path.Combine("Superman", "001.cbz"));

        var result = await _service.GetFolderSummariesAsync(search: "Batman", limit: 100);

        Assert.Single(result.Folders);
        Assert.Equal("Batman", result.Folders[0].Path);
    }

    [Fact]
    public async Task GetFolderSummariesAsync_SortsByNameDescending()
    {
        await AddFileAsync(Path.Combine("A", "1.cbz"));
        await AddFileAsync(Path.Combine("C", "1.cbz"));
        await AddFileAsync(Path.Combine("B", "1.cbz"));

        var result = await _service.GetFolderSummariesAsync(sort: "name", direction: "desc", limit: 100);

        Assert.Equal(new[] { "C", "B", "A" }, result.Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task GetFolderSummariesAsync_SortsBySize()
    {
        await AddFileAsync(Path.Combine("Small", "1.cbz"), 10);
        await AddFileAsync(Path.Combine("Big", "1.cbz"), 1000);
        await AddFileAsync(Path.Combine("Mid", "1.cbz"), 100);

        var result = await _service.GetFolderSummariesAsync(sort: "size", direction: "asc", limit: 100);

        Assert.Equal(new[] { "Small", "Mid", "Big" }, result.Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task GetFolderSummariesAsync_OffsetLimitPaging()
    {
        for (var i = 0; i < 5; i++)
        {
            await AddFileAsync(Path.Combine($"Folder{i}", "1.cbz"));
        }

        var page = await _service.GetFolderSummariesAsync(offset: 2, limit: 2);

        Assert.Equal(5, page.TotalFolders);
        Assert.Equal(2, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.Equal(2, page.Folders.Count);
        Assert.Equal("Folder2", page.Folders[0].Path);
        Assert.Equal("Folder3", page.Folders[1].Path);
    }

    [Fact]
    public async Task GetFolderSummariesAsync_LimitZero_ReturnsEmptyButTotal()
    {
        await AddFileAsync(Path.Combine("A", "1.cbz"));
        var page = await _service.GetFolderSummariesAsync(limit: 0);
        Assert.Empty(page.Folders);
        Assert.Equal(1, page.TotalFolders);
    }
}
