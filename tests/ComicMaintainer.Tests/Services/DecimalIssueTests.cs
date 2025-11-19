using Xunit;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services
{
    public class DecimalIssueTests
    {
        [Fact]
        public void FormatFilename_WithIssue142Point5_FormatsWithPadding()
        {
            // Arrange
            var template = "{series} - Chapter {issue}";
            var tags = new ComicInfo { Series = "Second Life Ranker" };

            // Act
            var result = ComicFileProcessor.FormatFilename(template, tags, "142.5", ".cbz", 4);

            // Assert
            Assert.Equal("Second Life Ranker - Chapter 0142.5.cbz", result);
        }
    }
}
