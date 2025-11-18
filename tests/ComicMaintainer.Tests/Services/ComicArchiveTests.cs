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

    [Fact]
    public void WriteTags_WithExistingComicInfo_ReplacesComicInfo()
    {
        // Arrange
        var filePath = CreateTestCbzArchiveWithComicInfo("test.cbz", "OldSeries", "1", 2020);
        var newTags = new ComicInfo
        {
            Series = "NewSeries",
            Number = "2",
            Year = 2024
        };

        // Act
        using (var archive = new ComicArchive(filePath))
        {
            archive.WriteTags(newTags, "cr");
        }

        // Verify - Old ComicInfo should be replaced
        using (var archive = new ComicArchive(filePath))
        {
            var readTags = archive.ReadTags("cr");
            Assert.NotNull(readTags);
            Assert.Equal("NewSeries", readTags.Series);
            Assert.Equal("2", readTags.Number);
            Assert.Equal(2024, readTags.Year);
            Assert.NotEqual("OldSeries", readTags.Series);
        }
    }

    [Fact]
    public void WriteTags_WithMultipleEntries_PreservesAllOtherFiles()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "multifile.cbz");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry1 = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(entry1.Open()))
                writer.Write("image1");
            
            var entry2 = archive.CreateEntry("page002.jpg");
            using (var writer = new StreamWriter(entry2.Open()))
                writer.Write("image2");
            
            var entry3 = archive.CreateEntry("subfolder/page003.jpg");
            using (var writer = new StreamWriter(entry3.Open()))
                writer.Write("image3");
        }
        _createdFiles.Add(filePath);

        var newTags = new ComicInfo { Series = "Test" };

        // Act
        using (var archive = new ComicArchive(filePath))
        {
            archive.WriteTags(newTags, "cr");
        }

        // Verify - All original files should still be present
        using (var verifyArchive = ZipFile.OpenRead(filePath))
        {
            Assert.Contains(verifyArchive.Entries, e => e.FullName == "page001.jpg");
            Assert.Contains(verifyArchive.Entries, e => e.FullName == "page002.jpg");
            Assert.Contains(verifyArchive.Entries, e => e.FullName == "subfolder/page003.jpg");
            Assert.Contains(verifyArchive.Entries, e => e.FullName == "ComicInfo.xml");
        }
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var filePath = CreateTestCbzArchive("test.cbz");
        var archive = new ComicArchive(filePath);

        // Act & Assert - Should not throw
        archive.Dispose();
        archive.Dispose();
    }

    [Fact]
    public void ReadTags_WithCaseInsensitiveComicInfo_FindsComicInfo()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "test_case.cbz");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            // Create ComicInfo with different casing
            var comicInfoEntry = archive.CreateEntry("COMICINFO.XML");
            using (var writer = new StreamWriter(comicInfoEntry.Open()))
            {
                writer.Write("<?xml version=\"1.0\"?><ComicInfo><Series>CaseTest</Series></ComicInfo>");
            }
            var imageEntry = archive.CreateEntry("page001.jpg");
            using (var writer = new StreamWriter(imageEntry.Open()))
            {
                writer.Write("dummy");
            }
        }
        _createdFiles.Add(filePath);

        // Act
        using var comicArchive = new ComicArchive(filePath);
        var tags = comicArchive.ReadTags("cr");

        // Assert
        Assert.NotNull(tags);
        Assert.Equal("CaseTest", tags.Series);
    }

    [Fact]
    public void WriteTags_CreatesValidXmlWithDeclaration()
    {
        // Arrange
        var filePath = CreateTestCbzArchive("test.cbz");
        var newTags = new ComicInfo
        {
            Series = "XMLTest",
            Number = "99"
        };

        // Act
        using (var archive = new ComicArchive(filePath))
        {
            archive.WriteTags(newTags, "cr");
        }

        // Verify XML structure
        using (var zipArchive = ZipFile.OpenRead(filePath))
        {
            var comicInfoEntry = zipArchive.Entries.FirstOrDefault(e => e.Name == "ComicInfo.xml");
            Assert.NotNull(comicInfoEntry);
            
            using var stream = comicInfoEntry.Open();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            
            // Should have XML declaration
            Assert.Contains("<?xml version", content);
            Assert.Contains("<ComicInfo", content);
            Assert.Contains("<Series>XMLTest</Series>", content);
        }
    }

    [Fact]
    public void Constructor_WithCbzExtensionVariants_OpensSuccessfully()
    {
        // Arrange - Create test files with different case extensions
        var filePath = Path.Combine(_testDirectory, "test.CBZ");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("page001.jpg");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("dummy");
        }
        _createdFiles.Add(filePath);

        // Act
        using var comicArchive = new ComicArchive(filePath);

        // Assert
        Assert.NotNull(comicArchive);
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
