using System.Net;
using System.Net.Http.Json;
using ComicMaintainer.WebApi.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComicMaintainer.Tests.Security;

/// <summary>
/// Sign-in, registration and first-run setup share a per-client rate limit (<see cref="AuthenticationRateLimit"/>) on
/// top of the global one, so one client cannot guess passwords for many accounts (SEC-3).
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

        // The limit is shared with setup and registration, and leaves the rest of the API alone.
        using var setup = await client.PostAsJsonAsync("/api/auth/setup", new { username = "someone", password = "An0therGuess", setupToken = "guess" });
        Assert.Equal(HttpStatusCode.TooManyRequests, setup.StatusCode);
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { username = "someone", password = "An0therGuess", email = "someone@example.com" });
        Assert.Equal(HttpStatusCode.TooManyRequests, register.StatusCode);
        using var status = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, int attempt) =>
        client.PostAsJsonAsync("/api/auth/login", new { username = "nobody-" + Guid.NewGuid().ToString("N")[..8], password = "Wr0ngGuess" + attempt });
}
