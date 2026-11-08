using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Tests.Utilities;

public class ComicInfoXmlTests
{
    /// <summary>A ComicInfo.xml as Suwayomi/ComicTagger write it, with many elements the app does not model.</summary>
    private const string TaggedDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Title>The Beginning</Title>
          <Series>Berserk</Series>
          <Number>1</Number>
          <Summary>Guts arrives.</Summary>
          <Writer>Kentaro Miura, Kouji Mori</Writer>
          <Penciller>Kentaro Miura</Penciller>
          <Translator>Duane Johnson</Translator>
          <Publisher>Hakusensha</Publisher>
          <Genre>Action, Dark Fantasy</Genre>
          <Tags>Seinen, Gore</Tags>
          <Web>https://example.org/berserk/1</Web>
          <LanguageISO>en</LanguageISO>
          <Manga>YesAndRightToLeft</Manga>
          <Pages>
            <Page Image="0" Type="FrontCover" ImageWidth="800" ImageHeight="1200" />
            <Page Image="1" />
          </Pages>
        </ComicInfo>
        """;

    [Fact]
    public void Read_ReadsStandardCommaSeparatedWriterAndTags()
    {
        var metadata = ComicInfoXml.Read(ComicInfoXml.Parse(TaggedDocument))!;

        Assert.Equal("Berserk", metadata.Series);
        Assert.Equal("The Beginning", metadata.Title);
        Assert.Equal("1", metadata.Issue);
        Assert.Equal("Hakusensha", metadata.Publisher);
        Assert.Equal(new[] { "Kentaro Miura", "Kouji Mori" }, metadata.Authors);
        Assert.Equal(new[] { "Seinen", "Gore" }, metadata.Tags);
    }

    [Fact]
    public void Read_AlsoReadsRepeatedWriterAndLegacyTagElements()
    {
        var document = XDocument.Parse(
            "<ComicInfo><Writer>A</Writer><Writer>B, C</Writer><Tags>x, y</Tags><Tag>z</Tag><Tag>X</Tag></ComicInfo>");

        var metadata = ComicInfoXml.Read(document)!;

        Assert.Equal(new[] { "A", "B", "C" }, metadata.Authors);
        Assert.Equal(new[] { "x", "y", "z" }, metadata.Tags);
    }

    [Fact]
    public void Merge_ChangesOnlyTheMaskedFieldsAndKeepsEveryOtherElementInOrder()
    {
        var original = ComicInfoXml.Parse(TaggedDocument);

        var merged = ComicInfoXml.Merge(
            original,
            new ComicMetadata { Title = "Chapter 1", Series = "Ignored", Publisher = "Ignored" },
            ComicMetadataFieldFlags.Title);

        var root = merged.Root!;
        Assert.Equal("Chapter 1", root.Element("Title")!.Value);
        Assert.Equal(
            original.Root!.Elements().Select(e => e.Name.LocalName),
            root.Elements().Select(e => e.Name.LocalName));
        foreach (var element in original.Root.Elements().Where(e => e.Name.LocalName != "Title"))
        {
            Assert.True(XNode.DeepEquals(element, root.Element(element.Name)), $"{element.Name} changed");
        }

        // The input document is not modified.
        Assert.Equal("The Beginning", original.Root.Element("Title")!.Value);
    }

    [Fact]
    public void Merge_EmptyValueForAMaskedFieldRemovesItsElement()
    {
        var merged = ComicInfoXml.Merge(
            ComicInfoXml.Parse(TaggedDocument),
            new ComicMetadata { Publisher = "  " },
            ComicMetadataFieldFlags.Publisher | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Authors);

        Assert.Null(merged.Root!.Element("Publisher"));
        Assert.Null(merged.Root.Element("Writer"));
        Assert.NotNull(merged.Root.Element("Penciller"));
    }

    [Fact]
    public void Merge_WritesAuthorsAndTagsAsSingleCommaSeparatedElementsAndDropsLegacyTags()
    {
        var document = XDocument.Parse(
            "<ComicInfo><Series>S</Series><Writer>Old</Writer><Writer>Older</Writer><Tag>legacy</Tag><Web>w</Web></ComicInfo>");

        var merged = ComicInfoXml.Merge(
            document,
            new ComicMetadata { Authors = { "Alice", " Bob ", "alice", "" }, Tags = { "t1", "t2" } },
            ComicMetadataFieldFlags.Authors | ComicMetadataFieldFlags.Tags);

        var root = merged.Root!;
        Assert.Equal("Alice, Bob", Assert.Single(root.Elements("Writer")).Value);
        Assert.Equal("t1, t2", Assert.Single(root.Elements("Tags")).Value);
        Assert.Empty(root.Elements("Tag"));
        // A new element goes where the ComicInfo schema puts it: Tags before Web.
        Assert.Equal(new[] { "Series", "Writer", "Tags", "Web" }, root.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Merge_AddsMissingElementsInSchemaOrder()
    {
        var document = XDocument.Parse("<ComicInfo><Series>S</Series><Summary>x</Summary><Manga>Yes</Manga></ComicInfo>");

        var merged = ComicInfoXml.Merge(
            document,
            new ComicMetadata { Title = "T", Issue = "5", Year = 2020, Publisher = "P" },
            ComicMetadataFieldFlags.Title | ComicMetadataFieldFlags.Issue | ComicMetadataFieldFlags.Year | ComicMetadataFieldFlags.Publisher);

        Assert.Equal(
            new[] { "Title", "Series", "Number", "Summary", "Year", "Publisher", "Manga" },
            merged.Root!.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Merge_WithoutADocument_CreatesOneWithOnlyTheSuppliedFields()
    {
        var merged = ComicInfoXml.Merge(null, new ComicMetadata { Series = "S", Issue = "1" }, ComicMetadataFields.All);

        Assert.Equal("ComicInfo", merged.Root!.Name.LocalName);
        Assert.Equal(new[] { "Series", "Number" }, merged.Root.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Merge_KeepsTheDocumentsNamespace()
    {
        var document = XDocument.Parse("<ComicInfo xmlns=\"urn:comicinfo\"><Series>S</Series></ComicInfo>");

        var merged = ComicInfoXml.Merge(document, new ComicMetadata { Series = "New", Title = "T" }, ComicMetadataFields.All);

        XNamespace ns = "urn:comicinfo";
        Assert.Equal("New", merged.Root!.Element(ns + "Series")!.Value);
        Assert.Equal("T", merged.Root.Element(ns + "Title")!.Value);
        Assert.Equal("New", ComicInfoXml.Read(merged)!.Series);
    }

    [Fact]
    public void AreEquivalent_IsTrueOnlyWhenNothingChanged()
    {
        var original = ComicInfoXml.Parse(TaggedDocument);
        var metadata = ComicInfoXml.Read(original)!;

        Assert.True(ComicInfoXml.AreEquivalent(original, ComicInfoXml.Merge(original, metadata, ComicMetadataFieldFlags.Series | ComicMetadataFieldFlags.Title)));
        Assert.False(ComicInfoXml.AreEquivalent(original, ComicInfoXml.Merge(original, new ComicMetadata { Series = "X" }, ComicMetadataFieldFlags.Series)));
        Assert.False(ComicInfoXml.AreEquivalent(null, original));
    }

    [Fact]
    public void ToUtf8Bytes_WritesAnXmlDeclarationAndRoundTrips()
    {
        var merged = ComicInfoXml.Merge(ComicInfoXml.Parse(TaggedDocument), new ComicMetadata { Title = "Ünïcode" }, ComicMetadataFieldFlags.Title);

        var bytes = ComicInfoXml.ToUtf8Bytes(merged);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte order mark");
        Assert.True(XNode.DeepEquals(merged.Root, XDocument.Parse(text).Root));
    }

    [Fact]
    public void ToUtf8Bytes_KeepsCommentsAndProcessingInstructionsOutsideTheRoot()
    {
        var document = ComicInfoXml.Parse("<!-- tagged by ComicTagger --><?app keep?><ComicInfo><Title>Old</Title></ComicInfo><!-- end -->");
        var merged = ComicInfoXml.Merge(document, new ComicMetadata { Title = "New" }, ComicMetadataFieldFlags.Title);

        var text = Encoding.UTF8.GetString(ComicInfoXml.ToUtf8Bytes(merged));

        Assert.Contains("<!-- tagged by ComicTagger -->", text);
        Assert.Contains("<?app keep?>", text);
        Assert.Contains("<!-- end -->", text);
        Assert.Contains("<Title>New</Title>", text);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
    }
}
