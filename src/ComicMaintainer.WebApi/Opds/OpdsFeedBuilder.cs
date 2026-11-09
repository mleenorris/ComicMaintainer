using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ComicMaintainer.WebApi.Opds;

/// <summary>What a feed says about itself; see <see cref="OpdsFeedBuilder.BuildFeed"/>.</summary>
/// <param name="Id">The feed's permanent Atom id.</param>
/// <param name="Title">The feed's title.</param>
/// <param name="MediaType">The feed's type: <see cref="OpdsMediaTypes.NavigationFeed"/> or <see cref="OpdsMediaTypes.AcquisitionFeed"/>.</param>
/// <param name="Self">This feed's URL, for the <c>self</c> link.</param>
/// <param name="Up">The parent feed, when there is one.</param>
/// <param name="Paging">Where this feed is in a paged result, when it is one.</param>
public sealed record OpdsFeedInfo(
    string Id,
    string Title,
    string MediaType,
    string Self,
    string? Up = null,
    OpdsPaging? Paging = null);

/// <param name="Page">The page being returned, from 1.</param>
/// <param name="TotalPages">How many pages the result has.</param>
/// <param name="ItemsPerPage">The size of a page.</param>
/// <param name="TotalResults">How many items the whole result has.</param>
/// <param name="PageLink">Builds the URL of another page.</param>
public sealed record OpdsPaging(int Page, int TotalPages, int ItemsPerPage, int TotalResults, Func<int, string> PageLink);

/// <summary>One comic in an acquisition feed.</summary>
/// <param name="FileId">The opaque id of the file.</param>
/// <param name="Title">The title to show.</param>
/// <param name="Updated">When the file last changed.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="MediaType">The media type of the archive.</param>
/// <param name="Publisher">The publisher, when known.</param>
/// <param name="Year">The year of publication, when known.</param>
/// <param name="PageCount">The number of pages; without any, no OPDS-PSE link is offered.</param>
/// <param name="PageMediaType">The OPDS-PSE stream's image type.</param>
/// <param name="LastReadPage">The last page the user read, from 1; null when the user has no progress.</param>
/// <param name="LastReadAt">When that was.</param>
public sealed record OpdsIssueEntry(
    int FileId,
    string Title,
    DateTimeOffset Updated,
    long Size,
    string MediaType,
    string? Publisher,
    int? Year,
    int PageCount,
    string PageMediaType,
    int? LastReadPage,
    DateTime? LastReadAt);

