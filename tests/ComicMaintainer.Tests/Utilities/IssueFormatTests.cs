using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class IssueFormatTests
{
    [Theory]
    [InlineData("Annual", "Annual")]
    [InlineData(" annual ", "Annual")]
    [InlineData("SPECIAL", "Special")]
    [InlineData("TPB", null)]
    [InlineData("Digital", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromFormat_NamesOnlyTheKindsWithTheirOwnNumbering(string? format, string? expected)
    {
        Assert.Equal(expected, IssueFormat.FromFormat(format));
    }

    [Theory]
    // <Format> decides first...
    [InlineData("Special", "Green Lantern", "1", "x.cbz", "Special")]
    [InlineData("Special", "Green Lantern Annual", "1", "x.cbz", "Special")]
    [InlineData("Annual", "Green Lantern", "1", "x.cbz", "Annual")]
    // ...then an Annual named by its series, number or file name...
    [InlineData(null, "Green Lantern Annual", "1", "x.cbz", "Annual")]
    [InlineData("TPB", "Green Lantern", "Annual 1", "x.cbz", "Annual")]
    [InlineData(null, "Green Lantern", "1", "Green Lantern Annual 001.cbz", "Annual")]
    // ...and a regular issue has no kind.
    [InlineData(null, "Green Lantern", "1", "Green Lantern 001.cbz", null)]
    [InlineData("TPB", "Green Lantern", "1", "x.cbz", null)]
    public void Of_ReadsTheFormatThenTheAnnualMarks(string? format, string series, string issue, string fileName, string? expected)
    {
        Assert.Equal(expected, IssueFormat.Of(format, series, issue, fileName));
    }
}
