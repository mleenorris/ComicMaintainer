using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ComicMaintainer.Tests.Services;

public class MetadataAuditJobHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_UserEditedLockedSeriesDiffersOnDisk_RecordsDiskDriftFinding()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var filePath = "/library/Series/c001.cbz";
        var fileStore = new Mock<IFileStoreService>();
        fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ComicFile { FilePath = filePath } });
        fileStore.Setup(f => f.GetFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile
            {
                FilePath = filePath,
                Metadata = new ComicMetadata
                {
                    Series = "DB Series",
                    Issue = "1",
                    IsUserEdited = true,
                    UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Series
                }
            });

        var processor = new Mock<IComicProcessorService>();
        processor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = "Disk Series", Issue = "1" });

        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(filePath, It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "Disk Series" });

        var handler = new MetadataAuditJobHandler(
            fileStore.Object,
            processor.Object,
            factory,
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            new Mock<ILogger<MetadataAuditJobHandler>>().Object,
            seriesNameResolver: resolver.Object);

        await handler.ExecuteAsync(null, CancellationToken.None);

        await using var verifyDb = await factory.CreateDbContextAsync();
        var finding = await verifyDb.MetadataAuditFindings.SingleAsync();
        Assert.Equal(MetadataAuditFindingType.DiskDriftedFromDb.ToString(), finding.FindingType);
        Assert.Equal("Disk Series", finding.ActualSeries);
        Assert.Equal("DB Series", finding.ExpectedSeries);
    }

    [Fact]
    public async Task ExecuteAsync_SeriesDiffersOnlyByComparisonNormalization_DoesNotFlagMismatch()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(opt => opt.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var filePath = "/library/Series/c001.cbz";
        var fileStore = new Mock<IFileStoreService>();
        fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ComicFile { FilePath = filePath } });
        fileStore.Setup(f => f.GetFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicFile { FilePath = filePath });

        // Disk tag carries a trailing "(*)" suffix and surrounding whitespace that
        // NormalizeSeriesName(forComparison: true) strips; the rest of the site treats
        // this as already-normalized, so the audit must not flag it as a mismatch.
        var processor = new Mock<IComicProcessorService>();
        processor.Setup(p => p.GetMetadataAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComicMetadata { Series = " My Series (*) ", Issue = "1" });

        var resolver = new Mock<ISeriesNameResolver>();
        resolver.Setup(r => r.ResolveAsync(filePath, It.IsAny<ComicMetadata?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesNameResolution { ResolvedSeries = "My Series" });

        var handler = new MetadataAuditJobHandler(
            fileStore.Object,
            processor.Object,
            factory,
            new TestOptionsMonitor<AppSettings>(new AppSettings()),
            new Mock<ILogger<MetadataAuditJobHandler>>().Object,
            seriesNameResolver: resolver.Object);

        await handler.ExecuteAsync(null, CancellationToken.None);

        await using var verifyDb = await factory.CreateDbContextAsync();
        Assert.False(await verifyDb.MetadataAuditFindings
            .AnyAsync(f => f.FindingType == MetadataAuditFindingType.SeriesMismatch.ToString()));
    }
}
