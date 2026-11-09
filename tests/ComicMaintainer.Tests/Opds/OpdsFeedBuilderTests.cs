using System.Xml;
using System.Xml.Linq;
using ComicMaintainer.WebApi.Opds;

namespace ComicMaintainer.Tests.Opds;

/// <summary>The documents the OPDS catalog serves, against what the OPDS 1.2 and OPDS-PSE specifications require.</summary>
public sealed class OpdsFeedBuilderTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";
    private const string StreamRel = "http://vaemendis.net/opds-pse/stream";
    private const string AcquisitionRel = "http://opds-spec.org/acquisition/open-access";

    private static readonly OpdsLinks Links = new(string.Empty);

    private static OpdsIssueEntry Issue(int? lastRead = null, DateTime? lastReadAt = null, int pageCount = 12, string? publisher = null, int? year = null) => new(
        5, "Saga #1", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 1234, OpdsMediaTypes.ComicBookZip, publisher, year, pageCount, OpdsMediaTypes.Jpeg, lastRead, lastReadAt);

    private static XElement Feed(OpdsFeedInfo info, params XElement[] entries) => XDocument.Parse(OpdsFeedBuilder.BuildFeed(Links, info, entries)).Root!;

    private static IEnumerable<XElement> LinkElements(XElement parent) => parent.Elements(Atom + "link");

    private static XElement Link(XElement parent, string rel) => LinkElements(parent).Single(l => (string?)l.Attribute("rel") == rel);

    private static readonly OpdsFeedInfo NavigationFeed = new("urn:id", "Title", OpdsMediaTypes.NavigationFeed, "/self");

    [Fact]
    public void AFeedDeclaresEveryNamespaceAndTheAtomMetadata()
    {
        var feed = Feed(NavigationFeed);

        Assert.Equal(Atom + "feed", feed.Name);
        Assert.Equal("urn:id", feed.Element(Atom + "id")!.Value);
        Assert.Equal("Title", feed.Element(Atom + "title")!.Value);
        Assert.NotNull(feed.Element(Atom + "updated"));
        Assert.Equal("ComicMaintainer", feed.Element(Atom + "author")!.Element(Atom + "name")!.Value);
        Assert.Equal(Pse.NamespaceName, feed.GetNamespaceOfPrefix("pse")!.NamespaceName);
        Assert.Equal(Dc.NamespaceName, feed.GetNamespaceOfPrefix("dc")!.NamespaceName);
        Assert.Equal("http://opds-spec.org/2010/catalog", feed.GetNamespaceOfPrefix("opds")!.NamespaceName);
    }

    [Fact]
    public void AFeedLinksToItselfTheCatalogAndTheSearch()
    {
        var feed = Feed(NavigationFeed with { Up = "/up" });

        Assert.Equal("/self", Link(feed, "self").Attribute("href")!.Value);
        Assert.Equal(OpdsMediaTypes.NavigationFeed, Link(feed, "self").Attribute("type")!.Value);
        Assert.Equal(Links.Catalog, Link(feed, "start").Attribute("href")!.Value);
        Assert.Equal("/up", Link(feed, "up").Attribute("href")!.Value);
        Assert.Equal(OpdsMediaTypes.OpenSearchDescription, Link(feed, "search").Attribute("type")!.Value);
    }

    [Theory]
    [InlineData(1, 3, false, true)]
    [InlineData(2, 3, true, true)]
    [InlineData(3, 3, true, false)]
    [InlineData(1, 1, false, false)]
    public void PagingLinksOnlyWhereThereIsAPageToGoTo(int page, int totalPages, bool previous, bool next)
    {
        var feed = Feed(NavigationFeed with { Paging = new OpdsPaging(page, totalPages, 50, 120, p => $"/feed?page={p}") });

        Assert.Equal(previous, LinkElements(feed).Any(l => (string?)l.Attribute("rel") == "previous"));
        Assert.Equal(next, LinkElements(feed).Any(l => (string?)l.Attribute("rel") == "next"));
        if (previous)
        {
            Assert.Equal($"/feed?page={page - 1}", Link(feed, "previous").Attribute("href")!.Value);
        }

        if (next)
        {
            Assert.Equal($"/feed?page={page + 1}", Link(feed, "next").Attribute("href")!.Value);
        }
    }

    [Fact]
    public void APagedFeedReportsItsPositionForOpenSearch()
    {
        XNamespace openSearch = "http://a9.com/-/spec/opensearch/1.1/";
        var feed = Feed(NavigationFeed with { Paging = new OpdsPaging(3, 4, 50, 170, p => $"/feed?page={p}") });

        Assert.Equal("170", feed.Element(openSearch + "totalResults")!.Value);
        Assert.Equal("50", feed.Element(openSearch + "itemsPerPage")!.Value);
        Assert.Equal("101", feed.Element(openSearch + "startIndex")!.Value);
    }

    [Fact]
    public void AnIssueEntryHasTheRequiredAtomFieldsAndAnAcquisitionLink()
    {
        var entry = OpdsFeedBuilder.IssueEntry(Links, Issue(publisher: "Image", year: 2012));

        Assert.Equal("urn:comicmaintainer:issue:5", entry.Element(Atom + "id")!.Value);
        Assert.Equal("Saga #1", entry.Element(Atom + "title")!.Value);
        Assert.Equal("2023-11-14T22:13:20Z", entry.Element(Atom + "updated")!.Value);
        Assert.Equal("Image", entry.Element(Dc + "publisher")!.Value);
        Assert.Equal("2012", entry.Element(Dc + "issued")!.Value);
        var acquisition = Link(entry, AcquisitionRel);
        Assert.Equal(OpdsMediaTypes.ComicBookZip, acquisition.Attribute("type")!.Value);
        Assert.Equal("1234", acquisition.Attribute("length")!.Value);
        Assert.Equal(Links.IssueCover(5), Link(entry, "http://opds-spec.org/image").Attribute("href")!.Value);
        Assert.Equal(Links.IssueCover(5), Link(entry, "http://opds-spec.org/image/thumbnail").Attribute("href")!.Value);
    }

    [Fact]
    public void AStreamLinkHasCountAndPageTemplate()
    {
        var stream = Link(OpdsFeedBuilder.IssueEntry(Links, Issue()), StreamRel);

        Assert.Equal("12", stream.Attribute(Pse + "count")!.Value);
        Assert.Equal("image/jpeg", stream.Attribute("type")!.Value);
        Assert.EndsWith("/pages/{pageNumber}", stream.Attribute("href")!.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void ProgressIsReportedNumberedFromOneAsTheSpecificationSays(int page)
    {
        var readAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var stream = Link(OpdsFeedBuilder.IssueEntry(Links, Issue(page, readAt)), StreamRel);

        Assert.Equal(page.ToString(), stream.Attribute(Pse + "lastRead")!.Value);
        Assert.Equal("2026-01-02T03:04:05Z", stream.Attribute(Pse + "lastReadDate")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void NoProgressMeansNoProgressAttributes(int? page)
    {
        var stream = Link(OpdsFeedBuilder.IssueEntry(Links, Issue(page, DateTime.UtcNow)), StreamRel);

        Assert.Null(stream.Attribute(Pse + "lastRead"));
        Assert.Null(stream.Attribute(Pse + "lastReadDate"));
    }

    [Fact]
    public void AComicWithoutPagesHasNoStreamButCanStillBeDownloaded()
    {
        var entry = OpdsFeedBuilder.IssueEntry(Links, Issue(pageCount: 0));

        Assert.DoesNotContain(LinkElements(entry), l => (string?)l.Attribute("rel") == StreamRel);
        Assert.NotNull(Link(entry, AcquisitionRel));
    }

    [Fact]
    public void MarkupInTitlesIsEscapedNotInterpreted()
    {
        const string hostile = "<script>alert('x')</script> & \"more\" ]]>";

        var entry = OpdsFeedBuilder.SeriesEntry(Links, "a/b c", hostile, DateTimeOffset.UtcNow, hostile, null);
        var document = XDocument.Parse(OpdsFeedBuilder.BuildFeed(Links, new OpdsFeedInfo("urn:x", hostile, OpdsMediaTypes.NavigationFeed, "/self"), [entry]));

        var parsed = document.Root!.Elements(Atom + "entry").Single();
        Assert.Equal(hostile, parsed.Element(Atom + "title")!.Value);
        Assert.Equal(hostile, parsed.Element(Atom + "content")!.Value);
        Assert.Equal(hostile, document.Root.Element(Atom + "title")!.Value);
        Assert.Empty(parsed.Descendants("script"));
        // A series id is an opaque value that can contain anything, in the link as in the Atom id.
        Assert.Contains("a%2Fb%20c", Link(parsed, "subsection").Attribute("href")!.Value, StringComparison.Ordinal);
        Assert.Equal("urn:comicmaintainer:series:a%2Fb%20c", parsed.Element(Atom + "id")!.Value);
    }

    [Fact]
    public void ASeriesEntryListsEachGenreOnceAsAnAtomCategory()
    {
        const string hostile = "<b>&\"";

        var entry = OpdsFeedBuilder.SeriesEntry(Links, "saga", "Saga", DateTimeOffset.UtcNow, null, null, ["Action", "action", "  ", "Slice\u0001 of Life", hostile, "\u0001"]);

        var categories = Feed(NavigationFeed, entry).Elements(Atom + "entry").Single().Elements(Atom + "category").ToList();
        Assert.Equal(new[] { "Action", "Slice of Life", hostile }, categories.Select(c => c.Attribute("term")!.Value));
        Assert.Equal(new[] { "Action", "Slice of Life", hostile }, categories.Select(c => c.Attribute("label")!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeriesEntryWithoutGenresHasNoCategories(bool emptyList)
    {
        var entry = OpdsFeedBuilder.SeriesEntry(Links, "saga", "Saga", DateTimeOffset.UtcNow, null, null, emptyList ? [] : null);

        Assert.Empty(Feed(NavigationFeed, entry).Elements(Atom + "entry").Single().Elements(Atom + "category"));
    }

    [Fact]
    public void CharactersXmlCannotCarryAreRemovedSoTheFeedStillParses()
    {
        var entry = OpdsFeedBuilder.NavigationEntry("urn:x", "Bad\u0001Title￾", "/x", OpdsMediaTypes.NavigationFeed, "Also\u0000bad");

        var feed = Feed(NavigationFeed, entry).Elements(Atom + "entry").Single();

        Assert.Equal("BadTitle", feed.Element(Atom + "title")!.Value);
        Assert.Equal("Alsobad", feed.Element(Atom + "content")!.Value);
    }

    [Fact]
    public void Clean_KeepsCharactersOutsideTheBasicPlane()
    {
        Assert.Equal("Saga \U0001F4DA", OpdsFeedBuilder.Clean("Saga \U0001F4DA"));
    }

    [Fact]
    public void Clean_DropsALoneSurrogateAndHandlesNothing()
    {
        Assert.Equal("ab", OpdsFeedBuilder.Clean("a\uD83Db"));
        Assert.Equal(string.Empty, OpdsFeedBuilder.Clean(null));
    }

    [Fact]
    public void ANavigationEntryLeadsToASubsection()
    {
        var entry = OpdsFeedBuilder.NavigationEntry("urn:x", "New", "/new", OpdsMediaTypes.NavigationFeed);

        // Not http://opds-spec.org/sort/new: OPDS 1.2 defines that relation as an acquisition feed of new entries.
        Assert.Equal("subsection", LinkElements(entry).Single().Attribute("rel")!.Value);
    }

    [Fact]
    public void TheDocumentDeclaresItsEncodingAsUtf8()
    {
        var xml = OpdsFeedBuilder.BuildFeed(Links, NavigationFeed, []);

        using var reader = XmlReader.Create(new StringReader(xml));
        reader.MoveToContent();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.cbz", OpdsMediaTypes.ComicBookZip)]
    [InlineData("a.CBZ", OpdsMediaTypes.ComicBookZip)]
    [InlineData("a.zip", OpdsMediaTypes.ComicBookZip)]
    [InlineData("a.cbr", OpdsMediaTypes.ComicBookRar)]
    [InlineData("a.rar", OpdsMediaTypes.ComicBookRar)]
    [InlineData("a.epub", OpdsMediaTypes.OctetStream)]
    public void ArchivesGetTheirComicMediaType(string name, string expected)
    {
        Assert.Equal(expected, OpdsMediaTypes.ForArchive(name));
    }

    [Theory]
    [InlineData(new[] { "1.jpg", "2.jpeg" }, OpdsMediaTypes.Jpeg)]
    [InlineData(new[] { "1.png", "2.PNG" }, OpdsMediaTypes.Png)]
    [InlineData(new[] { "1.gif" }, OpdsMediaTypes.Gif)]
    [InlineData(new[] { "1.webp", "2.webp" }, OpdsMediaTypes.Jpeg)]
    [InlineData(new[] { "1.png", "2.jpg" }, OpdsMediaTypes.Jpeg)]
    [InlineData(new string[0], OpdsMediaTypes.Jpeg)]
    public void AStreamIsServedAsOneType(string[] pages, string expected)
    {
        Assert.Equal(expected, OpdsMediaTypes.ForPageStream(pages));
    }

    [Fact]
    public void LinksStartWithTheBasePath()
    {
        var links = new OpdsLinks("/comics");

        Assert.Equal("/comics/opds/v1.2/catalog", links.Catalog);
        Assert.Equal("/comics/opds/v1.2/series?page=3", links.AllSeries(3));
        Assert.Equal("/comics/opds/v1.2/search?q=a%20b&page=2", links.Search("a b", 2));
        Assert.Equal("/comics/opds/v1.2/search?q=a", links.Search("a"));
        Assert.Equal("/comics/opds/v1.2/search?q={searchTerms}", links.SearchTemplate);
        Assert.Equal("/comics/opds/v1.2/issues/9/pages/{pageNumber}", links.IssuePageTemplate(9));
    }

    [Fact]
    public void TheOpenSearchDescriptionFollowsTheSpecification()
    {
        var description = XDocument.Parse(OpdsFeedBuilder.BuildOpenSearchDescription(new OpdsLinks("/comics"))).Root!;
        XNamespace openSearch = "http://a9.com/-/spec/opensearch/1.1/";

        Assert.Equal(openSearch + "OpenSearchDescription", description.Name);
        Assert.False(string.IsNullOrEmpty(description.Element(openSearch + "ShortName")!.Value));
        var url = description.Element(openSearch + "Url")!;
        Assert.Equal(OpdsMediaTypes.AcquisitionFeed, url.Attribute("type")!.Value);
        Assert.Equal("/comics/opds/v1.2/search?q={searchTerms}", url.Attribute("template")!.Value);
    }
}
