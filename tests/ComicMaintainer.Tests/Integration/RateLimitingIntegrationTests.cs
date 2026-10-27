using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The global rate limiter in the real pipeline behind a reverse proxy listed in
/// <c>ReverseProxySettings:TrustedProxies</c>: every request comes from the proxy, which forwards the client's address
/// in <c>X-Forwarded-For</c>. A client that exhausts the anonymous limit gets 429, while the other clients behind the
/// same proxy can still sign in.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RateLimitingIntegrationTests : IAsyncLifetime
{
    // Anonymous requests allowed per client and minute; up to 10 more wait in a queue for the next window.
    private const int AnonymousPermitLimit = 100;
    private const int AnonymousQueueLimit = 10;

    /// <summary>
    /// The reverse proxy every request comes from (inside the trusted range, as an IPv4 peer of a dual-mode
    /// socket); TestServer leaves the remote address null.
    /// </summary>
    private static readonly IPAddress ProxyAddress = IPAddress.Parse("::ffff:172.18.0.2");

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "RateLimiting-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _baseFactory = new();
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        var watched = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
        var duplicates = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        var config = Directory.CreateDirectory(Path.Join(_root, "config")).FullName;

        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AppSettings:WatchedDirectory", watched);
            builder.UseSetting("AppSettings:DuplicateDirectory", duplicates);
            builder.UseSetting("AppSettings:ConfigDirectory", config);
            builder.UseSetting("ReverseProxySettings:TrustedProxies", "172.18.0.0/16");
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ProxyPeerStartupFilter>());
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _baseFactory.DisposeAsync();
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
    public async Task ClientExhaustingTheAnonymousLimit_DoesNotLockTheOtherClientsOutOfSigningIn()
    {
        const string clientA = "203.0.113.10";
        const string clientB = "198.51.100.20";
        using var client = _factory.CreateClient();

        for (var i = 1; i <= AnonymousPermitLimit; i++)
        {
            using var response = await client.SendAsync(AuthStatus(clientA));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Request {i} of client A returned {response.StatusCode}.");
        }

        // The next ten requests of client A wait for the next window, and the one after them is rejected at once.
        using var cancellation = new CancellationTokenSource();
        var overLimit = Enumerable.Range(0, AnonymousQueueLimit + 1)
            .Select(_ => client.SendAsync(AuthStatus(clientA), cancellation.Token))
            .ToList();
        try
        {
            var rejected = await await Task.WhenAny(overLimit).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

            // Client B, behind the same proxy, still has its own limit: a wrong password, not 429.
            using var login = await client.SendAsync(Login(clientB));
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

            using var status = await client.SendAsync(AuthStatus(clientB));
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }
        finally
        {
            // Release the queued requests rather than leave them waiting for the window to reset.
            await cancellation.CancelAsync();
            foreach (var request in overLimit)
            {
                try
                {
                    (await request).Dispose();
                }
                catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
                {
                }
            }
        }
    }

    private static HttpRequestMessage AuthStatus(string forwardedFor) =>
        Forwarded(new HttpRequestMessage(HttpMethod.Get, "/api/auth/status"), forwardedFor);

    private static HttpRequestMessage Login(string forwardedFor) =>
        Forwarded(
            new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = "nobody-" + Guid.NewGuid().ToString("N")[..12], password = "Not-the-passw0rd" }),
            },
            forwardedFor);

    /// <summary>Adds the <c>X-Forwarded-For</c> header the proxy sends: the address of the client it relays.</summary>
    private static HttpRequestMessage Forwarded(HttpRequestMessage request, string forwardedFor)
    {
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    /// <summary>Makes requests without a remote address come from <see cref="ProxyAddress"/>.</summary>
    private sealed class ProxyPeerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= ProxyAddress;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
