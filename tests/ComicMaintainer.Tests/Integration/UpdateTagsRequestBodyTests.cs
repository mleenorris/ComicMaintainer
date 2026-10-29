using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// The tag endpoints, called as docs/API.md documents them, against real archives:
/// <c>GET</c> and <c>PUT /api/files/{encodedFilePath}/tags</c>, and
/// <c>POST /api/files/tags</c> with a <c>{"files", "metadata"}</c> body. A POST body
/// without a file list, such as the <c>{"file", "tags"}</c> format the docs used to
/// describe, is rejected instead of answering 200 having updated nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UpdateTagsRequestBodyTests : IDisposable
{
    private const string ComicInfo =
        "<ComicInfo><Series>Batman</Series><Title>The Dark Knight</Title><Number>1</Number>" +
        "<Year>2016</Year><Writer>Tom King</Writer><Tag>Superhero</Tag></ComicInfo>";

    private readonly string _root = Path.Join(AppContext.BaseDirectory, "update-tags-body-tests", Guid.NewGuid().ToString("N"));

    private string WatchedDirectory => Path.Join(_root, "watched");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Exception? cleanupError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                cleanupError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                cleanupError = ex;
            }

            if (attempt < 2)
                Thread.Sleep(100 * (attempt + 1));
        }

        Console.Error.WriteLine($"Failed to delete test directory '{_root}' after 3 attempts: {cleanupError}");
    }

    [Fact]
    public async Task GetReturnsTheDocumentedFields()
    {
        var path = WriteCbz("Batman #001.cbz");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var tags = await GetTagsAsync(client, path);

        Assert.Equal(
            new[] { "authors", "isUserEdited", "issue", "publisher", "series", "summary", "tags", "title", "userLockedFieldsMask", "volume", "year" },
            tags.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("The Dark Knight", tags.RootElement.GetProperty("title").GetString());
        Assert.Equal("1", tags.RootElement.GetProperty("issue").GetString());
        Assert.Equal(2016, tags.RootElement.GetProperty("year").GetInt32());
        Assert.Equal("Tom King", Assert.Single(tags.RootElement.GetProperty("authors").EnumerateArray()).GetString());
        Assert.Equal("Superhero", Assert.Single(tags.RootElement.GetProperty("tags").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task PutReplacesTheTagsOfTheFile()
    {
        var path = WriteCbz("Batman #001.cbz");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync($"/api/files/{Encode(path)}/tags", new
        {
            series = "Batman",
            issue = "1",
            title = "I Am Gotham",
            authors = new[] { "Tom King", "David Finch" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var tags = await GetTagsAsync(client, path);
        Assert.Equal("I Am Gotham", tags.RootElement.GetProperty("title").GetString());
        Assert.Equal(new[] { "Tom King", "David Finch" }, tags.RootElement.GetProperty("authors").EnumerateArray().Select(a => a.GetString()));
        // The fields the body leaves out are removed, not kept
        Assert.Equal(JsonValueKind.Null, tags.RootElement.GetProperty("year").ValueKind);
        Assert.Empty(tags.RootElement.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task PostWritesTheMetadataToEveryListedFile()
    {
        var first = WriteCbz("Batman #001.cbz");
        var second = WriteCbz("Batman #002.cbz");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/files/tags", new
        {
            files = new[] { first, second },
            metadata = new { series = "Batman", publisher = "DC Comics", year = 2023 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var path in new[] { first, second })
        {
            using var tags = await GetTagsAsync(client, path);
            Assert.Equal("DC Comics", tags.RootElement.GetProperty("publisher").GetString());
            Assert.Equal(2023, tags.RootElement.GetProperty("year").GetInt32());
            // The metadata replaces the tags of each file, so the fields it leaves out are removed
            Assert.Equal(JsonValueKind.Null, tags.RootElement.GetProperty("title").ValueKind);
            Assert.Empty(tags.RootElement.GetProperty("authors").EnumerateArray());
        }
    }

    // {path} stands for the archive's path as a JSON string. The first body is the
    // {"file", "tags"} format docs/API.md used to describe.
    [Theory]
    [InlineData("""{"file": {path}, "tags": {"series": "Superman", "issue": "2"}}""", "No files provided")]
    [InlineData("{}", "No files provided")]
    [InlineData("""{"files": [], "metadata": {"series": "Superman"}}""", "No files provided")]
    [InlineData("""{"files": null, "metadata": {"series": "Superman"}}""", null)]
    public async Task PostWithoutFilesIsRejectedAndChangesNothing(string body, string? expectedError)
    {
        var path = WriteCbz("Batman #001.cbz");
        var original = await File.ReadAllBytesAsync(path);
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/files/tags",
            new StringContent(body.Replace("{path}", JsonSerializer.Serialize(path)), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (expectedError is not null)
        {
            Assert.Contains(expectedError, responseBody);
        }
        else
        {
            using var validation = JsonDocument.Parse(responseBody);
            Assert.True(
                validation.RootElement.TryGetProperty("errors", out var errors)
                && errors.EnumerateObject().Any(error => string.Equals(error.Name, "Files", StringComparison.OrdinalIgnoreCase)),
                $"Expected a validation error for files, but got: {responseBody}");
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    private static string Encode(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JsonDocument> GetTagsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync($"/api/files/{Encode(path)}/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private string WriteCbz(string fileName)
    {
        var path = Path.Join(WatchedDirectory, "Batman", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
            {
                writer.Write(ComicInfo);
            }

            using var page = zip.CreateEntry("page001.jpg").Open();
            page.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        }

        return path;
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var directories = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = Path.Join(_root, "config"),
            ["AppSettings:TempFileDirectory"] = Path.Join(_root, "temp"),
        };
        foreach (var directory in directories.Values)
        {
            Directory.CreateDirectory(directory!);
        }

        // Keep the file watcher from renaming or normalizing the test archives
        var overrides = new Dictionary<string, string?>(directories)
        {
            ["AppSettings:WatcherEnableRename"] = "false",
            ["AppSettings:WatcherEnableNormalize"] = "false",
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>());
        });
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
