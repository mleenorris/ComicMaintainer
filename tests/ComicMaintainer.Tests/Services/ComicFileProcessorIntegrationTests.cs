using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using System.IO.Compression;

namespace ComicMaintainer.Tests.Services;

public class ComicFileProcessorIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<string> _createdFiles = new();

    public ComicFileProcessorIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"comic_processor_integration_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithNormalizedFile_ReturnsTrue()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = CreateNormalizedFile(comicFolder, "Batman", "12", template);

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithWrongFilename_ReturnsFalse()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        // Create with correct tags but wrong filename
        var filePath = Path.Combine(comicFolder, "WrongName.cbz");
        CreateArchiveWithTags(filePath, "Batman", "12", 2023, "Chapter 12");

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithWrongTitle_ReturnsFalse()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = Path.Combine(comicFolder, "Batman - Chapter 0012.cbz");
        CreateArchiveWithTags(filePath, "Batman", "12", 2023, "Wrong Title");

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithWrongSeries_ReturnsFalse()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = Path.Combine(comicFolder, "Batman - Chapter 0012.cbz");
        CreateArchiveWithTags(filePath, "Superman", "12", 2023, "Chapter 12");

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithNoMetadata_ReturnsFalse()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = Path.Combine(comicFolder, "Batman - Chapter 0012.cbz");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("page001.jpg");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("dummy");
        }
        _createdFiles.Add(filePath);

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithOnlyTitleCheck_ChecksTitleOnly()
    {
        // Arrange
        var comicFolder = Path.Combine(_testDirectory, "Batman");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = Path.Combine(comicFolder, "WrongFilename.cbz");
        CreateArchiveWithTags(filePath, "WrongSeries", "12", 2023, "Chapter 12");

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            null,
            fixTitle: true,
            fixSeries: false,
            fixFilename: false,
            comicFolder: comicFolder);

        // Assert
        Assert.True(result); // Only title matters, and it's correct
    }

    [Fact]
    public void IsFileAlreadyNormalized_WithSeriesNameContainingSpecialChars_HandlesCorrectly()
    {
        // Arrange
        var template = "{series} - Chapter {issue}";
        var comicFolder = Path.Combine(_testDirectory, "Batman_The Dark Knight");
        Directory.CreateDirectory(comicFolder);
        
        var filePath = CreateNormalizedFile(comicFolder, "Batman:The Dark Knight", "12", template);

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            template,
            fixTitle: true,
            fixSeries: true,
            fixFilename: true,
            comicFolder: comicFolder);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFileAlreadyNormalized_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDirectory, "nonexistent.cbz");

        // Act
        var result = ComicFileProcessor.IsFileAlreadyNormalized(
            filePath,
            "{series} {issue}",
            fixTitle: true,
            fixSeries: true,
            fixFilename: true);

        // Assert
        Assert.False(result);
    }

    private string CreateNormalizedFile(string folder, string series, string issue, string template)
    {
        var filename = ComicFileProcessor.FormatFilename(template, new ComicInfo { Series = series }, issue);
        var filePath = Path.Combine(folder, filename);
        CreateArchiveWithTags(filePath, series, issue, 2023, $"Chapter {issue}");
        return filePath;
    }

    private void CreateArchiveWithTags(string filePath, string series, string issue, int year, string title)
    {
        var comicInfoXml = $@"<?xml version=""1.0""?>
<ComicInfo>
    <Series>{series}</Series>
    <Number>{issue}</Number>
    <Year>{year}</Year>
    <Title>{title}</Title>
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
