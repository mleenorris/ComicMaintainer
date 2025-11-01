using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class FileStoreServiceIntegrationTests
{
    [Fact]
    public async Task MarkFileProcessedAsync_UpdatesDatabase()
    {
        // Arrange
        var testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_integration_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(testDirectory);

        var settings = new AppSettings
        {
            WatchedDirectory = testDirectory
        };
        var options = Options.Create(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var fileStoreService = new FileStoreService(options, logger, serviceProvider);

        var filePath = Path.Combine(testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");

        // Add file to in-memory store
        await fileStoreService.AddFileAsync(filePath);

        // Add file to database first
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = new ComicFileEntity
            {
                FilePath = filePath,
                FileName = "test.cbz",
                Directory = testDirectory,
                FileSize = new FileInfo(filePath).Length,
                LastModified = File.GetLastWriteTime(filePath),
                IsProcessed = false,
                IsDuplicate = false
            };
            dbContext.ComicFiles.Add(entity);
            await dbContext.SaveChangesAsync();
        }

        // Act - Mark file as processed
        await fileStoreService.MarkFileProcessedAsync(filePath, true);

        // Assert - Verify in database
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath);
            
            Assert.NotNull(entity);
            Assert.True(entity.IsProcessed, "IsProcessed should be true in database");
        }

        // Cleanup
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }

    [Fact]
    public async Task MarkFileDuplicateAsync_UpdatesDatabase()
    {
        // Arrange
        var testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_integration_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(testDirectory);

        var settings = new AppSettings
        {
            WatchedDirectory = testDirectory
        };
        var options = Options.Create(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var fileStoreService = new FileStoreService(options, logger, serviceProvider);

        var filePath = Path.Combine(testDirectory, "test.cbz");
        File.WriteAllText(filePath, "test content");

        // Add file to in-memory store
        await fileStoreService.AddFileAsync(filePath);

        // Add file to database first
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = new ComicFileEntity
            {
                FilePath = filePath,
                FileName = "test.cbz",
                Directory = testDirectory,
                FileSize = new FileInfo(filePath).Length,
                LastModified = File.GetLastWriteTime(filePath),
                IsProcessed = false,
                IsDuplicate = false
            };
            dbContext.ComicFiles.Add(entity);
            await dbContext.SaveChangesAsync();
        }

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

        // Cleanup
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }

    [Fact]
    public async Task MarkFileProcessedAsync_HandlesNonExistentFileGracefully()
    {
        // Arrange
        var testDirectory = Path.Combine(Path.GetTempPath(), $"filestore_integration_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(testDirectory);

        var settings = new AppSettings
        {
            WatchedDirectory = testDirectory
        };
        var options = Options.Create(settings);

        // Setup in-memory database
        var services = new ServiceCollection();
        var dbName = $"TestDb_{Guid.NewGuid()}";
        services.AddDbContext<ComicMaintainerDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var logger = new Mock<ILogger<FileStoreService>>().Object;
        var fileStoreService = new FileStoreService(options, logger, serviceProvider);

        var filePath = Path.Combine(testDirectory, "nonexistent.cbz");

        // Act - Mark non-existent file as processed (should not throw)
        await fileStoreService.MarkFileProcessedAsync(filePath, true);

        // Assert - Verify nothing was added to database
        using (var scope = serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
            var entity = await dbContext.ComicFiles.FirstOrDefaultAsync(e => e.FilePath == filePath);
            
            Assert.Null(entity);
        }

        // Cleanup
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }
}
