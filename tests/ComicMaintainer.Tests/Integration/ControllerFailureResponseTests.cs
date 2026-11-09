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
using Moq;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// What a client receives when a controller action fails unexpectedly, through the
/// real pipeline: the exception escapes the action and GlobalExceptionHandler turns
/// it into a problem document. It carries the action's own message, an "error" field
/// the UI reads, and a reference, but never the exception text.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ControllerFailureResponseTests : IDisposable
{
    private const string InternalDetail = "SQLITE_BUSY near /config/comics.db";

    private readonly string _root = Path.Join(AppContext.BaseDirectory, "controller-failure-tests", Guid.NewGuid().ToString("N"));

    public ControllerFailureResponseTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "watched"));
        Directory.CreateDirectory(Path.Join(_root, "duplicates"));
        Directory.CreateDirectory(Path.Join(_root, "config"));
    }

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

    [Fact]
    public async Task AFailingActionAnswersWithAProblemDocumentCarryingItsMessage()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/processing-history");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("title").GetString());
        Assert.Equal("Error retrieving processing history", root.GetProperty("detail").GetString());
        // The field every other failure response uses, for clients that read only that.
        Assert.Equal("Error retrieving processing history", root.GetProperty("error").GetString());

        var correlationId = root.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrEmpty(correlationId));
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues("X-Correlation-Id")));

        Assert.DoesNotContain("SQLITE_BUSY", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = Path.Join(_root, "watched"),
            ["AppSettings:DuplicateDirectory"] = Path.Join(_root, "duplicates"),
            ["AppSettings:ConfigDirectory"] = Path.Join(_root, "config"),
            ["CONFIG_DIR"] = Path.Join(_root, "config"),
        };

        var history = new Mock<IProcessingHistoryService>();
        history
            .Setup(h => h.GetHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(InternalDetail));

        return new TestWebApplicationFactory(builder =>
        {
            // Outside Development, where the exception text is withheld.
            builder.UseEnvironment("Production");
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(history.Object);
                services.AddSingleton<IAuthorizationMiddlewareResultHandler, AllowAllAuthorizationHandler>();
            });
        });
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
