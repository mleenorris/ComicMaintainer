using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Tests.Data;

public class ComicMaintainerDbContextFactoryTests
{
    [Fact]
    public void CreateDbContext_WithEmptyArgs_CreatesContext()
    {
        // Arrange
        var factory = new ComicMaintainerDbContextFactory();
        var args = Array.Empty<string>();

        // Act
        using var context = factory.CreateDbContext(args);

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Database);
    }

    [Fact]
    public void CreateDbContext_WithArgs_CreatesContext()
    {
        // Arrange
        var factory = new ComicMaintainerDbContextFactory();
        var args = new[] { "arg1", "arg2" };

        // Act
        using var context = factory.CreateDbContext(args);

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Database);
    }

    [Fact]
    public void CreateDbContext_UsesSqlite()
    {
        // Arrange
        var factory = new ComicMaintainerDbContextFactory();
        var args = Array.Empty<string>();

        // Act
        using var context = factory.CreateDbContext(args);

        // Assert
        Assert.True(context.Database.IsSqlite());
    }

    [Fact]
    public void CreateDbContext_CreatesMultipleContexts()
    {
        // Arrange
        var factory = new ComicMaintainerDbContextFactory();
        var args = Array.Empty<string>();

        // Act
        using var context1 = factory.CreateDbContext(args);
        using var context2 = factory.CreateDbContext(args);

        // Assert
        Assert.NotNull(context1);
        Assert.NotNull(context2);
        Assert.NotSame(context1, context2);
    }
}

public class FileReadStatusEntityTests
{
    [Fact]
    public void FileReadStatusEntity_DefaultConstructor_SetsDefaults()
    {
        // Act
        var entity = new FileReadStatusEntity();

        // Assert
        Assert.NotNull(entity);
        Assert.Equal(0, entity.Id);
        Assert.Equal(string.Empty, entity.FilePath);
        Assert.False(entity.IsRead);
        Assert.Equal(1, entity.CurrentPage);
        Assert.Null(entity.LastReadDate);
        Assert.True(entity.CreatedAt > DateTime.MinValue);
        Assert.True(entity.UpdatedAt > DateTime.MinValue);
    }

    [Fact]
    public void FileReadStatusEntity_AllPropertiesCanBeSet()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var entity = new FileReadStatusEntity
        {
            Id = 5,
            FilePath = "/test/path/comic.cbz",
            IsRead = true,
            CurrentPage = 10,
            LastReadDate = now,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now
        };

        // Assert
        Assert.Equal(5, entity.Id);
        Assert.Equal("/test/path/comic.cbz", entity.FilePath);
        Assert.True(entity.IsRead);
        Assert.Equal(10, entity.CurrentPage);
        Assert.Equal(now, entity.LastReadDate);
        Assert.Equal(now.AddDays(-1), entity.CreatedAt);
        Assert.Equal(now, entity.UpdatedAt);
    }

    [Fact]
    public void FileReadStatusEntity_CurrentPage_DefaultsToOne()
    {
        // Act
        var entity = new FileReadStatusEntity();

        // Assert
        Assert.Equal(1, entity.CurrentPage);
    }

    [Fact]
    public void FileReadStatusEntity_LastReadDate_CanBeNull()
    {
        // Act
        var entity = new FileReadStatusEntity
        {
            FilePath = "/test/comic.cbz",
            IsRead = false
        };

        // Assert
        Assert.Null(entity.LastReadDate);
    }
}

public class ComicFileEntityTests
{
    [Fact]
    public void ComicFileEntity_DefaultConstructor_SetsDefaults()
    {
        // Act
        var entity = new ComicFileEntity();

        // Assert
        Assert.Equal(0, entity.Id);
        Assert.Equal(string.Empty, entity.FilePath);
        Assert.Equal(string.Empty, entity.FileName);
        Assert.Equal(string.Empty, entity.Directory);
        Assert.Equal(0, entity.FileSize);
        Assert.False(entity.IsProcessed);
        Assert.False(entity.IsRenamed);
        Assert.False(entity.IsNormalized);
        Assert.False(entity.IsDuplicate);
        Assert.False(entity.IsRead);
        Assert.Null(entity.Metadata);
    }

    [Fact]
    public void ComicFileEntity_AllPropertiesCanBeSet()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var metadata = new ComicMaintainer.Core.Models.ComicMetadata
        {
            Series = "Test Series",
            Title = "Test Title",
            Issue = "1"
        };

