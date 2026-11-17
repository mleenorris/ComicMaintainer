using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class IssueFormatTests
{
    [Theory]
    [InlineData("Annual", "Annual")]
    [InlineData(" annual ", "Annual")]
    [InlineData("SPECIAL", "Special")]
    [InlineData("Giant-Size", "Giant-Size")]
    [InlineData("giant", "Giant-Size")]
    [InlineData(" Giant Size ", "Giant-Size")]
    [InlineData("GIANT-SIZE", "Giant-Size")]
    [InlineData("Giant Sized Annual", null)]
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
    // ...or by a title that names the issue as one...
    [InlineData(null, "Batman", "1", "Batman - Chapter 0001.cbz", "Annual", "#- Annual 01")]
    [InlineData("Special", "Batman", "1", "x.cbz", "Special", "#- Annual 01")]
    // ...and a regular issue has no kind.
    [InlineData(null, "Batman", "1", "Batman - Chapter 0001.cbz", null, "The Annual Party")]
    [InlineData(null, "Green Lantern", "1", "Green Lantern 001.cbz", null)]
    [InlineData("TPB", "Green Lantern", "1", "x.cbz", null)]
    public void Of_ReadsTheFormatThenTheAnnualMarks(string? format, string series, string issue, string fileName, string? expected, string? title = null)
    {
        Assert.Equal(expected, IssueFormat.Of(format, series, issue, fileName, title));
    }

    [Theory]
    // <Format>, in any of its spellings.
    [InlineData("Giant", "X-Men", "1", "x.cbz", null, "Giant-Size")]
    // The series or file name, with "Giant-Size" before or after the title.
    [InlineData(null, "Giant-Size X-Men", "1", "x.cbz", null, "Giant-Size")]
    [InlineData(null, "X-Men Giant Size (2005)", "1", "x.cbz", null, "Giant-Size")]
    [InlineData(null, "X-Men", "1", "Giant-Size X-Men 001 (1975).cbz", null, "Giant-Size")]
    [InlineData(null, "X-Men", "1", "X-Men Giant-Size - Chapter 0001.cbz", null, "Giant-Size")]
    // A title naming the issue as one, as taggers leave it.
    [InlineData(null, "X-Men", "2025", "X-Men - Chapter 2025.cbz", "Giant 2025", "Giant-Size")]
    [InlineData(null, "X-Men", "2025", "X-Men - Chapter 2025.cbz", "#Giant 2025 2", "Giant-Size")]
    [InlineData(null, "X-Men", "1", "X-Men - Chapter 0001.cbz", "Giant-Size X-Men 1", "Giant-Size")]
    // "Giant" alone in a series, or in a story title, is not one.
    [InlineData(null, "Giant Days", "1", "Giant Days 001.cbz", null, null)]
    [InlineData(null, "The Iron Giant", "1", "x.cbz", null, null)]
    [InlineData(null, "X-Men", "2", "x.cbz", "Attack of the Giant", null)]
    [InlineData(null, "X-Men", "2", "x.cbz", "Giant 2025", null)]
    // An Annual stays one, whatever its title says.
    [InlineData(null, "X-Men Annual", "1", "x.cbz", "Giant 1", "Annual")]
    public void Of_ReadsAGiantSizeIssueFromItsFormatSeriesFileNameOrTitle(string? format, string series, string issue, string fileName, string? title, string? expected)
    {
        Assert.Equal(expected, IssueFormat.Of(format, series, issue, fileName, title));
    }

    [Theory]
    // A title naming the issue as a Special, before its number...
    [InlineData("1", "Special 1", "Special")]
    [InlineData("1", "All New X-Men Special 001 (2015)", "Special")]
    // ...or as its whole label when it has no number.
    [InlineData(null, "All New X-Men .Special – One Down", "Special")]
    [InlineData("", "Special", "Special")]
    [InlineData(null, "Special: One Down", "Special")]
    // A story title is not a label.
    [InlineData(null, "Special Delivery", null)]
    [InlineData(null, "A Special Day", null)]
    [InlineData("3", "Special 1", null)]
    [InlineData("1", ".Special – One Down", null)]
    [InlineData("1a", "Special 1", null)]
    [InlineData(null, null, null)]
    public void Of_ReadsASpecialFromItsTitle(string? issue, string? title, string? expected)
    {
        Assert.Equal(expected, IssueFormat.Of(null, "All-New X-Men", issue, "All-New X-Men - Chapter.cbz", title));
    }
}
