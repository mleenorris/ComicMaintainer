using System.Net;
using System.Net.Http.Json;
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
/// <c>POST /api/files/tags</c> through the real app: files whose tags the
/// processor could not write are reported, instead of the request answering
/// 200 as if every file had been updated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UpdateTagsEndpointTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "update-tags-endpoint-tests", Guid.NewGuid().ToString("N"));

    private string WatchedDirectory => Path.Join(_root, "watched");

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
    public async Task FilesWhoseTagsCannotBeWrittenAreReportedAsFailed()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var missing = Path.Join(WatchedDirectory, "Missing 001.cbz");
        var notAnArchive = Path.Join(WatchedDirectory, "Notes.txt");
        await File.WriteAllTextAsync(notAnArchive, "not a comic");

        using var response = await client.PostAsJsonAsync("/api/files/tags",
            new { files = new[] { missing, notAnArchive }, metadata = new { series = "X" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Failed to update tags", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            new[] { missing, notAnArchive },
            body.RootElement.GetProperty("failed").EnumerateArray().Select(file => file.GetString()));
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = Path.Join(_root, "config"),
        };
        foreach (var directory in overrides.Values)
        {
            Directory.CreateDirectory(directory!);
        }

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
