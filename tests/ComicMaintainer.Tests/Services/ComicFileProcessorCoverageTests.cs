using System.Globalization;
using System.IO.Compression;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers filename formatting edge cases and the early-exit checks of
/// <see cref="ComicFileProcessor.IsFileAlreadyNormalized"/>.
/// </summary>
public class ComicFileProcessorCoverageTests : IDisposable
{
    private const string Template = "{series} - Chapter {issue}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfp-cov-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData("{series} - {issue_no_pad}", "5.0", 4, "S - 5.cbz")]
    [InlineData("{series} - {issue}", "5.0", 4, "S - 0005.cbz")]
    [InlineData("{series} - {issue}", "7", int.MaxValue, "S - 0000000007.cbz")]
    [InlineData("{series}.cbr", "1", 4, "S.cbr")]
    [InlineData("{series}.CBZ", "1", 4, "S.CBZ")]
    public void FormatFilename_HandlesTrailingZeroDecimalsHugePaddingAndExplicitExtensions(
        string template, string issue, int padding, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var result = ComicFileProcessor.FormatFilename(template, new ComicInfo { Series = "S" }, issue, ".cbz", padding);

            Assert.Equal(expected, result);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Padding can reach the formatter unchecked from user-settings.json or the environment: a negative
    // value used to render "{issue}" as the literal "D-1", and 1,000,000,000 and above made "D{padding}"
    // throw, so the padding was silently dropped.
    [Theory]
    [InlineData(int.MinValue, "S - Chapter 5.cbz")]
    [InlineData(-1, "S - Chapter 5.cbz")]
    [InlineData(0, "S - Chapter 5.cbz")]
    [InlineData(10, "S - Chapter 0000000005.cbz")]
    [InlineData(11, "S - Chapter 0000000005.cbz")]
    [InlineData(1_000_000_000, "S - Chapter 0000000005.cbz")]
    [InlineData(int.MaxValue, "S - Chapter 0000000005.cbz")]
    public void FormatFilename_ClampsPaddingTo0Through10(int padding, string expected)
    {
        var result = ComicFileProcessor.FormatFilename(Template, new ComicInfo { Series = "S" }, "5", ".cbz", padding);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("", "1", false)]
    [InlineData("1", null, false)]
    public void ChapterNumbersEquivalent_MissingValues_OnlyMatchWhenBothMissing(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, ComicFileProcessor.ChapterNumbersEquivalent(a, b));
    }

    [Fact]
    public void IsFileAlreadyNormalized_NilComicInfoRoot_ReturnsFalse()
    {
        var path = WriteCbz("Series/Series - Chapter 0001.cbz",
            "<ComicInfo xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\" />");

        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, Template));
    }

    [Fact]
    public void IsFileAlreadyNormalized_NoChapterInTagsOrFilename_ReturnsFalse()
    {
        var path = WriteCbz("Series/Oneshot.cbz", "<ComicInfo><Series>Series</Series><Title>Oneshot</Title></ComicInfo>");

        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, Template));
    }

    [Fact]
    public void IsFileAlreadyNormalized_MissingSeriesTag_ReturnsFalse()
    {
        var path = WriteCbz("Series/Series - Chapter 0001.cbz",
            "<ComicInfo><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");

        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, Template, fixTitle: false));
    }

    [Fact]
    public void IsFileAlreadyNormalized_FilenameCheckWithoutNumberTag_ReturnsFalse()
    {
        var path = WriteCbz("Series/Series - Chapter 0001.cbz", "<ComicInfo><Series>Series</Series></ComicInfo>");

        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, Template, fixTitle: false, fixSeries: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsFileAlreadyNormalized_FilenameCheckWithoutTemplate_ReturnsFalse(string? template)
    {
        var path = WriteCbz("Series/Series - Chapter 0001.cbz",
            "<ComicInfo><Series>Series</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");

        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, template));
        Assert.True(ComicFileProcessor.IsFileAlreadyNormalized(path, template, fixFilename: false));
    }

    [Fact]
    public void IsFileAlreadyNormalized_ExplicitComicFolder_IsUsedForSeriesComparison()
    {
        var path = WriteCbz("Other/Series - Chapter 0001.cbz",
            "<ComicInfo><Series>Series</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");

        Assert.True(ComicFileProcessor.IsFileAlreadyNormalized(path, Template, comicFolder: Path.Combine(_root, "Series")));
        Assert.False(ComicFileProcessor.IsFileAlreadyNormalized(path, Template));
    }

    private string WriteCbz(string relativePath, string comicInfoXml)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var info = zip.CreateEntry("ComicInfo.xml");
        using (var writer = new StreamWriter(info.Open()))
        {
            writer.Write(comicInfoXml);
        }

        var page = zip.CreateEntry("page001.jpg");
        using (var stream = page.Open())
        {
            stream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        return path;
    }
}
