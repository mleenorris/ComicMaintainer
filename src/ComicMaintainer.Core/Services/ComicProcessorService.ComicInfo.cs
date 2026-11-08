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
    private static bool IsComicArchive(string filePath)
    {
        return ComicFileExtensions.IsComicArchive(filePath);
    }

    private string? ExtractSeriesFromFilename(string filePath)
    {
        // A loose file directly in the watched or duplicates root takes its series from its own
        // name: the root folder's name is not a series (see SeriesFromPath). Null when the name
        // holds none, so the file is not renamed after a made-up series.
        if (IsInLibraryRoot(filePath))
        {
            return SeriesFromPath.FromFileName(filePath);
        }

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
            return ComicInfoXml.Read(ComicInfoXml.Parse(xmlContent));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
}
