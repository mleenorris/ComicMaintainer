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
