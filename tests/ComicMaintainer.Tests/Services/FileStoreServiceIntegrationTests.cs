using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileStoreServiceIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<string> _testDirectories = new();

    public FileStoreServiceIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_integration_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testDirectories.Add(_testDirectory);
    }

    public void Dispose()
    {
        foreach (var dir in _testDirectories.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private async Task<ComicFileEntity> AddFileToDatabase(
        IServiceProvider serviceProvider,
        string filePath,
        string fileName,
        string directory)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
        var entity = new ComicFileEntity
        {
            FilePath = filePath,
            FileName = fileName,
            Directory = directory,
            FileSize = new FileInfo(filePath).Length,
            LastModified = File.GetLastWriteTime(filePath),
            IsProcessed = false,
            IsDuplicate = false
        };
        dbContext.ComicFiles.Add(entity);
        await dbContext.SaveChangesAsync();
        return entity;
    }

    [Fact]
    public async Task MarkFileProcessedAsync_UpdatesDatabase()
    {
        // Arrange
        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = new TestOptionsMonitor<AppSettings>(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var fileStoreService = new FileStoreService(options, logger, dbContextFactory);

        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");

        // Add file to in-memory store
        await fileStoreService.AddFileAsync(filePath);

        // Add file to database first
        await AddFileToDatabase(serviceProvider, filePath, "test.cbz", _testDirectory);

        // Act - Mark file as processed (renamed AND normalized)
        await fileStoreService.MarkFileRenamedAsync(filePath, true);
        await fileStoreService.MarkFileNormalizedAsync(filePath, true);

        // Assert - Verify in database
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath);
            
            Assert.NotNull(entity);
            Assert.True(entity.IsProcessed, "IsProcessed should be true in database");
            Assert.True(entity.IsRenamed, "IsRenamed should be true in database");
            Assert.True(entity.IsNormalized, "IsNormalized should be true in database");
        }
    }

    [Fact]
    public async Task MarkFileDuplicateAsync_UpdatesDatabase()
    {
        // Arrange
        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = new TestOptionsMonitor<AppSettings>(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var fileStoreService = new FileStoreService(options, logger, dbContextFactory);

        var filePath = Path.Combine(_testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");

        // Add file to in-memory store
        await fileStoreService.AddFileAsync(filePath);

        // Add file to database first
        await AddFileToDatabase(serviceProvider, filePath, "test.cbz", _testDirectory);

        // Act - Mark file as duplicate
        await fileStoreService.MarkFileDuplicateAsync(filePath, true);

        // Assert - Verify in database
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath);
            
            Assert.NotNull(entity);
            Assert.True(entity.IsDuplicate, "IsDuplicate should be true in database");
        }
    }

    [Fact]
    public async Task MarkFileProcessedAsync_HandlesNonExistentFileGracefully()
    {
        // Arrange
        var settings = new AppSettings
        {
            WatchedDirectory = _testDirectory
        };
        var options = new TestOptionsMonitor<AppSettings>(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var fileStoreService = new FileStoreService(options, logger, dbContextFactory);

        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act - Mark non-existent file as processed (should not throw - it's now a no-op)
        #pragma warning disable CS0618 // Type or member is obsolete
        await fileStoreService.MarkFileProcessedAsync(filePath, true);
        #pragma warning restore CS0618 // Type or member is obsolete

        // Assert - Verify nothing was added to database (MarkFileProcessedAsync is now a no-op)
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath);
            
            Assert.Null(entity);
        }
    }

    [Fact]
    public async Task MarkFilesNeedingBackfillAsync_BumpsMetadataVersionAndSurfacesToBackfillQueue()
    {
        // Arrange
        var settings = new AppSettings { WatchedDirectory = _testDirectory };
        var options = new TestOptionsMonitor<AppSettings>(settings);

        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        var fileStoreService = new FileStoreService(options, logger, dbContextFactory);

        var trackedPath = Path.Combine(_testDirectory, "tracked.cbz");
        var duplicatePath = Path.Combine(_testDirectory, "dupe.cbz");
        File.WriteAllText(trackedPath, "content");
        File.WriteAllText(duplicatePath, "content");
        await AddFileToDatabase(serviceProvider, trackedPath, "tracked.cbz", _testDirectory);
        var dupeEntity = await AddFileToDatabase(serviceProvider, duplicatePath, "dupe.cbz", _testDirectory);
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var dupe = await dbContext.ComicFiles.FirstAsync(e => e.FilePath == duplicatePath);
            dupe.IsDuplicate = true;
            await dbContext.SaveChangesAsync();
        }

        // Act
        var marked = await fileStoreService.MarkFilesNeedingBackfillAsync(
            new[] { trackedPath, duplicatePath, "/not/tracked.cbz" });

        // Assert - only the non-duplicate tracked file is flagged.
        Assert.Equal(1, marked);
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstAsync(e => e.FilePath == trackedPath);
            Assert.True(entity.MetadataVersion > entity.WrittenMetadataVersion,
                "MetadataVersion should now exceed WrittenMetadataVersion so the backfill job picks it up.");
            var dupe = await dbContext.ComicFiles.FirstAsync(e => e.FilePath == duplicatePath);
            Assert.Equal(0, dupe.MetadataVersion);
        }

        var queued = await fileStoreService.GetFilesNeedingBackfillAsync(10);
        Assert.Contains(queued, f => f.FilePath == trackedPath);
        Assert.DoesNotContain(queued, f => f.FilePath == duplicatePath);
    }
}
