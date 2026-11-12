using System.Net;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Tests.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The genre and status filters through the real app: routing, the migrated SQLite schema, the
/// metadata cache and the series library. Every host gets its own directories and database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SeriesFacetsRouteTests : IDisposable
{
    private readonly string _root = Path.Join(AppContext.BaseDirectory, "series-facets-route-tests", Guid.NewGuid().ToString("N"));

    public SeriesFacetsRouteTests()
    {
        Directory.CreateDirectory(WatchedDirectory);
        Directory.CreateDirectory(DuplicateDirectory);
        Directory.CreateDirectory(ConfigDirectory);
    }

    private string WatchedDirectory => Path.Join(_root, "watched");

    private string DuplicateDirectory => Path.Join(_root, "duplicates");

    private string ConfigDirectory => Path.Join(_root, "config");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_root);
    }

    [Fact]
    public async Task TheFiltersWorkAgainstTheMigratedDatabase()
    {
        await using var factory = CreateFactory(authorized: true);
        await SeedSeriesAsync(factory);
        using var client = factory.CreateClient();

        using var facetsResponse = await client.GetAsync("/api/files/series/facets");
        Assert.Equal(HttpStatusCode.OK, facetsResponse.StatusCode);
        Assert.NotNull(facetsResponse.Headers.ETag);
        using (var facets = JsonDocument.Parse(await facetsResponse.Content.ReadAsStringAsync()))
        {
            var genre = Assert.Single(facets.RootElement.GetProperty("genres").EnumerateArray());
            Assert.Equal("Drama", genre.GetProperty("value").GetString());
            Assert.Equal(1, genre.GetProperty("count").GetInt32());
            var status = Assert.Single(facets.RootElement.GetProperty("statuses").EnumerateArray());
            Assert.Equal("completed", status.GetProperty("value").GetString());
            Assert.Equal(0, facets.RootElement.GetProperty("series_without_genres").GetInt32());
        }

        using (var matching = JsonDocument.Parse(await client.GetStringAsync("/api/files/series?genre=drama&status=completed&limit=10")))
        {
            Assert.Equal(1, matching.RootElement.GetProperty("total_series").GetInt32());
            var series = Assert.Single(matching.RootElement.GetProperty("series").EnumerateArray());
            Assert.Equal("Drama", Assert.Single(series.GetProperty("genres").EnumerateArray()).GetString());
            Assert.Equal("completed", series.GetProperty("publication_status").GetString());
            Assert.Equal("https://example.org/saga", series.GetProperty("site_url").GetString());
        }

        using var excluded = JsonDocument.Parse(await client.GetStringAsync("/api/files/series?genre=Horror&limit=10"));
        Assert.Equal(0, excluded.RootElement.GetProperty("total_series").GetInt32());
        using var noData = JsonDocument.Parse(await client.GetStringAsync("/api/files/series?status=none&limit=10"));
        Assert.Equal(0, noData.RootElement.GetProperty("total_series").GetInt32());
    }

    [Fact]
    public async Task TheFacetsRouteRequiresAuthentication()
    {
        await using var factory = CreateFactory(authorized: false);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/files/series/facets")).StatusCode);
    }

    private async Task SeedSeriesAsync(WebApplicationFactory<Program> factory)
    {
        var folder = Path.Join(WatchedDirectory, "Saga");
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "Saga 001.cbz");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        var store = factory.Services.GetRequiredService<IFileStoreService>();
        await store.AddFileAsync(path);
        await store.RecordArchiveMetadataAsync(path, new ComicMetadata { Series = "Saga", Issue = "1", Title = "Chapter 1" });

        var cache = factory.Services.GetRequiredService<ISeriesMetadataCacheService>();
        await cache.PersistExternalLookupAsync("Saga", new ExternalSeriesMetadata
        {
            CanonicalTitle = "Saga",
            Source = "MangaDex",
            SourceId = "entry-1",
            Genres = new List<string> { "Drama" },
            PublicationStatus = SeriesPublicationStatus.Completed,
            TotalChapters = 66,
            Origin = "en",
            SiteUrl = "https://example.org/saga"
        });
    }

    private WebApplicationFactory<Program> CreateFactory(bool authorized)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = DuplicateDirectory,
            ["AppSettings:ConfigDirectory"] = ConfigDirectory,
            ["CONFIG_DIR"] = ConfigDirectory,
        };

        return new TestWebApplicationFactory(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            if (authorized)
            {
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>());
            }
        });
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
