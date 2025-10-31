using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class ComicInfoTests
{
    [Fact]
    public void ComicInfo_DefaultConstructor_CreatesInstance()
    {
        // Act
        var comicInfo = new ComicInfo();

        // Assert
        Assert.NotNull(comicInfo);
    }

    [Fact]
    public void ComicInfo_PropertiesCanBeSet()
    {
        // Arrange & Act
        var comicInfo = new ComicInfo
        {
            Series = "Batman",
            Number = "12",
            Title = "Dark Knight",
            Volume = "2",
            Year = 2023,
            Month = 6,
            Publisher = "DC Comics",
            Summary = "Test summary"
        };

        // Assert
        Assert.Equal("Batman", comicInfo.Series);
        Assert.Equal("12", comicInfo.Number);
        Assert.Equal("Dark Knight", comicInfo.Title);
        Assert.Equal("2", comicInfo.Volume);
        Assert.Equal(2023, comicInfo.Year);
        Assert.Equal(6, comicInfo.Month);
        Assert.Equal("DC Comics", comicInfo.Publisher);
        Assert.Equal("Test summary", comicInfo.Summary);
    }

    [Fact]
    public void ComicInfo_NullablePropertiesCanBeNull()
    {
        // Arrange & Act
        var comicInfo = new ComicInfo();

        // Assert
        Assert.Null(comicInfo.Series);
        Assert.Null(comicInfo.Number);
        Assert.Null(comicInfo.Title);
        Assert.Null(comicInfo.Volume);
        Assert.Null(comicInfo.Year);
        Assert.Null(comicInfo.Month);
        Assert.Null(comicInfo.Publisher);
    }
}

public class ComicMetadataTests
{
    [Fact]
    public void ComicMetadata_PropertiesWorkCorrectly()
    {
        // Act
        var metadata = new ComicMetadata
        {
            Series = "Test Series",
            Title = "Test Title",
            Issue = "5",
            Volume = "1",
            Publisher = "Test Publisher",
            Year = 2024,
            Summary = "Test Summary",
            Authors = new List<string> { "Author1", "Author2" },
            Tags = new List<string> { "Tag1", "Tag2" }
        };

        // Assert
        Assert.Equal("Test Series", metadata.Series);
        Assert.Equal("5", metadata.Issue);
        Assert.Equal(2, metadata.Authors.Count);
        Assert.Equal(2, metadata.Tags.Count);
    }
}

public class ComicFileTests
{
    [Fact]
    public void ComicFile_PropertiesWorkCorrectly()
    {
        // Act
        var file = new ComicFile
        {
            FilePath = "/test/file.cbz",
            FileSize = 1024000,
            LastModified = DateTime.UtcNow,
            IsProcessed = true,
            Metadata = new ComicMetadata { Series = "Test" }
        };

        // Assert
        Assert.Equal("/test/file.cbz", file.FilePath);
        Assert.Equal(1024000, file.FileSize);
        Assert.True(file.IsProcessed);
        Assert.NotNull(file.Metadata);
    }
}

public class ProcessingJobTests
{
    [Fact]
    public void ProcessingJob_PropertiesWorkCorrectly()
    {
        // Act
        var job = new ProcessingJob
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Running,
            Files = new List<string> { "file1.cbz", "file2.cbz" },
            TotalFiles = 2,
            ProcessedFiles = 1,
            FailedFiles = 0,
            CurrentFile = "file1.cbz",
            StartTime = DateTime.UtcNow,
            Errors = new Dictionary<string, string>()
        };

        // Assert
        Assert.NotEqual(Guid.Empty, job.JobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(2, job.TotalFiles);
        Assert.Equal(1, job.ProcessedFiles);
        Assert.NotNull(job.Errors);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void JobStatus_AllValues_AreValid(JobStatus status)
    {
        // Arrange & Act
        var job = new ProcessingJob { Status = status };

        // Assert
        Assert.Equal(status, job.Status);
    }
}
