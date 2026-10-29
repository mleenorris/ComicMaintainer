using System.Net;
using System.Text.Json;
using ComicMaintainer.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Routing of the Files view's per-folder file lists through the real app. A
/// folder is addressed by its base64url-encoded path
/// (<c>GET /api/files/folders/{encodedPath}/files</c>), but the library
/// root's path is empty and an empty segment (<c>folders//files</c>) routes
/// nowhere, so the root has a route of its own:
/// <c>GET /api/files/folders/files</c>. Every host gets its own directories,
/// so its own SQLite database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FolderFilesRouteTests : IDisposable
{
    private readonly string _root = Path.Join(AppContext.BaseDirectory, "folder-files-route-tests", Guid.NewGuid().ToString("N"));

    public FolderFilesRouteTests()
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task TheRootRouteListsTheFilesDirectlyInTheLibraryRoot()
    {
        await using var factory = CreateFactory(authorized: true);
        await SeedAsync(factory,
            Path.Join(WatchedDirectory, "Root Issue.cbz"),
            Path.Join(WatchedDirectory, "abc", "abc #1.cbz"));
        using var client = factory.CreateClient();

        // The folder list names the root by its empty path...
        using (var list = JsonDocument.Parse(await client.GetStringAsync("/api/files/folders")))
        {
            var paths = list.RootElement.GetProperty("folders").EnumerateArray()
                .Select(folder => folder.GetProperty("path").GetString()!)
                .Order(StringComparer.Ordinal);
            Assert.Equal(new[] { "", "abc" }, paths);
        }

        // ...and the root route serves that folder's files.
        var root = await GetFolderAsync(client, "/api/files/folders/files?sort=name");

        Assert.Equal(string.Empty, root.Path);
        Assert.Equal(new[] { "Root Issue.cbz" }, root.Files);
    }

    [Fact]
    public async Task TheEncodedFolderRouteStillListsItsFolder()
    {
        await using var factory = CreateFactory(authorized: true);
        await SeedAsync(factory,
            Path.Join(WatchedDirectory, "Root Issue.cbz"),
            Path.Join(WatchedDirectory, "abc", "abc #1.cbz"));
        using var client = factory.CreateClient();

        // "YWJj" is "abc" base64url-encoded.
        var abc = await GetFolderAsync(client, "/api/files/folders/YWJj/files");

        Assert.Equal("abc", abc.Path);
        Assert.Equal(new[] { "abc #1.cbz" }, abc.Files);
    }

    // "a" is the issue's example; "YWJjZ" is "YWJjZA" ("abcd") with its last
    // character cut off. Both used to decode to the root's empty path.
    [Theory]
    [InlineData("a")]
    [InlineData("YWJjZ")]
    public async Task AnUndecodableFolderKeyIsRejectedInsteadOfListingTheRoot(string encodedPath)
    {
        await using var factory = CreateFactory(authorized: true);
        await SeedAsync(factory, Path.Join(WatchedDirectory, "Root Issue.cbz"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/files/folders/{encodedPath}/files");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid folder path", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BothFolderRoutesRequireAuthentication()
    {
        await using var factory = CreateFactory(authorized: false);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/files/folders/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/files/folders/YWJj/files")).StatusCode);
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

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // As in ProgramTests: UseSetting is seen while Program registers its
            // services (the database lives in ConfigDirectory); the in-memory
            // source, added last, also wins over user-settings.json.
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

    /// <summary>Tracks the given files in the app's database, as a library scan would.</summary>
    private static async Task SeedAsync(WebApplicationFactory<Program> factory, params string[] filePaths)
    {
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        foreach (var filePath in filePaths)
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Directory = Path.GetDirectoryName(filePath)!,
                FileSize = 1024,
                LastModified = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>GETs a folder's file list: its echoed folder path and the names of its files.</summary>
    private static async Task<(string Path, string[] Files)> GetFolderAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Conditional GET applies, as for every library list.
        Assert.NotNull(response.Headers.ETag);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var files = body.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("name").GetString()!)
            .ToArray();
        return (body.RootElement.GetProperty("path").GetString()!, files);
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
