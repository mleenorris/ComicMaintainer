using System.Xml.Linq;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ComicInfoXmlFillTests
{
    private static readonly ProviderComicInfoFields AllFields = new(
        Summary: "A summary.",
        Genre: "Action, Drama",
        Web: "https://example.org/series",
        Count: 12,
        Manga: ProviderComicInfoFields.MangaRightToLeft);

    [Fact]
    public void FillEmptyFields_PutsNewElementsInSchemaOrderAndKeepsTheRest()
    {
        var document = XDocument.Parse("<ComicInfo><Series>S</Series><Number>1</Number><Writer>W</Writer><Pages /></ComicInfo>");

        var filled = ComicInfoXml.FillEmptyFields(document, AllFields);

        Assert.Equal(
            new[] { "Series", "Number", "Count", "Summary", "Writer", "Genre", "Web", "Manga", "Pages" },
            filled.Root!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Action, Drama", filled.Root.Element("Genre")?.Value);
    }

    [Theory]
    [InlineData("<Summary></Summary>", "Summary")]
    [InlineData("<Summary>   </Summary>", "Summary")]
    [InlineData("<Genre />", "Genre")]
    [InlineData("<Web> </Web>", "Web")]
    [InlineData("<Count></Count>", "Count")]
    [InlineData("<Count>-1</Count>", "Count")]
    [InlineData("<Manga></Manga>", "Manga")]
    [InlineData("<Manga>Unknown</Manga>", "Manga")]
    [InlineData("<Manga>unknown</Manga>", "Manga")]
    public void FillEmptyFields_TreatsBlankAndUnsetValuesAsEmpty(string element, string name)
    {
        var document = XDocument.Parse($"<ComicInfo>{element}</ComicInfo>");

        var filled = ComicInfoXml.FillEmptyFields(document, AllFields);

        var value = filled.Root!.Element(name)?.Value;
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual("-1", value);
        Assert.NotEqual("unknown", value!.ToLowerInvariant());
    }

    [Theory]
    [InlineData("<Summary>Mine</Summary>", "Summary", "Mine")]
    [InlineData("<Genre>Mine</Genre>", "Genre", "Mine")]
    [InlineData("<Web>Mine</Web>", "Web", "Mine")]
    [InlineData("<Count>0</Count>", "Count", "0")]
    [InlineData("<Count>not a number</Count>", "Count", "not a number")]
    [InlineData("<Manga>No</Manga>", "Manga", "No")]
    [InlineData("<Manga>Yes</Manga>", "Manga", "Yes")]
    public void FillEmptyFields_LeavesAnyExistingValue(string element, string name, string expected)
    {
        var document = XDocument.Parse($"<ComicInfo>{element}</ComicInfo>");

        var filled = ComicInfoXml.FillEmptyFields(document, AllFields);

        Assert.Equal(expected, filled.Root!.Element(name)?.Value);
    }

    [Fact]
    public void FillEmptyFields_RunTwiceChangesNothing()
    {
        var once = ComicInfoXml.FillEmptyFields(XDocument.Parse("<ComicInfo />"), AllFields);

        var twice = ComicInfoXml.FillEmptyFields(once, AllFields with { Summary = "Different" });

        Assert.True(ComicInfoXml.AreEquivalent(once, twice));
    }

    [Fact]
    public void FillEmptyFields_NothingToFillIsEquivalent()
    {
        var document = XDocument.Parse("<ComicInfo><Series>S</Series></ComicInfo>");

        Assert.True(ComicInfoXml.AreEquivalent(document, ComicInfoXml.FillEmptyFields(document, new ProviderComicInfoFields())));
    }

    [Fact]
    public void FillEmptyFields_DoesNotChangeTheOriginal()
    {
        var document = XDocument.Parse("<ComicInfo />");

        ComicInfoXml.FillEmptyFields(document, AllFields);

        Assert.False(document.Root!.HasElements);
    }

    [Fact]
    public void FillEmptyFields_WithoutADocument_CreatesOne()
    {
        var filled = ComicInfoXml.FillEmptyFields(null, AllFields);

        Assert.Equal("ComicInfo", filled.Root!.Name.LocalName);
        Assert.Equal("12", filled.Root.Element("Count")?.Value);
    }

    [Fact]
    public void FillEmptyFields_KeepsTheDocumentsNamespace()
    {
        XNamespace ns = "urn:comicinfo";
        var document = new XDocument(new XElement(ns + "ComicInfo", new XElement(ns + "Summary", "Mine")));

        var filled = ComicInfoXml.FillEmptyFields(document, AllFields);

        Assert.Equal("Mine", filled.Root!.Element(ns + "Summary")?.Value);
        Assert.NotNull(filled.Root.Element(ns + "Genre"));
    }

    [Fact]
    public void FillEmptyFields_EscapesProviderText()
    {
        var filled = ComicInfoXml.FillEmptyFields(
            XDocument.Parse("<ComicInfo />"),
            new ProviderComicInfoFields(Summary: "a <b> & c"));

        var xml = System.Text.Encoding.UTF8.GetString(ComicInfoXml.ToUtf8Bytes(filled));

        Assert.Contains("a &lt;b&gt; &amp; c", xml);
    }

    [Fact]
    public void FromRecord_MapsEachField()
    {
        var fields = ProviderComicInfoFields.From(new SeriesMetadataCacheRecord
        {
            Synopsis = "**A** tale.",
            Genres = new List<string> { "Action", "Drama" },
            SiteUrl = "https://example.org/x",
            PublicationStatus = SeriesPublicationStatus.Completed,
            TotalChapters = 9,
            Origin = "JA"
        });

        Assert.Equal(new ProviderComicInfoFields("A tale.", "Action, Drama", "https://example.org/x", 9, "YesAndRightToLeft"), fields);
        Assert.False(fields.IsEmpty);
    }

    [Fact]
    public void FromRecord_WithNothingIsEmpty()
    {
        Assert.True(ProviderComicInfoFields.From(new SeriesMetadataCacheRecord()).IsEmpty);
    }

    [Fact]
    public void FromRecord_DropsAnUnsafeLinkAndAZeroCount()
    {
        var fields = ProviderComicInfoFields.From(new SeriesMetadataCacheRecord
        {
            SiteUrl = "javascript:alert(1)",
            PublicationStatus = SeriesPublicationStatus.Completed,
            TotalChapters = 0
        });

        Assert.Null(fields.Web);
        Assert.Null(fields.Count);
    }
}
