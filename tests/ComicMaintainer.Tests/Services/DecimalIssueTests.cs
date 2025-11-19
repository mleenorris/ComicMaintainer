using Xunit;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services
{
    public class DecimalIssueTests
    {
        [Theory]
        [InlineData("142.5", "Second Life Ranker - Chapter 0142.5.cbz")]
        [InlineData("12.5", "Second Life Ranker - Chapter 0012.5.cbz")]
        [InlineData("1.5", "Second Life Ranker - Chapter 0001.5.cbz")]
        [InlineData("142", "Second Life Ranker - Chapter 0142.cbz")]
        [InlineData("5.25", "Second Life Ranker - Chapter 0005.25.cbz")]
        public void FormatFilename_WithDecimalIssues_FormatsWithCorrectPadding(string issueNumber, string expected)
        {
            // Arrange
            var template = "{series} - Chapter {issue}";
            var tags = new ComicInfo { Series = "Second Life Ranker" };

            // Act
            var result = ComicFileProcessor.FormatFilename(template, tags, issueNumber, ".cbz", 4);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("142.5", 3, "Second Life Ranker - Chapter 142.5.cbz")]
        [InlineData("12.5", 3, "Second Life Ranker - Chapter 012.5.cbz")]
        [InlineData("1.5", 6, "Second Life Ranker - Chapter 000001.5.cbz")]
        public void FormatFilename_WithDifferentPadding_FormatsCorrectly(string issueNumber, int padding, string expected)
        {
            // Arrange
            var template = "{series} - Chapter {issue}";
            var tags = new ComicInfo { Series = "Second Life Ranker" };

            // Act
            var result = ComicFileProcessor.FormatFilename(template, tags, issueNumber, ".cbz", padding);

            // Assert
            Assert.Equal(expected, result);
        }
    }
}
