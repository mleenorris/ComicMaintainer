using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using System.IO.Compression;

namespace ComicMaintainer.Tests.Services;

public class ComicArchiveTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<string> _createdFiles = new();

    public ComicArchiveTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"comic_archive_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Constructor_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => new ComicArchive(filePath));
    }

    [Fact]
    public void Constructor_WithUnsupportedExtension_ThrowsNotSupportedException()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(filePath, "test content");
        _createdFiles.Add(filePath);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => new ComicArchive(filePath));
    }

    [Fact]
    public void Constructor_WithValidCbzFile_OpensSuccessfully()
    {
        // Arrange
        var filePath = CreateTestCbzArchive("test.cbz");

        // Act
        using var archive = new ComicArchive(filePath);

        // Assert
        Assert.NotNull(archive);
    }

    [Fact]
    public void ReadTags_WithComicInfo_ReturnsComicInfo()
    {
        // Arrange
        var filePath = CreateTestCbzArchiveWithComicInfo("test.cbz", "Batman", "12", 2023);

        // Act
        using var archive = new ComicArchive(filePath);
        var tags = archive.ReadTags("cr");

        // Assert
        Assert.NotNull(tags);
        Assert.Equal("Batman", tags.Series);
        Assert.Equal("12", tags.Number);
        Assert.Equal(2023, tags.Year);
    }

    [Fact]
    public void ReadTags_WithoutComicInfo_ReturnsEmptyComicInfo()
    {
        // Arrange
        var filePath = CreateTestCbzArchive("test.cbz");

        // Act
        using var archive = new ComicArchive(filePath);
        var tags = archive.ReadTags("cr");

        // Assert
        Assert.NotNull(tags);
        Assert.Null(tags.Series);
    }

    [Fact]
    public void WriteTags_ToCbzFile_UpdatesSuccessfully()
    {
        // Arrange
        var filePath = CreateTestCbzArchive("test.cbz");
        var newTags = new ComicInfo
        {
            Series = "Superman",
            Number = "5",
            Title = "Test Title",
            Year = 2024
        };

        // Act
        using (var archive = new ComicArchive(filePath))
        {
            archive.WriteTags(newTags, "cr");
        }

        // Verify
        using (var archive = new ComicArchive(filePath))
        {
            var readTags = archive.ReadTags("cr");
            Assert.NotNull(readTags);
            Assert.Equal("Superman", readTags.Series);
            Assert.Equal("5", readTags.Number);
            Assert.Equal(2024, readTags.Year);
        }
    }

    [Fact]
    public void WriteTags_ToCbrFile_ThrowsNotSupportedException()
    {
        // Arrange
        var filePath = CreateTestCbrArchive("test.cbr");
        var newTags = new ComicInfo { Series = "Test" };

        // Act & Assert
        using var archive = new ComicArchive(filePath);
        Assert.Throws<NotSupportedException>(() => archive.WriteTags(newTags, "cr"));
    }

    [Fact]
    public void ReadTags_WithInvalidXml_ReturnsEmptyComicInfo()
    {
        // Arrange
        var filePath = CreateTestCbzArchiveWithInvalidXml("test.cbz");

        // Act
        using var archive = new ComicArchive(filePath);
        var tags = archive.ReadTags("cr");

        // Assert
        Assert.NotNull(tags);
    }

    private string CreateTestCbzArchive(string fileName)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("page001.jpg");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("dummy image content");
        }
        _createdFiles.Add(filePath);
        return filePath;
    }

    private string CreateTestCbzArchiveWithComicInfo(string fileName, string series, string issue, int year)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>{issue}</Number>
    <Year>{year}</Year>
</ComicInfo>";

        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write(comicInfoXml);
            }

            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy image content");
            }
        }
        _createdFiles.Add(filePath);
        return filePath;
    }

    private string CreateTestCbzArchiveWithInvalidXml(string fileName)
    {
        var filePath = Path.Combine(_testDirectory, fileName);
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var comicInfoEntry = archive.CreateEntry("ComicInfo.xml");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write("Invalid XML <<<<");
            }
        }
        _createdFiles.Add(filePath);
        return filePath;
    }

    private string CreateTestCbrArchive(string fileName)
    {
        // For testing purposes, create a .cbr file (it won't be a valid RAR, but we're testing the extension check)
        var filePath = Path.Combine(_testDirectory, fileName);
        // Create a dummy file with .cbr extension
        using (var archive = ZipFile.Open(filePath + ".tmp", ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("page001.jpg");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("dummy content");
        }
        File.Move(filePath + ".tmp", filePath);
        _createdFiles.Add(filePath);
        return filePath;
    }

    public void Dispose()
    {
        foreach (var file in _createdFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
