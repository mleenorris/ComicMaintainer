using System.Text.Json;
using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// Scheduled job that fills the library's issues with ComicVine's details a batch at a time
/// (see <see cref="ComicVineIssueDetailsService"/>). Off by default: it writes into archives and
/// spends ComicVine requests, so the user turns it on.
/// </summary>
public class ComicVineIssueDetailsJobHandler : IScheduledJobHandler
{
    public const string Key = "comicvine-issue-details";

    /// <summary>Requests a run may make when its options do not say: room is left for searches the user makes meanwhile.</summary>
    internal const int DefaultMaxRequests = 150;

    private readonly IComicVineIssueDetailsService _details;
    private readonly IComicVineCatalogClient _client;

    public ComicVineIssueDetailsJobHandler(IComicVineIssueDetailsService details, IComicVineCatalogClient client)
    {
        _details = details;
        _client = client;
    }

    public string JobKey => Key;
    public string DisplayName => "ComicVine Issue Details";
    public string Description =>
        "Fills issue titles, cover dates, summaries, writers and story arcs from ComicVine into fields that are empty " +
        "or only hold a placeholder. Uses up to maxRequests ComicVine requests a run; the rest continue next run.";
    public ScheduledJobDefaults Defaults => new(Enabled: false, IntervalMinutes: 60, OptionsJson: $$"""{"maxRequests":{{DefaultMaxRequests}}}""");

    public async Task<string> ExecuteAsync(string? optionsJson, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            return "ComicVine is not configured: set a ComicVine API key and turn on external metadata.";
        }

        var summary = await _details.RunAsync(null, ParseMaxRequests(optionsJson), cancellationToken);
        return summary.ToString();
    }

    internal static int ParseMaxRequests(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return DefaultMaxRequests;
        }

        try
        {
            var options = JsonSerializer.Deserialize<Options>(optionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return options is { MaxRequests: > 0 } ? options.MaxRequests : DefaultMaxRequests;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return DefaultMaxRequests;
        }
    }

    private sealed class Options
    {
        public int MaxRequests { get; set; }
    }
}
