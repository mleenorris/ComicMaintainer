using ComicMaintainer.Core.Models;
using System.Text.Json;

namespace ComicMaintainer.Tests.Models;

public class FileDtoTests
{
    [Fact]
    public void FromComicFile_MapsAllProperties()
    {
        // Arrange
        var comicFile = new ComicFile
        {
            FilePath = "/comics/Batman #1.cbz",
            FileName = "Batman #1.cbz",
            FileSize = 1024 * 1024 * 10, // 10 MB
            LastModified = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            IsProcessed = true,
            IsDuplicate = false
        };

        // Act
        var dto = FileDto.FromComicFile(comicFile);

        // Assert
        Assert.Equal(comicFile.FilePath, dto.RelativePath);
        Assert.Equal(comicFile.FileName, dto.Name);
        Assert.Equal(comicFile.FileSize, dto.Size);
        Assert.Equal(comicFile.IsProcessed, dto.Processed);
        Assert.Equal(comicFile.IsDuplicate, dto.Duplicate);
        Assert.Equal(new DateTimeOffset(comicFile.LastModified).ToUnixTimeSeconds(), dto.Modified);
    }

    [Fact]
    public void FileDto_SerializesWithCorrectPropertyNames()
    {
        // Arrange
        var dto = new FileDto
        {
            RelativePath = "/test/file.cbz",
            Name = "file.cbz",
            Size = 1024,
            Modified = 1705315800,
            Processed = true,
            Duplicate = false
        };

        // Act
        var json = JsonSerializer.Serialize(dto);
        var jsonDoc = JsonDocument.Parse(json);
        var root = jsonDoc.RootElement;

        // Assert - verify JSON uses snake_case property names
        Assert.True(root.TryGetProperty("relative_path", out _));
        Assert.True(root.TryGetProperty("name", out _));
        Assert.True(root.TryGetProperty("size", out _));
        Assert.True(root.TryGetProperty("modified", out _));
        Assert.True(root.TryGetProperty("processed", out _));
        Assert.True(root.TryGetProperty("duplicate", out _));
        
        // Verify it doesn't have camelCase properties
        Assert.False(root.TryGetProperty("relativePath", out _));
        Assert.False(root.TryGetProperty("isProcessed", out _));
    }
}
