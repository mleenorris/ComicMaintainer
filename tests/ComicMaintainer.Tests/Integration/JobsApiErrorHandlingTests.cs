using System.Net;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
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
using Moq;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// An unexpected failure in the jobs API is reported by GlobalExceptionHandler: a problem
/// document with an <c>error</c> string, and nothing from the exception itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JobsApiErrorHandlingTests : IDisposable
{
    private const string InternalDetail = "internal path C:\\secret";

    private readonly string _root = Path.Join(AppContext.BaseDirectory, "jobs-api-error-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData("GET", "/api/jobs/6f27d510-2be7-49b9-bb14-a2cf014d9c9e")]
    [InlineData("GET", "/api/jobs")]
    [InlineData("GET", "/api/active-job")]
    [InlineData("DELETE", "/api/jobs/6f27d510-2be7-49b9-bb14-a2cf014d9c9e")]
    [InlineData("POST", "/api/jobs/6f27d510-2be7-49b9-bb14-a2cf014d9c9e/cancel")]
    [InlineData("POST", "/api/jobs/process-all")]
    public async Task AnUnexpectedFailure_IsAProblemDocumentThatLeaksNothing(string method, string url)
    {
        var failure = new InvalidOperationException(InternalDetail);
        var registry = new Mock<IBatchJobRegistry>();
        registry.Setup(r => r.GetJob(It.IsAny<Guid>())).Throws(failure);
        registry.Setup(r => r.GetJobSummaries()).Throws(failure);
        registry.Setup(r => r.GetActiveJob()).Throws(failure);
        registry.Setup(r => r.DeleteJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        registry.Setup(r => r.CancelJob(It.IsAny<Guid>())).Throws(failure);
        var fileStore = new Mock<IFileStoreService>();
        fileStore.Setup(f => f.GetAllFilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        await using var factory = CreateFactory(registry.Object, fileStore.Object);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("correlationId").GetString()));
    }

    private WebApplicationFactory<Program> CreateFactory(IBatchJobRegistry registry, IFileStoreService fileStore)
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

        return new TestWebApplicationFactory(builder =>
        {
            // Development would put the exception in the response on purpose.
            builder.UseEnvironment("Production");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>();
                services.AddSingleton(registry);
                services.AddSingleton(fileStore);
            });
        });
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