/// <summary>
/// Builds the documents of the OPDS 1.2 catalog: Atom feeds with the OPDS, OPDS-PSE and Dublin Core extensions, and
/// the OpenSearch description. Everything goes through <see cref="XElement"/>, which escapes text and attributes;
/// characters XML cannot represent at all are removed, because metadata in comic archives can contain them.
/// </summary>
public static class OpdsFeedBuilder
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";
    private static readonly XNamespace Opds = "http://opds-spec.org/2010/catalog";
    private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
    private static readonly XNamespace Pse = "http://vaemendis.net/opds-pse/ns";

    private const string AcquisitionRel = "http://opds-spec.org/acquisition/open-access";
    private const string ImageRel = "http://opds-spec.org/image";
    private const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";
    private const string StreamRel = "http://vaemendis.net/opds-pse/stream";
    private const string CatalogAuthor = "ComicMaintainer";

    /// <summary>A navigation entry that leads to another feed.</summary>
    public static XElement NavigationEntry(string id, string title, string href, string targetMediaType, string? content = null) =>
        new(Atom + "entry",
            new XElement(Atom + "id", Clean(id)),
            new XElement(Atom + "title", Clean(title)),
            new XElement(Atom + "updated", FormatDate(DateTimeOffset.UtcNow)),
            content is null ? null : new XElement(Atom + "content", new XAttribute("type", "text"), Clean(content)),
            Link("subsection", href, targetMediaType));

    /// <summary>A series, as a navigation entry that leads to its issues.</summary>
    public static XElement SeriesEntry(OpdsLinks links, string seriesId, string title, DateTimeOffset updated, string? synopsis, int? coverFileId) =>
        new(Atom + "entry",
            new XElement(Atom + "id", Clean($"urn:comicmaintainer:series:{Uri.EscapeDataString(seriesId)}")),
            new XElement(Atom + "title", Clean(title)),
            new XElement(Atom + "updated", FormatDate(updated)),
            string.IsNullOrWhiteSpace(synopsis) ? null : new XElement(Atom + "content", new XAttribute("type", "text"), Clean(synopsis)),
            Covers(links, coverFileId),
            Link("subsection", links.Series(seriesId), OpdsMediaTypes.AcquisitionFeed));

    /// <summary>A comic: its acquisition link, its cover and, when it has pages, its OPDS-PSE stream.</summary>
    public static XElement IssueEntry(OpdsLinks links, OpdsIssueEntry issue)
    {
        var entry = new XElement(Atom + "entry",
            new XElement(Atom + "id", $"urn:comicmaintainer:issue:{issue.FileId.ToString(CultureInfo.InvariantCulture)}"),
            new XElement(Atom + "title", Clean(issue.Title)),
            new XElement(Atom + "updated", FormatDate(issue.Updated)),
            string.IsNullOrWhiteSpace(issue.Publisher) ? null : new XElement(Dc + "publisher", Clean(issue.Publisher)),
            issue.Year is { } year ? new XElement(Dc + "issued", year.ToString(CultureInfo.InvariantCulture)) : null,
            Covers(links, issue.FileId),
            new XElement(Atom + "link",
                new XAttribute("rel", AcquisitionRel),
                new XAttribute("href", links.IssueFile(issue.FileId)),
                new XAttribute("type", issue.MediaType),
                new XAttribute("length", issue.Size.ToString(CultureInfo.InvariantCulture))));

        if (issue.PageCount > 0)
        {
            entry.Add(new XElement(Atom + "link",
                new XAttribute("rel", StreamRel),
                new XAttribute("href", links.IssuePageTemplate(issue.FileId)),
                new XAttribute("type", issue.PageMediaType),
                new XAttribute(Pse + "count", issue.PageCount.ToString(CultureInfo.InvariantCulture)),
                // PSE numbers pse:lastRead from 1, like the reader, unlike the page numbers in its URLs.
                issue.LastReadPage is > 0 and var lastRead ? new XAttribute(Pse + "lastRead", lastRead.ToString(CultureInfo.InvariantCulture)) : null,
                issue.LastReadPage > 0 && issue.LastReadAt is { } readAt ? new XAttribute(Pse + "lastReadDate", FormatDate(new DateTimeOffset(DateTime.SpecifyKind(readAt, DateTimeKind.Utc)))) : null));
        }

        return entry;
    }

    /// <summary>The Atom feed around <paramref name="entries"/>, as a UTF-8 XML document.</summary>
    public static string BuildFeed(OpdsLinks links, OpdsFeedInfo feed, IEnumerable<XElement> entries)
    {
        var root = new XElement(Atom + "feed",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "opds", Opds),
            new XAttribute(XNamespace.Xmlns + "opensearch", OpenSearch),
            new XAttribute(XNamespace.Xmlns + "pse", Pse),
            new XElement(Atom + "id", Clean(feed.Id)),
            new XElement(Atom + "title", Clean(feed.Title)),
            new XElement(Atom + "updated", FormatDate(DateTimeOffset.UtcNow)),
            new XElement(Atom + "author", new XElement(Atom + "name", CatalogAuthor)),
            Link("self", feed.Self, feed.MediaType),
            Link("start", links.Catalog, OpdsMediaTypes.NavigationFeed),
            feed.Up is null ? null : Link("up", feed.Up, OpdsMediaTypes.NavigationFeed),
            Link("search", links.OpenSearch, OpdsMediaTypes.OpenSearchDescription));

        if (feed.Paging is { } paging)
        {
            root.Add(
                new XElement(OpenSearch + "totalResults", paging.TotalResults.ToString(CultureInfo.InvariantCulture)),
                new XElement(OpenSearch + "itemsPerPage", paging.ItemsPerPage.ToString(CultureInfo.InvariantCulture)),
                new XElement(OpenSearch + "startIndex", (((paging.Page - 1) * paging.ItemsPerPage) + 1).ToString(CultureInfo.InvariantCulture)));

            if (paging.Page > 1)
            {
                root.Add(Link("previous", paging.PageLink(paging.Page - 1), feed.MediaType));
            }

            if (paging.Page < paging.TotalPages)
            {
                root.Add(Link("next", paging.PageLink(paging.Page + 1), feed.MediaType));
            }
        }

        root.Add(entries);
        return Serialize(root);
    }

    /// <summary>The OpenSearch description document that tells clients how to search the catalog.</summary>
    public static string BuildOpenSearchDescription(OpdsLinks links) =>
        Serialize(new XElement(OpenSearch + "OpenSearchDescription",
            new XElement(OpenSearch + "ShortName", CatalogAuthor),
            new XElement(OpenSearch + "Description", "Search the ComicMaintainer library"),
            new XElement(OpenSearch + "InputEncoding", "UTF-8"),
            new XElement(OpenSearch + "OutputEncoding", "UTF-8"),
            new XElement(OpenSearch + "Url",
                new XAttribute("type", OpdsMediaTypes.AcquisitionFeed),
                new XAttribute("template", links.SearchTemplate))));

    private static XElement Link(string rel, string href, string type) =>
        new(Atom + "link", new XAttribute("rel", rel), new XAttribute("href", href), new XAttribute("type", type));

    private static IEnumerable<XElement> Covers(OpdsLinks links, int? coverFileId) => coverFileId is { } id
        ? [Link(ImageRel, links.IssueCover(id), OpdsMediaTypes.Jpeg), Link(ThumbnailRel, links.IssueCover(id), OpdsMediaTypes.Jpeg)]
        : [];

    private static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Serialize(XElement root)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf8StringWriter(builder), new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
        }

        return builder.ToString();
    }

    /// <summary>Removes the characters XML 1.0 has no representation for, keeping surrogate pairs whole.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clean = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsSurrogatePair(value, i))
            {
                clean.Append(value[i]).Append(value[++i]);
            }
            else if (XmlConvert.IsXmlChar(value[i]))
            {
                clean.Append(value[i]);
            }
        }

        return clean.ToString();
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
