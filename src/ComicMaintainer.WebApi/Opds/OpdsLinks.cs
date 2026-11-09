using System.Globalization;

namespace ComicMaintainer.WebApi.Opds;

/// <summary>
/// The links of the OPDS catalog. They are path-absolute and start with the configured base path: a client resolves
/// them against the URL it reached the catalog at, so the public host and scheme are always its own, and the links
/// are right whether a reverse proxy forwards or strips the base path.
/// </summary>
internal sealed class OpdsLinks(string basePath)
{
    /// <summary>The route prefix of every OPDS action.</summary>
    public const string RoutePrefix = "opds/v1.2";

    private string Url(string path) => $"{basePath}/{RoutePrefix}/{path}";

    public string Catalog => Url("catalog");

    public string OpenSearch => Url("opensearch.xml");

    public string ContinueReading => Url("continue-reading");

    public string SeriesUpdates => Url("series-updates");

    public string NewlyAdded => Url("newly-added");

    /// <summary>The URL template of the search, which clients fill in; OpenSearch's term goes in <c>q</c>.</summary>
    public string SearchTemplate => Url("search") + "?q={searchTerms}";

    public string AllSeries(int page = 1) => WithPage(Url("series"), page);

    public string Series(string seriesId, int page = 1) => WithPage(Url($"series/{Uri.EscapeDataString(seriesId)}"), page);

    public string Search(string query, int page = 1) =>
        $"{Url("search")}?q={Uri.EscapeDataString(query)}" + (page > 1 ? $"&page={page.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    public string IssueCover(int fileId) => Url($"issues/{fileId.ToString(CultureInfo.InvariantCulture)}/cover");

    public string IssueFile(int fileId) => Url($"issues/{fileId.ToString(CultureInfo.InvariantCulture)}/file");

    /// <summary>The OPDS-PSE page URL template; the client replaces <c>{pageNumber}</c>, which starts at 0.</summary>
    public string IssuePageTemplate(int fileId) => Url($"issues/{fileId.ToString(CultureInfo.InvariantCulture)}/pages/{{pageNumber}}");

    private static string WithPage(string url, int page) =>
        page > 1 ? $"{url}?page={page.ToString(CultureInfo.InvariantCulture)}" : url;
}
