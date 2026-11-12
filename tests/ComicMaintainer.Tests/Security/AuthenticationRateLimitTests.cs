using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Security;

/// <summary>
/// Sign-in, first-run setup and the other endpoints that check a password share a per-client rate limit
/// (<see cref="AuthenticationRateLimit"/>) on top of the global one, so one client cannot guess passwords for many
/// accounts (SEC-3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthenticationRateLimitTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AClientGetsAFewSignInAttemptsPerWindow()
    {
        using var client = _factory.CreateClient();

        for (var attempt = 1; attempt <= AuthenticationRateLimit.PermitLimit; attempt++)
        {
            using var response = await LoginAsync(client, attempt);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await LoginAsync(client, 0);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // The sign-in page shows the server's message: the response says what happened and how long to wait.
        Assert.Equal("application/json", limited.Content.Headers.ContentType?.MediaType);
        var seconds = limited.Headers.RetryAfter?.Delta?.TotalSeconds;
        Assert.NotNull(seconds);
        Assert.InRange(seconds.Value, 1, AuthenticationRateLimit.Window.TotalSeconds);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Too many requests. Please try again in ", body.GetProperty("error").GetString());

        // The limit is shared with setup, and leaves the rest of the API alone. Registration needs an administrator,
        // so an anonymous client is refused before the limit is consulted.
        using var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "someone", password = "An0therGuess", setupToken = "guess" });
        Assert.Equal(HttpStatusCode.TooManyRequests, setup.StatusCode);
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { username = "someone", password = "An0therGuess", email = "someone@example.com" });
        Assert.Equal(HttpStatusCode.Unauthorized, register.StatusCode);
        using var status = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, int attempt) =>
        client.PostAsJsonAsync("/api/auth/login", new { username = "nobody-" + Guid.NewGuid().ToString("N")[..8], password = "Wr0ngGuess" + attempt });
}
