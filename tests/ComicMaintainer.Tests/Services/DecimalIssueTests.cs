using System.Globalization;
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

        // Regression test for issue #843: ComicInfo issue numbers always use "." as the
        // decimal separator, so the server culture (LANG/LC_ALL) must not change how they
        // are parsed or formatted. Under de-DE "5.5" used to be read as 55 ("Chapter 0055.5"),
        // under fr-FR the padding was silently skipped ("Chapter 5.5"), and under sv-SE a
        // negative issue was written with U+2212 instead of "-".
        [Theory]
        [InlineData("de-DE", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("de-DE", "{series} - Chapter {issue}", "5.0", "S - Chapter 0005.cbz")]
        [InlineData("de-DE", "{series} - Chapter {issue}", "142.5", "S - Chapter 0142.5.cbz")]
        [InlineData("de-DE", "{series} - Chapter {issue}", ".5", "S - Chapter 0000.5.cbz")]
        [InlineData("de-DE", "{series} - {issue_no_pad}", "5.0", "S - 5.cbz")]
        [InlineData("de-DE", "{series} - {issue_no_pad}", "5.5", "S - 5.5.cbz")]
        [InlineData("es-ES", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("it-IT", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("nl-NL", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("pt-BR", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("tr-TR", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("fr-FR", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        [InlineData("fr-FR", "{series} - Chapter {issue}", "5.0", "S - Chapter 0005.cbz")]
        [InlineData("sv-SE", "{series} - Chapter {issue}", "-1", "S - Chapter -0001.cbz")]
        [InlineData("en-US", "{series} - Chapter {issue}", "5.5", "S - Chapter 0005.5.cbz")]
        public void FormatFilename_RendersIssueIndependentOfServerCulture_Issue843(
            string culture, string template, string issueNumber, string expected)
        {
            // Arrange
            var tags = new ComicInfo { Series = "S" };
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            try
            {
                // Act
                var result = ComicFileProcessor.FormatFilename(template, tags, issueNumber, ".cbz", 4);

                // Assert
                Assert.Equal(expected, result);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // Regression test for issue #843: "," is not a decimal or thousands separator in
        // an issue number (matching the original Python float() parse), so such values are
        // kept verbatim instead of being read as a different chapter (e.g. "1,5" as 15).
        [Theory]
        [InlineData("", "1,5")]
        [InlineData("de-DE", "1,5")]
        [InlineData("", "1,000")]
        [InlineData("en-US", "1,000")]
        public void FormatFilename_WithCommaInIssue_KeepsIssueVerbatim_Issue843(string culture, string issueNumber)
        {
            // Arrange
            var tags = new ComicInfo { Series = "S" };
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            try
            {
                // Act
                var result = ComicFileProcessor.FormatFilename("{series} - Chapter {issue}", tags, issueNumber, ".cbz", 4);

                // Assert
                Assert.Equal($"S - Chapter {issueNumber}.cbz", result);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
