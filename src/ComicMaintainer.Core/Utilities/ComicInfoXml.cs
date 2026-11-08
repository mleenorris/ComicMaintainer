using System.Text;
using System.Xml;
using System.Xml.Linq;
using ComicMaintainer.Core.Models;

namespace ComicMaintainer.Core.Utilities;

/// <summary>
/// Reads the fields the application models from a ComicInfo.xml document and merges edits back
/// into it. A merge changes only the elements of the fields being written and keeps every other
/// element (credits, Genre, Web, LanguageISO, Manga, Pages, ...) and their order, so writing
/// metadata never discards tags the application does not model.
/// </summary>
/// <remarks>
/// Credits and tags follow the ComicInfo schema: one comma-separated &lt;Writer&gt; and one
/// comma-separated &lt;Tags&gt; element. Repeated &lt;Writer&gt; elements and the non-standard
/// &lt;Tag&gt; elements older versions of this application wrote are still read, and are folded
/// into the standard element when that field is written.
/// </remarks>
public static class ComicInfoXml
{
    /// <summary>The archive entry name of the metadata document.</summary>
    public const string EntryName = "ComicInfo.xml";

    /// <summary>
    /// The element order of the ComicInfo schema (v2.1), used to place an element that a merge
    /// adds. Elements already in the document keep their position.
    /// </summary>
    private static readonly string[] SchemaOrder =
    {
        "Title", "Series", "Number", "Count", "Volume", "AlternateSeries", "AlternateNumber",
        "AlternateCount", "Summary", "Notes", "Year", "Month", "Day", "Writer", "Penciller", "Inker",
        "Colorist", "Letterer", "CoverArtist", "Editor", "Translator", "Publisher", "Imprint", "Genre",
        "Tags", "Web", "PageCount", "LanguageISO", "Format", "BlackAndWhite", "Manga", "Characters",
        "Teams", "Locations", "ScanInformation", "StoryArc", "StoryArcNumber", "SeriesGroup",
        "AgeRating", "Pages", "CommunityRating", "MainCharacterOrTeam", "Review", "GTIN"
    };

    private const string LegacyTagElement = "Tag";

    /// <summary>
    /// Parses a ComicInfo.xml document. Throws <see cref="XmlException"/> for malformed XML.
    /// </summary>
    public static XDocument Parse(string xml) => XDocument.Parse(xml);

    /// <summary>
    /// Reads the modelled fields of <paramref name="document"/>. Returns null when it has no root.
    /// </summary>
    public static ComicMetadata? Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var ns = root.Name.Namespace;
        return new ComicMetadata
        {
            Series = root.Element(ns + "Series")?.Value,
            Title = root.Element(ns + "Title")?.Value,
            Issue = root.Element(ns + "Number")?.Value,
            Volume = root.Element(ns + "Volume")?.Value,
            Publisher = root.Element(ns + "Publisher")?.Value,
            Year = int.TryParse(root.Element(ns + "Year")?.Value, out var year) ? year : null,
            Summary = root.Element(ns + "Summary")?.Value,
            Authors = SplitList(root.Elements(ns + "Writer").Select(e => e.Value)),
            Tags = SplitList(root.Elements(ns + "Tags").Concat(root.Elements(ns + LegacyTagElement)).Select(e => e.Value))
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="existing"/> (or a new document when there is none) with
    /// the fields in <paramref name="fields"/> set from <paramref name="metadata"/>. An empty value
    /// removes the field's element. Every other element is kept as it is.
    /// </summary>
    public static XDocument Merge(XDocument? existing, ComicMetadata metadata, ComicMetadataFieldFlags fields)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var document = existing is { Root: not null }
            ? new XDocument(existing)
            : new XDocument(new XElement("ComicInfo",
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema")));
        var root = document.Root!;

        if (fields.HasFlag(ComicMetadataFieldFlags.Title)) SetElement(root, "Title", metadata.Title);
        if (fields.HasFlag(ComicMetadataFieldFlags.Series)) SetElement(root, "Series", metadata.Series);
        if (fields.HasFlag(ComicMetadataFieldFlags.Issue)) SetElement(root, "Number", metadata.Issue);
        if (fields.HasFlag(ComicMetadataFieldFlags.Volume)) SetElement(root, "Volume", metadata.Volume);
        if (fields.HasFlag(ComicMetadataFieldFlags.Summary)) SetElement(root, "Summary", metadata.Summary);
        if (fields.HasFlag(ComicMetadataFieldFlags.Year))
        {
            SetElement(root, "Year", metadata.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (fields.HasFlag(ComicMetadataFieldFlags.Authors)) SetElement(root, "Writer", JoinList(metadata.Authors));
        if (fields.HasFlag(ComicMetadataFieldFlags.Publisher)) SetElement(root, "Publisher", metadata.Publisher);
        if (fields.HasFlag(ComicMetadataFieldFlags.Tags))
        {
            root.Elements(root.Name.Namespace + LegacyTagElement).Remove();
            SetElement(root, "Tags", JoinList(metadata.Tags));
        }

        return document;
    }

    /// <summary>
    /// True when both documents hold the same elements, attributes and values, so writing
    /// <paramref name="updated"/> would not change the archive's metadata.
    /// </summary>
    public static bool AreEquivalent(XDocument? original, XDocument updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        return original?.Root is not null && XNode.DeepEquals(original.Root, updated.Root);
    }

    /// <summary>
    /// Serializes <paramref name="document"/> as indented UTF-8 with an XML declaration. Comments
    /// and processing instructions outside the root element are kept.
    /// </summary>
    public static byte[] ToUtf8Bytes(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), document.Nodes()).Save(writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Sets the first <paramref name="name"/> element to <paramref name="value"/> and removes any
    /// others of that name, or removes them all when the value is empty. A new element is placed
    /// before the first element that follows it in the schema order.
    /// </summary>
    private static void SetElement(XElement root, string name, string? value)
    {
        var ns = root.Name.Namespace;
        var elements = root.Elements(ns + name).ToList();
        if (string.IsNullOrWhiteSpace(value))
        {
            elements.Remove();
            return;
        }

        if (elements.Count > 0)
        {
            elements[0].RemoveNodes();
            elements[0].Value = value;
            elements.Skip(1).Remove();
            return;
        }

        var element = new XElement(ns + name, value);
        var position = Array.IndexOf(SchemaOrder, name);
        var next = root.Elements().FirstOrDefault(e =>
        {
            var index = Array.IndexOf(SchemaOrder, e.Name.LocalName);
            return index > position;
        });

        if (next is not null)
        {
            next.AddBeforeSelf(element);
        }
        else
        {
            root.Add(element);
        }
    }

    private static List<string> SplitList(IEnumerable<string> values) =>
        values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? JoinList(IEnumerable<string> values)
    {
        var items = values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return items.Count == 0 ? null : string.Join(", ", items);
    }
}
