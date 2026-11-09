using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Recording a user's metadata edit on a real SQLite row: what the first edit stores, what a later edit changes,
/// the field locks, and when the version that tells the processor to rewrite the archive moves.
/// </summary>
public sealed class FileStoreServiceUserMetadataEditsTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _library = Path.Join(Path.GetTempPath(), $"fss_useredit_{Guid.NewGuid():N}");
    private readonly Mock<IEventBroadcaster> _broadcaster = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    private string FilePath => Path.Join(_library, "One Piece", "One Piece - Chapter 0001.cbz");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_library);
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
        TestIO.TryDeleteDirectory(_library);
    }

    [Fact]
    public async Task RecordUserMetadataEdit_NullValues_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.RecordUserMetadataEditAsync(FilePath, null!, ComicMetadataFieldFlags.Series, pendingWrite: true));
    }

    [Fact]
    public async Task RecordUserMetadataEdit_UntrackedFile_ThrowsAndNamesTheFile()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordUserMetadataEditAsync(FilePath, new ComicMetadata(), ComicMetadataFieldFlags.Series, pendingWrite: true));

        Assert.Contains(FilePath, ex.Message, StringComparison.Ordinal);
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Never);
    }

    // The first stored metadata is the whole snapshot, so the series library sees the file's other fields too.
    [Fact]
    public async Task RecordUserMetadataEdit_FirstEdit_StoresTheWholeSnapshotAndLocksOnlyTheEditedFields()
    {
        await SeedRowAsync(metadata: null);
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();
        var values = new ComicMetadata { Series = "One Piece", Title = "Romance Dawn", Year = 1999, Authors = ["Oda"] };
        var before = service.ChangeVersion;

        await service.RecordUserMetadataEditAsync(FilePath, values, ComicMetadataFieldFlags.Title, pendingWrite: true);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.ComicFiles.AsNoTracking().SingleAsync();
        Assert.Equal("One Piece", row.Metadata!.Series);
        Assert.Equal("Romance Dawn", row.Metadata.Title);
        Assert.Equal(1999, row.Metadata.Year);
        Assert.Equal(new[] { "Oda" }, row.Metadata.Authors);
        Assert.True(row.Metadata.IsUserEdited);
        Assert.Equal((long)ComicMetadataFieldFlags.Title, row.Metadata.UserLockedFieldsMask);
        Assert.Equal(1, row.MetadataVersion);
        Assert.Equal(FileMetadataSource.UserEdit.ToString(), row.MetadataSource);
        Assert.NotNull(row.LastDbEditAt);
        Assert.True(row.UpdatedAt > T0, "UpdatedAt should move to the edit's time");

        // The file list and series views are served from the in-memory copy, not from the row.
        var memory = Assert.Single(await service.GetAllFilesAsync());
        Assert.Equal("Romance Dawn", memory.Metadata!.Title);
        Assert.Equal(FileMetadataSource.UserEdit, memory.MetadataSource);
        Assert.Equal(1, memory.MetadataVersion);
        Assert.True(service.ChangeVersion > before, "Derived views must see the edit");
        _broadcaster.Verify(b => b.BroadcastFileListUpdateAsync(), Times.Once);

        // The snapshot is a copy: the edit's bookkeeping must not be written onto the caller's object.
        Assert.False(values.IsUserEdited);
        Assert.Equal(0, values.UserLockedFieldsMask);
    }

    [Fact]
    public async Task RecordUserMetadataEdit_LaterEdit_ChangesOnlyTheEditedFieldsAndAddsTheirLocks()
    {
        await SeedRowAsync(new ComicMetadata
        {
            Series = "One Piece",
            Title = "Old title",
            Publisher = "Shueisha",
            Tags = ["Adventure"],
            UserLockedFieldsMask = (long)ComicMetadataFieldFlags.Publisher,
        });
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.RecordUserMetadataEditAsync(
            FilePath,
            new ComicMetadata { Series = "Ignored", Title = "New title", Publisher = "Ignored", Tags = ["Ignored"] },
            ComicMetadataFieldFlags.Title,
            pendingWrite: true);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = (await db.ComicFiles.AsNoTracking().SingleAsync()).Metadata!;
        Assert.Equal("One Piece", stored.Series);
        Assert.Equal("New title", stored.Title);
        Assert.Equal("Shueisha", stored.Publisher);
        Assert.Equal(new[] { "Adventure" }, stored.Tags);
        Assert.Equal((long)(ComicMetadataFieldFlags.Publisher | ComicMetadataFieldFlags.Title), stored.UserLockedFieldsMask);
    }

    // Without a pending archive write there is nothing the version would tell the processor to do.
    [Fact]
    public async Task RecordUserMetadataEdit_WithoutAPendingWrite_LeavesTheVersionButStillLocks()
    {
        await SeedRowAsync(new ComicMetadata { Series = "One Piece" }, version: 4);
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.RecordUserMetadataEditAsync(FilePath, new ComicMetadata { Title = "T" }, ComicMetadataFieldFlags.Title, pendingWrite: false);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.ComicFiles.AsNoTracking().SingleAsync();
        Assert.Equal(4, row.MetadataVersion);
        Assert.Equal("T", row.Metadata!.Title);
        Assert.Equal((long)ComicMetadataFieldFlags.Title, row.Metadata.UserLockedFieldsMask);
    }

    [Fact]
    public async Task RecordUserMetadataEdit_AtTheHighestVersion_DoesNotOverflow()
    {
        await SeedRowAsync(new ComicMetadata(), version: int.MaxValue);
        var service = CreateService();
        await service.InitializeFromDatabaseAsync();

        await service.RecordUserMetadataEditAsync(FilePath, new ComicMetadata { Title = "T" }, ComicMetadataFieldFlags.Title, pendingWrite: true);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(int.MaxValue, (await db.ComicFiles.AsNoTracking().SingleAsync()).MetadataVersion);
    }

    private FileStoreService CreateService() =>
        TestServices.CreateFileStore(
            new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = _library, DuplicateDirectory = string.Empty }),
            NullLogger<FileStoreService>.Instance,
            _factory,
            _broadcaster.Object);

    private async Task SeedRowAsync(ComicMetadata? metadata, int version = 0)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.ComicFiles.Add(new ComicFileEntity
        {
            FilePath = FilePath,
            FileName = Path.GetFileName(FilePath),
            Directory = Path.GetDirectoryName(FilePath)!,
            FileSize = 10,
            LastModified = T0,
            CreatedAt = T0,
            UpdatedAt = T0,
            Metadata = metadata,
            MetadataVersion = version,
            MetadataSource = FileMetadataSource.Scanned.ToString(),
        });
        await db.SaveChangesAsync();
    }
}
