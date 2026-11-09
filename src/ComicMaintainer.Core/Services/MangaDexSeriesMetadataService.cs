using ComicMaintainer.Core.Telemetry;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComicMaintainer.Core.Services;

/// <summary>
/// External series metadata provider backed by the public MangaDex REST API
/// (<c>/manga</c> search and <c>/manga/{id}</c>), throttled to
/// <see cref="AppSettings.MangaDexRequestsPerSecond"/>.
/// </summary>
public class MangaDexSeriesMetadataService : ExternalSeriesMetadataProviderBase
{
    private const string MangaDexCdnBase = "https://uploads.mangadex.org";
    private const string MangaDexSiteBase = "https://mangadex.org";

    private readonly Lazy<ProviderRateLimiter> _rateLimiter;

    public MangaDexSeriesMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppSettings> settings,
        IMemoryCache cache,
        ILogger<MangaDexSeriesMetadataService> logger,
        ComicMaintainerMetrics? metrics = null)
        : base("MangaDex", httpClientFactory, settings, cache, logger, metrics)
    {
        _rateLimiter = new Lazy<ProviderRateLimiter>(
            () => new ProviderRateLimiter(
                "MangaDex",
                Math.Max(1, Settings.CurrentValue.MangaDexRequestsPerSecond),
                TimeSpan.FromSeconds(1)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private protected override string HttpClientName => nameof(MangaDexSeriesMetadataService);

    private protected override string CacheKeyPrefix => "mangadex-series";

    private protected override ProviderRateLimiter RateLimiter => _rateLimiter.Value;

    private protected override bool CanQuery(AppSettings settings) => settings.EnableMangaDexMetadata;

    private protected override HttpRequestMessage CreateSearchRequest(AppSettings settings, string query, int limit)
        => CreateJsonRequest(BuildSearchUri(settings, query, limit));

    private protected override HttpRequestMessage CreateGetByIdRequest(AppSettings settings, string sourceId)
        => CreateJsonRequest($"{settings.MangaDexBaseUrl.TrimEnd('/')}/manga/{Uri.EscapeDataString(sourceId)}?includes[]=cover_art");

    private protected override IReadOnlyList<ExternalSeriesMetadata> ParseSearchResults(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExternalSeriesMetadata>();
        }

        var output = new List<ExternalSeriesMetadata>();
        foreach (var item in data.EnumerateArray())
        {
            if (ParseManga(item) is { } metadata)
            {
                output.Add(metadata);
            }
        }

        return output;
    }

    private protected override ExternalSeriesMetadata? ParseGetByIdResult(JsonElement root)
        => root.TryGetProperty("data", out var data) ? ParseManga(data) : null;

    /// <summary>
    /// MangaDex reports API errors as <c>{"result":"error","errors":[...]}</c>;
    /// such a body is a failure even when it arrives with HTTP 200.
    /// </summary>
    private protected override ExternalMetadataUnavailableException? CheckResponseBody(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String
            && string.Equals(result.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalMetadataUnavailableException(
                ProviderName,
                ExternalMetadataFailureKind.InvalidResponse,
                "MangaDex answered with an error result");
        }

        return null;
    }

    public override async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = Settings.CurrentValue;
        var enabled = config.EnableMangaDexMetadata;
        var configured = enabled && !string.IsNullOrWhiteSpace(config.MangaDexBaseUrl);

        var snapshot = Health.Snapshot();
        snapshot.Enabled = enabled;
        snapshot.Configured = configured;

        if (!enabled)
        {
            snapshot.Reachable = null;
            snapshot.StatusMessage = "Disabled in settings";
            return snapshot;
        }
        if (!configured)
        {
            snapshot.Reachable = null;
            snapshot.StatusMessage = "Missing base URL";
            return snapshot;
        }

        await ApplyProbeAsync(
            snapshot,
            "mangadex-health-probe",
            () => CreateJsonRequest(BuildSearchUri(config, "ping", limit: 1)),
            HealthCacheDuration,
            cancellationToken);

        // If we have observed a 429 within the back-off window, surface a
        // "degraded - rate limited" status even when reachability checks pass.
        if (snapshot.RateLimited)
        {
            snapshot.StatusMessage = "Degraded - rate limited";
        }
        return snapshot;
    }

    private static HttpRequestMessage CreateJsonRequest(string requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string BuildSearchUri(AppSettings settings, string seriesName, int limit)
    {
        var query = Uri.EscapeDataString(seriesName);
        var baseUrl = settings.MangaDexBaseUrl.TrimEnd('/');
        // Include the cover_art relationship so we can build a poster URL for
        // the local series-image cache without a follow-up request. Order by
        // relevance so the best title matches come first (the default order
        // is by most recent upload).
        return $"{baseUrl}/manga?title={query}&limit={limit}&includes[]=cover_art&order[relevance]=desc";
    }

    /// <summary>
    /// Maps one MangaDex manga record to a candidate; null when it is
    /// malformed or has no title.
    /// </summary>
    private static ExternalSeriesMetadata? ParseManga(JsonElement item)
    {
        // TryGetProperty throws on anything but an object, so a malformed
        // entry is skipped instead of failing the whole page.
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var localizedTitles = ExtractLocalizedTitles(attributes);
        var canonicalTitle = localizedTitles.FirstOrDefault()?.Title;
        if (string.IsNullOrWhiteSpace(canonicalTitle))
        {
            return null;
        }

        var aliases = localizedTitles.Skip(1).Select(lt => lt.Title).ToList();
        var (imageUrl, thumbnailUrl) = ExtractCoverUrls(item);

        return new ExternalSeriesMetadata
        {
            CanonicalTitle = canonicalTitle,
            Aliases = aliases,
            Source = "MangaDex",
            SourceId = ReadId(item),
            ImageUrl = imageUrl,
            ThumbnailUrl = thumbnailUrl,
            Synopsis = ExtractSynopsis(attributes),
            SiteUrl = BuildSiteUrl(item),
            Genres = ExtractGenres(attributes),
            PublicationStatus = SeriesPublicationStatus.Normalize(ReadString(attributes, "status")),
            TotalChapters = ParseLastChapter(ReadString(attributes, "lastChapter")),
            Origin = ProviderMetadataText.PrimaryLanguage(ReadString(attributes, "originalLanguage")),
            LocalizedTitles = localizedTitles
        };
    }

    /// <summary>The API has no site link; a title's page is addressed by its id.</summary>
    private static string? BuildSiteUrl(JsonElement item)
        => ReadId(item) is { } id ? ProviderMetadataText.HttpUrl($"{MangaDexSiteBase}/title/{Uri.EscapeDataString(id)}") : null;

    /// <summary>
    /// The names of the tags in MangaDex's <c>genre</c> group, in English when offered. Tags in
    /// the theme, format and content groups are not genres.
    /// </summary>
    private static List<string> ExtractGenres(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        var names = new List<string?>();
        foreach (var tag in tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.Object))
        {
            if (!tag.TryGetProperty("attributes", out var tagAttributes)
                || !string.Equals(ReadString(tagAttributes, "group"), "genre", StringComparison.OrdinalIgnoreCase)
                || !tagAttributes.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            names.Add(ReadString(name, "en")
                ?? name.EnumerateObject().Where(entry => entry.Value.ValueKind == JsonValueKind.String)
                    .Select(entry => entry.Value.GetString())
                    .FirstOrDefault());
        }

        return ProviderMetadataText.Genres(names);
    }

    /// <summary>
    /// The last chapter number as a total. MangaDex keeps it as text and it may be blank or a
    /// half chapter such as "107.5", neither of which is a reliable count.
    /// </summary>
    private static int? ParseLastChapter(string? lastChapter)
        => int.TryParse(lastChapter, NumberStyles.None, CultureInfo.InvariantCulture, out var chapter) && chapter > 0
            ? chapter
            : null;

    /// <summary>
    /// Reads the MangaDex localized <c>description</c> object, preferring the
    /// English entry and falling back to the first available language.
    /// </summary>
    private static string? ExtractSynopsis(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("description", out var description)
            || description.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? raw = null;
        if (description.TryGetProperty("en", out var enDesc) && enDesc.ValueKind == JsonValueKind.String)
        {
            raw = enDesc.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            foreach (var property in description.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    raw = property.Value.GetString();
                    break;
                }
            }
        }

        return SynopsisTextNormalizer.Normalize(raw);
    }

    /// <summary>
    /// Locates the cover_art relationship in a MangaDex manga record and
    /// returns the public CDN URL of the cover (and a smaller thumbnail
    /// variant). Returns (null, null) when no cover relationship is present.
    /// </summary>
    private static (string? Image, string? Thumbnail) ExtractCoverUrls(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
        {
            return (null, null);
        }
        var mangaId = idProp.GetString();
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return (null, null);
        }

        if (!item.TryGetProperty("relationships", out var rels) || rels.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var rel in rels.EnumerateArray())
        {
            if (rel.ValueKind != JsonValueKind.Object
                || !rel.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String
                || !string.Equals(typeProp.GetString(), "cover_art", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!rel.TryGetProperty("attributes", out var coverAttrs)
                || coverAttrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!coverAttrs.TryGetProperty("fileName", out var fileNameProp)
                || fileNameProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var fileName = fileNameProp.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }
            // Defensive: filename is provider-controlled. Use Path.GetFileName
            // to robustly strip any directory components (covers the
            // null-byte, encoded-separator, and parent-directory variants
            // beyond simple Replace), then drop residual leading dots.
            fileName = Path.GetFileName(fileName)
                .Replace("..", string.Empty)
                .TrimStart('.');
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }
            var image = $"{MangaDexCdnBase}/covers/{Uri.EscapeDataString(mangaId)}/{Uri.EscapeDataString(fileName)}";
            // MangaDex supports server-side thumbnails by appending a size token.
            var thumb = $"{image}.256.jpg";
            return (image, thumb);
        }

        return (null, null);
    }

    /// <summary>
    /// Build a language-tagged title list from a MangaDex manga attributes
    /// object. The MangaDex API keys both <c>title</c> and <c>altTitles</c>
    /// entries by BCP-47 language code (e.g. <c>en</c>, <c>ja</c>,
    /// <c>ja-ro</c>, <c>ko</c>, <c>zh</c>, <c>zh-hk</c>). We preserve those
    /// codes verbatim; the preferred-language matcher collapses regional
    /// variants to their primary subtag.
    /// <para>
    /// English titles are always promoted to the front of the list (whether
    /// they come from <c>title.en</c> or from an <c>altTitles</c> entry).
    /// Because <see cref="ParseManga"/> picks the first localized title as
    /// the candidate's <see cref="ExternalSeriesMetadata.CanonicalTitle"/>,
    /// this means a series whose primary MangaDex title is a romanization
    /// (e.g. Chinese pinyin) but has an English alt-title will default to
    /// the English alt-title as its display name.
    /// </para>
    /// </summary>
    private static List<LocalizedTitle> ExtractLocalizedTitles(JsonElement attributes)
    {
        var englishTitles = new List<LocalizedTitle>();
        var otherTitles = new List<LocalizedTitle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string? value, string? language)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!seen.Add(value)) return;
            var entry = new LocalizedTitle(value, language);
            if (IsEnglish(language))
            {
                englishTitles.Add(entry);
            }
            else
            {
                otherTitles.Add(entry);
            }
        }

        if (attributes.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            // Process the explicit English variant first so that, even if a
            // provider quirk repeats the same string under another language,
            // the English entry is the one we keep.
            if (titleObj.TryGetProperty("en", out var enTitle) && enTitle.ValueKind == JsonValueKind.String)
            {
                AddIfNew(enTitle.GetString(), "en");
            }
            foreach (var property in StringProperties(titleObj))
            {
                AddIfNew(property.Value.GetString(), property.Name);
            }
        }

        if (attributes.TryGetProperty("altTitles", out var altTitles) && altTitles.ValueKind == JsonValueKind.Array)
        {
            foreach (var altTitleObj in altTitles.EnumerateArray().Where(alt => alt.ValueKind == JsonValueKind.Object))
            {
                foreach (var property in StringProperties(altTitleObj))
                {
                    AddIfNew(property.Value.GetString(), property.Name);
                }
            }
        }

        // English first (preserving insertion order within each group) so the
        // first entry — which becomes the candidate's CanonicalTitle — is the
        // English title whenever the provider supplied one.
        var result = new List<LocalizedTitle>(englishTitles.Count + otherTitles.Count);
        result.AddRange(englishTitles);
        result.AddRange(otherTitles);
        return result;

        static IEnumerable<JsonProperty> StringProperties(JsonElement obj) =>
            obj.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String);
    }

    /// <summary>
    /// Returns true when the BCP-47 language tag represents English (primary
    /// subtag <c>en</c>), e.g. <c>en</c>, <c>en-US</c>, <c>en-GB</c>.
    /// </summary>
    private static bool IsEnglish(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var primary = language.Split('-', 2)[0];
        return string.Equals(primary, "en", StringComparison.OrdinalIgnoreCase);
    }
}
