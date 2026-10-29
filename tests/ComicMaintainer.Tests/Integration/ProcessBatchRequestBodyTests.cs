using System.Net;
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
/// <c>POST /api/files/process-batch</c> binds its file list from a
/// non-nullable <c>[FromBody]</c> parameter, so the framework answers a
/// missing or <c>null</c> list with 400 before the action runs: the action
/// never sees a null list.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessBatchRequestBodyTests : IDisposable
{
    private readonly string _root = Path.Join(AppContext.BaseDirectory, "process-batch-body-tests", Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task AMissingFileListIsRejectedBeforeTheActionRuns(string body)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A non-empty request body is required.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnEmptyFileListReachesTheAction()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, "[]");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("jobId").TryGetGuid(out _));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync("/api/files/process-batch", new StringContent(body, Encoding.UTF8, "application/json"));

    private WebApplicationFactory<Program> CreateFactory()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = Path.Join(_root, "watched"),
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = Path.Join(_root, "config"),
            ["CONFIG_DIR"] = Path.Join(_root, "config"),
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
