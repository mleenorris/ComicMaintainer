using ComicMaintainer.Core.ErrorReporting;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace ComicMaintainer.Core.Services;

public partial class ComicProcessorService
{
    private static string? ParseIssueNumber(string filename)
    {
        // Simple pattern matching for issue numbers
        var match = Regex.Match(filename, @"(?i)(?:ch|chapter|issue|#)?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        // Strip leading zero padding so the per-file metadata issue field holds
        // the bare issue number (e.g. "0012" -> "12"); only the generated
        // filename is zero padded.
        return match.Success ? ComicFileProcessor.StripLeadingZeros(match.Groups[1].Value) : null;
    }

    private static bool IsComicArchive(string filePath)
    {
        return ComicFileExtensions.IsComicArchive(filePath);
    }

    private static string ExtractSeriesFromFilename(string filePath)
    {
        // Extract series name from the parent folder name instead of filename
        var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (string.IsNullOrEmpty(folderName))
        {
            return UnknownSeries;
        }
        
        // Apply the same normalization as ComicFileProcessor to convert underscores to colons
        var series = ComicFileProcessor.NormalizeSeriesName(folderName, forComparison: false);
        return string.IsNullOrEmpty(series) ? UnknownSeries : series;
    }

    private ComicMetadata? ParseComicInfoXml(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            return new ComicMetadata
            {
                Series = root.Element("Series")?.Value,
                Title = root.Element("Title")?.Value,
                Issue = root.Element("Number")?.Value,
                Volume = root.Element("Volume")?.Value,
                Publisher = root.Element("Publisher")?.Value,
                Year = int.TryParse(root.Element("Year")?.Value, out var year) ? year : null,
                Summary = root.Element("Summary")?.Value,
                Authors = root.Elements("Writer").Select(e => e.Value).ToList(),
                Tags = root.Elements("Tag").Select(e => e.Value).ToList()
            };
        }
        catch (Exception ex)
        {
            // Malformed ComicInfo.xml comes from the archive, not from us.
            using (_logger.BeginScope(ErrorReportLogProperties.Exclude("malformed ComicInfo.xml in a user file")))
            {
                _logger.LogError(ex, "Error parsing ComicInfo.xml");
            }

            return null;
        }
    }

    private SeriesMetadata? ParseSeriesMetadataXml(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            return new SeriesMetadata
            {
                Series = root.Element("Series")?.Value,
                AlternateSeries = root.Element("AlternateSeries")?.Value,
                SeriesGroup = root.Element("SeriesGroup")?.Value,
                Title = root.Element("Title")?.Value,
                Issue = root.Element("Number")?.Value,
                Volume = root.Element("Volume")?.Value,
                Publisher = root.Element("Publisher")?.Value,
                Year = int.TryParse(root.Element("Year")?.Value, out var year) ? year : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing series metadata from ComicInfo.xml");
            return null;
        }
    }

    private static string ReadComicInfoXml(IArchiveEntry comicInfoEntry)
    {
        using var stream = comicInfoEntry.OpenEntryStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GenerateComicInfoXml(ComicMetadata metadata)
    {
        var doc = new XDocument(
            new XElement("ComicInfo",
                metadata.Series != null ? new XElement("Series", metadata.Series) : null,
                metadata.Title != null ? new XElement("Title", metadata.Title) : null,
                metadata.Issue != null ? new XElement("Number", metadata.Issue) : null,
                metadata.Volume != null ? new XElement("Volume", metadata.Volume) : null,
                metadata.Publisher != null ? new XElement("Publisher", metadata.Publisher) : null,
                metadata.Year.HasValue ? new XElement("Year", metadata.Year.Value) : null,
                metadata.Summary != null ? new XElement("Summary", metadata.Summary) : null,
                metadata.Authors.Select(a => new XElement("Writer", a)),
                metadata.Tags.Select(t => new XElement("Tag", t))
            )
        );
        return doc.ToString();
    }
}
