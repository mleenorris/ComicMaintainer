using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Tests.Models;

public class ComicPageInfoTests
{
    [Fact]
    public void ComicPageInfo_DefaultConstructor_CreatesInstance()
    {
        // Act
        var pageInfo = new ComicPageInfo();

        // Assert
        Assert.NotNull(pageInfo);
        Assert.Equal(0, pageInfo.Image);
        Assert.False(pageInfo.DoublePage);
        Assert.Equal(0, pageInfo.ImageSize);
        Assert.Equal(0, pageInfo.ImageWidth);
        Assert.Equal(0, pageInfo.ImageHeight);
    }

    [Fact]
    public void ComicPageInfo_AllPropertiesCanBeSet()
    {
        // Arrange & Act
        var pageInfo = new ComicPageInfo
        {
            Image = 5,
            Type = "FrontCover",
            DoublePage = true,
            ImageSize = 1024000,
            Key = "page-key",
            Bookmark = "bookmark-name",
            ImageWidth = 1920,
            ImageHeight = 1080
        };

        // Assert
        Assert.Equal(5, pageInfo.Image);
        Assert.Equal("FrontCover", pageInfo.Type);
        Assert.True(pageInfo.DoublePage);
        Assert.Equal(1024000, pageInfo.ImageSize);
        Assert.Equal("page-key", pageInfo.Key);
        Assert.Equal("bookmark-name", pageInfo.Bookmark);
        Assert.Equal(1920, pageInfo.ImageWidth);
        Assert.Equal(1080, pageInfo.ImageHeight);
    }