        // Act
        var entity = new ComicFileEntity
        {
            Id = 1,
            FilePath = "/test/file.cbz",
            FileName = "file.cbz",
            Directory = "/test",
            FileSize = 1024000,
            LastModified = now,
            IsProcessed = true,
            IsRenamed = true,
            IsNormalized = true,
            IsDuplicate = false,
            IsRead = true,
            Metadata = metadata,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now
        };

        // Assert
        Assert.Equal(1, entity.Id);
        Assert.Equal("/test/file.cbz", entity.FilePath);
        Assert.Equal("file.cbz", entity.FileName);
        Assert.Equal("/test", entity.Directory);
        Assert.Equal(1024000, entity.FileSize);
        Assert.Equal(now, entity.LastModified);
        Assert.True(entity.IsProcessed);
        Assert.True(entity.IsRenamed);
        Assert.True(entity.IsNormalized);
        Assert.False(entity.IsDuplicate);
        Assert.True(entity.IsRead);
        Assert.NotNull(entity.Metadata);
        Assert.Equal("Test Series", entity.Metadata.Series);
        Assert.Equal(now.AddDays(-1), entity.CreatedAt);
        Assert.Equal(now, entity.UpdatedAt);
    }
}

public class ProcessingHistoryEntityTests
{
    [Fact]
    public void ProcessingHistoryEntity_DefaultConstructor_SetsDefaults()
    {
        // Act
        var entity = new ProcessingHistoryEntity();

        // Assert
        Assert.Equal(0, entity.Id);
        Assert.Equal(Guid.Empty, entity.EntryId);
        Assert.Equal(string.Empty, entity.FilePath);
        Assert.Equal(string.Empty, entity.Action);
        Assert.False(entity.Success);
        Assert.Null(entity.ErrorMessage);
    }

    [Fact]
    public void ProcessingHistoryEntity_AllPropertiesCanBeSet()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var entryId = Guid.NewGuid();

        // Act
        var entity = new ProcessingHistoryEntity
        {
            Id = 1,
            EntryId = entryId,
            FilePath = "/test/file.cbz",
            Action = "Rename",
            Timestamp = now,
            Success = true,
            ErrorMessage = null,
            BeforeFilename = "old.cbz",
            AfterFilename = "new.cbz",
            BeforeTitle = "Old Title",
            AfterTitle = "New Title",
            BeforeSeries = "Old Series",
            AfterSeries = "New Series",
            BeforeIssue = "1",
            AfterIssue = "01",
            BeforePublisher = "Old Publisher",
            AfterPublisher = "New Publisher",
            BeforeYear = 2020,
            AfterYear = 2021,
            BeforeVolume = "1",
            AfterVolume = "01"
        };

        // Assert
        Assert.Equal(1, entity.Id);
        Assert.Equal(entryId, entity.EntryId);
        Assert.Equal("/test/file.cbz", entity.FilePath);
        Assert.Equal("Rename", entity.Action);
        Assert.Equal(now, entity.Timestamp);
        Assert.True(entity.Success);
        Assert.Null(entity.ErrorMessage);
        Assert.Equal("old.cbz", entity.BeforeFilename);
        Assert.Equal("new.cbz", entity.AfterFilename);
        Assert.Equal("Old Title", entity.BeforeTitle);
        Assert.Equal("New Title", entity.AfterTitle);
        Assert.Equal("Old Series", entity.BeforeSeries);
        Assert.Equal("New Series", entity.AfterSeries);
        Assert.Equal("1", entity.BeforeIssue);
        Assert.Equal("01", entity.AfterIssue);
        Assert.Equal("Old Publisher", entity.BeforePublisher);
        Assert.Equal("New Publisher", entity.AfterPublisher);
        Assert.Equal(2020, entity.BeforeYear);
        Assert.Equal(2021, entity.AfterYear);
        Assert.Equal("1", entity.BeforeVolume);
        Assert.Equal("01", entity.AfterVolume);
    }

    [Fact]
    public void ProcessingHistoryEntity_WithError_StoresErrorMessage()
    {
        // Act
        var entity = new ProcessingHistoryEntity
        {
            FilePath = "/test/file.cbz",
            Action = "Process",
            Success = false,
            ErrorMessage = "File not found"
        };

        // Assert
        Assert.False(entity.Success);
        Assert.Equal("File not found", entity.ErrorMessage);
    }
}
