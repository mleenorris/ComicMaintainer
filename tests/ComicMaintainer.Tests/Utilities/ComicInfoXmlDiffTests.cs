using System.Xml.Linq;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

/// <summary>
/// <see cref="ComicInfoXml.Diff"/>: the tag changes a processing preview shows, element by element.
/// </summary>
public class ComicInfoXmlDiffTests
{
    [Fact]
    public void Diff_ListsChangedAndAddedElementsInTheNewOrder_ThenRemovedOnes()
    {
        var before = XDocument.Parse("<ComicInfo><Series>batman</Series><Number>001</Number><Volume>2016</Volume><Notes>kept</Notes></ComicInfo>");
        var after = XDocument.Parse("<ComicInfo><Title>Chapter 1</Title><Series>Batman</Series><Number>1</Number><Notes>kept</Notes></ComicInfo>");

        var changes = ComicInfoXml.Diff(before, after);

        Assert.Equal(
            new[]
            {
                new ComicInfoFieldChange("Title", null, "Chapter 1"),
                new ComicInfoFieldChange("Series", "batman", "Batman"),
                new ComicInfoFieldChange("Number", "001", "1"),
                new ComicInfoFieldChange("Volume", "2016", null)
            },
            changes);
    }

    [Fact]
    public void Diff_WithoutAnEarlierDocument_ListsEveryValue()
    {
        var after = XDocument.Parse("<ComicInfo><Series>Batman</Series><Number>1</Number><Summary></Summary></ComicInfo>");

        Assert.Equal(
            new[] { new ComicInfoFieldChange("Series", null, "Batman"), new ComicInfoFieldChange("Number", null, "1") },
            ComicInfoXml.Diff(null, after));
    }

    [Fact]
    public void Diff_JoinsRepeatedElements_AndIgnoresPages()
    {
        var before = XDocument.Parse("<ComicInfo><Writer>A</Writer><Writer>B</Writer><Pages><Page Image=\"0\" /></Pages></ComicInfo>");
        var same = XDocument.Parse("<ComicInfo><Writer>A, B</Writer><Pages><Page Image=\"1\" /></Pages></ComicInfo>");
        var changed = XDocument.Parse("<ComicInfo><Writer>A, C</Writer></ComicInfo>");

        Assert.Empty(ComicInfoXml.Diff(before, same));
        Assert.Equal(new[] { new ComicInfoFieldChange("Writer", "A, B", "A, C") }, ComicInfoXml.Diff(before, changed));
    }

    [Fact]
    public void Diff_ReadsElementsInANamespaceByTheirLocalName()
    {
        var before = XDocument.Parse("<ComicInfo xmlns=\"urn:x\"><Series>Old</Series></ComicInfo>");
        var after = XDocument.Parse("<ComicInfo xmlns=\"urn:x\"><Series>New</Series></ComicInfo>");

        Assert.Equal(new[] { new ComicInfoFieldChange("Series", "Old", "New") }, ComicInfoXml.Diff(before, after));
        Assert.Throws<ArgumentNullException>(() => ComicInfoXml.Diff(before, null!));
    }
}