    [Theory]
    [InlineData("FrontCover")]
    [InlineData("BackCover")]
    [InlineData("Story")]
    [InlineData("Advertisement")]
    [InlineData("Editorial")]
    public void ComicPageInfo_TypeProperty_AcceptsValidTypes(string pageType)
    {
        // Arrange & Act
        var pageInfo = new ComicPageInfo { Type = pageType };

        // Assert
        Assert.Equal(pageType, pageInfo.Type);
    }
}

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

    [Fact]
    public void ComicInfo_AllPropertiesCanBeSet()
    {
        // Arrange & Act
        var pages = new List<ComicPageInfo>
        {
            new ComicPageInfo { Image = 0, Type = "FrontCover", DoublePage = false },
            new ComicPageInfo { Image = 1, Type = "Story", DoublePage = true }
        };

        var comicInfo = new ComicInfo
        {
            Title = "Test Title",
            Series = "Test Series",
            Number = "1",
            Count = 10,
            Volume = "1",
            AlternateSeries = "Alt Series",
            AlternateNumber = "2",
            AlternateCount = 5,
            Summary = "Test Summary",
            Notes = "Test Notes",
            Year = 2024,
            Month = 11,
            Day = 14,
            Writer = "Writer Name",
            Penciller = "Penciller Name",
            Inker = "Inker Name",
            Colorist = "Colorist Name",
            Letterer = "Letterer Name",
            CoverArtist = "Cover Artist",
            Editor = "Editor Name",
            Publisher = "Publisher Name",
            Imprint = "Imprint Name",
            Genre = "Action",
            Web = "http://example.com",
            PageCount = 24,
            LanguageISO = "en",
            Format = "Series",
            BlackAndWhite = "No",
            Manga = "No",
            Characters = "Character1, Character2",
            Teams = "Team1",
            Locations = "Location1",
            ScanInformation = "Scan Info",
            StoryArc = "Arc Name",
            SeriesGroup = "Group Name",
            AgeRating = "Teen",
            Pages = pages
        };

        // Assert
        Assert.Equal("Test Title", comicInfo.Title);
        Assert.Equal("Test Series", comicInfo.Series);
        Assert.Equal("1", comicInfo.Number);
        Assert.Equal(10, comicInfo.Count);
        Assert.Equal("1", comicInfo.Volume);
        Assert.Equal("Alt Series", comicInfo.AlternateSeries);
        Assert.Equal("2", comicInfo.AlternateNumber);
        Assert.Equal(5, comicInfo.AlternateCount);
        Assert.Equal("Test Summary", comicInfo.Summary);
        Assert.Equal("Test Notes", comicInfo.Notes);
        Assert.Equal(2024, comicInfo.Year);
        Assert.Equal(11, comicInfo.Month);
        Assert.Equal(14, comicInfo.Day);
        Assert.Equal("Writer Name", comicInfo.Writer);
        Assert.Equal("Penciller Name", comicInfo.Penciller);
        Assert.Equal("Inker Name", comicInfo.Inker);
        Assert.Equal("Colorist Name", comicInfo.Colorist);
        Assert.Equal("Letterer Name", comicInfo.Letterer);
        Assert.Equal("Cover Artist", comicInfo.CoverArtist);
        Assert.Equal("Editor Name", comicInfo.Editor);
        Assert.Equal("Publisher Name", comicInfo.Publisher);
        Assert.Equal("Imprint Name", comicInfo.Imprint);
        Assert.Equal("Action", comicInfo.Genre);
        Assert.Equal("http://example.com", comicInfo.Web);
        Assert.Equal(24, comicInfo.PageCount);
        Assert.Equal("en", comicInfo.LanguageISO);
        Assert.Equal("Series", comicInfo.Format);
        Assert.Equal("No", comicInfo.BlackAndWhite);
        Assert.Equal("No", comicInfo.Manga);
        Assert.Equal("Character1, Character2", comicInfo.Characters);
        Assert.Equal("Team1", comicInfo.Teams);
        Assert.Equal("Location1", comicInfo.Locations);
        Assert.Equal("Scan Info", comicInfo.ScanInformation);
        Assert.Equal("Arc Name", comicInfo.StoryArc);
        Assert.Equal("Group Name", comicInfo.SeriesGroup);
        Assert.Equal("Teen", comicInfo.AgeRating);
        Assert.NotNull(comicInfo.Pages);
        Assert.Equal(2, comicInfo.Pages.Count);
    }

    [Fact]
    public void ComicInfo_ToMetadata_ConvertsCorrectly()
    {
        // Arrange
        var comicInfo = new ComicInfo
        {
            Title = "Test Title",
            Series = "Test Series",
            Number = "5",
            Volume = "2",
            Publisher = "Test Publisher",
            Year = 2024,
            Summary = "Test Summary",
            Writer = "Writer1",
            Penciller = "Penciller1",
            Inker = "Inker1"
        };

        // Act
        var metadata = comicInfo.ToMetadata();

        // Assert
        Assert.Equal("Test Title", metadata.Title);
        Assert.Equal("Test Series", metadata.Series);
        Assert.Equal("5", metadata.Issue);
        Assert.Equal("2", metadata.Volume);
        Assert.Equal("Test Publisher", metadata.Publisher);
        Assert.Equal(2024, metadata.Year);
        Assert.Equal("Test Summary", metadata.Summary);
        Assert.Equal(3, metadata.Authors.Count);
        Assert.Contains("Writer1", metadata.Authors);
        Assert.Contains("Penciller1", metadata.Authors);
        Assert.Contains("Inker1", metadata.Authors);
    }

    [Fact]
    public void ComicInfo_ToMetadata_FiltersEmptyAuthors()
    {
        // Arrange
        var comicInfo = new ComicInfo
        {
            Writer = "Writer1",
            Penciller = "",
            Inker = null,
            Colorist = "   ",
            Letterer = "Letterer1",
            Editor = "Editor1"
        };

        // Act
        var metadata = comicInfo.ToMetadata();

        // Assert
        Assert.Equal(3, metadata.Authors.Count);
        Assert.Contains("Writer1", metadata.Authors);
        Assert.Contains("Letterer1", metadata.Authors);
        Assert.Contains("Editor1", metadata.Authors);
    }

    [Fact]
    public void ComicInfo_FromMetadata_ConvertsCorrectly()
    {
        // Arrange
        var metadata = new ComicMetadata
        {
            Title = "Test Title",
            Series = "Test Series",
            Issue = "10",
            Volume = "3",
            Publisher = "Test Publisher",
            Year = 2024,
            Summary = "Test Summary",
            Authors = new List<string> { "Author1", "Author2" },
            Tags = new List<string> { "Tag1" }
        };

        // Act
        var comicInfo = ComicInfo.FromMetadata(metadata);

        // Assert
        Assert.Equal("Test Title", comicInfo.Title);
        Assert.Equal("Test Series", comicInfo.Series);
        Assert.Equal("10", comicInfo.Number);
        Assert.Equal("3", comicInfo.Volume);
        Assert.Equal("Test Publisher", comicInfo.Publisher);
        Assert.Equal(2024, comicInfo.Year);
        Assert.Equal("Test Summary", comicInfo.Summary);
        Assert.Equal("Author1", comicInfo.Writer);
    }

    [Fact]
    public void ComicInfo_FromMetadata_WithEmptyAuthors_SetsNullWriter()
    {
        // Arrange
        var metadata = new ComicMetadata
        {
            Series = "Test Series",
            Authors = new List<string>()
        };

        // Act
        var comicInfo = ComicInfo.FromMetadata(metadata);

        // Assert
        Assert.Null(comicInfo.Writer);
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
