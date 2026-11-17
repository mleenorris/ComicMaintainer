using System.Globalization;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// ComicVine's issue-level catalogue (see <see cref="Interfaces.IComicVineCatalogClient"/>). These
/// calls run in background jobs, so they wait for the rate limiter's permits instead of giving
/// up after the interactive wait.
/// </summary>
public partial class ComicVineSeriesMetadataService
{
    /// <summary>The most issues ComicVine returns to one list request.</summary>
    private const int IssuePageSize = 100;

    private const string IssueListFields = "id,issue_number,name,cover_date,deck,description,site_detail_url";
    private const string IssueDetailFields = "id,person_credits,story_arc_credits";
    private const string StoryArcFields = "id,name,deck,description,site_detail_url,issues";

    /// <inheritdoc />
    public bool IsConfigured => CanQuery(Settings.CurrentValue);

    /// <inheritdoc />
    public async Task<ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>> ListVolumeIssuesAsync(
        int volumeId,
        int maxRequests,
        CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        var issues = new List<ComicVineIssueSummary>();
        var requests = 0;
        if (!CanQuery(config))
        {
            return new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(issues, requests);
        }

        while (requests < maxRequests)
        {
            var offset = requests * IssuePageSize;
            var page = await SendAsync(
                           config,
                           () => CreateJsonRequest(
                               $"{BaseUri(config)}/issues/?{ApiKeyQuery(config)}&filter=volume:{volumeId}" +
                               $"&field_list={IssueListFields}&sort=id:asc&limit={IssuePageSize}&offset={offset}"),
                           $"volume {volumeId}",
                           ParseIssueList,
                           maxPermitWait: null,
                           cancellationToken)
                       ?? Array.Empty<ComicVineIssueSummary>();
            requests++;
            issues.AddRange(page);
            if (page.Count < IssuePageSize)
            {
                break;
            }
        }

        return new ComicVineResult<IReadOnlyList<ComicVineIssueSummary>>(issues, requests);
    }

    /// <inheritdoc />
    public async Task<ComicVineIssueDetails?> GetIssueDetailsAsync(int issueId, CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        if (!CanQuery(config))
        {
            return null;
        }

        return await SendAsync(
            config,
            () => CreateJsonRequest($"{BaseUri(config)}/issue/4000-{issueId}/?{ApiKeyQuery(config)}&field_list={IssueDetailFields}"),
            $"issue {issueId}",
            ParseIssueDetails,
            maxPermitWait: null,
            cancellationToken,
            notFoundIsEmpty: true);
    }

    /// <inheritdoc />
    public async Task<ComicVineStoryArc?> GetStoryArcAsync(int storyArcId, CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        if (!CanQuery(config))
        {
            return null;
        }

        return await SendAsync(
            config,
            () => CreateJsonRequest($"{BaseUri(config)}/story_arc/4045-{storyArcId}/?{ApiKeyQuery(config)}&field_list={StoryArcFields}"),
            $"story arc {storyArcId}",
            ParseStoryArc,
            RateLimitedHttpInvoker.InteractivePermitWait,
            cancellationToken,
            notFoundIsEmpty: true);
    }

    private static string BaseUri(AppSettings config) => config.ComicVineBaseUrl.TrimEnd('/');

    private static string ApiKeyQuery(AppSettings config)
        => $"api_key={Uri.EscapeDataString(config.ComicVineApiKey ?? string.Empty)}&format=json";

    internal static IReadOnlyList<ComicVineIssueSummary> ParseIssueList(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ComicVineIssueSummary>();
        }

        var issues = new List<ComicVineIssueSummary>();
        foreach (var item in results.EnumerateArray())
        {
            if (ReadIntId(item) is not { } id || ReadString(item, "issue_number")?.Trim() is not { Length: > 0 } number)
            {
                continue;
            }

            issues.Add(new ComicVineIssueSummary(
                id,
                number,
                ReadString(item, "name")?.Trim() is { Length: > 0 } name ? name : null,
                ReadString(item, "cover_date")?.Trim() is { Length: > 0 } date ? date : null,
                ExtractSynopsis(item),
                ProviderMetadataText.HttpUrl(ReadString(item, "site_detail_url"))));
        }

        return issues;
    }

    internal static ComicVineIssueDetails? ParseIssueDetails(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var item) || item.ValueKind != JsonValueKind.Object || ReadIntId(item) is not { } id)
        {
            return null;
        }

        var writers = new List<string>();
        if (item.TryGetProperty("person_credits", out var people) && people.ValueKind == JsonValueKind.Array)
        {
            foreach (var person in people.EnumerateArray())
            {
                var roles = ReadString(person, "role") ?? string.Empty;
                if (ReadString(person, "name")?.Trim() is { Length: > 0 } name
                    && roles.Split(',').Any(role => role.Trim().Equals("writer", StringComparison.OrdinalIgnoreCase))
                    && !writers.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    writers.Add(name);
                }
            }
        }

        return new ComicVineIssueDetails(id, writers, ReadArcReferences(item, "story_arc_credits"));
    }

    internal static ComicVineStoryArc? ParseStoryArc(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var item) || item.ValueKind != JsonValueKind.Object
            || ReadIntId(item) is not { } id || ReadString(item, "name")?.Trim() is not { Length: > 0 } name)
        {
            return null;
        }

        var issues = new List<ComicVineArcIssue>();
        if (item.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in list.EnumerateArray())
            {
                if (ReadIntId(issue) is not { } issueId)
                {
                    continue;
                }

                var volume = issue.TryGetProperty("volume", out var volumeProperty) ? ReadString(volumeProperty, "name") : null;
                issues.Add(new ComicVineArcIssue(
                    issueId,
                    ReadString(issue, "name")?.Trim() is { Length: > 0 } issueName ? issueName : null,
                    ReadString(issue, "issue_number")?.Trim() is { Length: > 0 } number ? number : null,
                    volume?.Trim() is { Length: > 0 } volumeName ? volumeName : null));
            }
        }

        return new ComicVineStoryArc(id, name, ExtractSynopsis(item), ProviderMetadataText.HttpUrl(ReadString(item, "site_detail_url")), issues);
    }

    private static IReadOnlyList<ComicVineArcReference> ReadArcReferences(JsonElement item, string property)
    {
        var arcs = new List<ComicVineArcReference>();
        if (item.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var arc in list.EnumerateArray())
            {
                if (ReadIntId(arc) is { } id && ReadString(arc, "name")?.Trim() is { Length: > 0 } name && arcs.All(a => a.Id != id))
                {
                    arcs.Add(new ComicVineArcReference(id, name));
                }
            }
        }

        return arcs;
    }

    private static int? ReadIntId(JsonElement item)
        => int.TryParse(ReadId(item), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
