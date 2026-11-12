using System.Net;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Notifications;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Tests.Notifications;

/// <summary>
/// A SQLite database in a temporary file, because the queue uses bulk updates and deletes. Each
/// context gets its own connection, as it does in production, so background work and a test's
/// reads can overlap.
/// </summary>
internal sealed class SqliteTestDbFactory : IDbContextFactory<ComicMaintainerDbContext>, IDisposable
{
    private readonly string _path = Path.Join(Path.GetTempPath(), $"notify-tests-{Guid.NewGuid():N}.db");

    public SqliteTestDbFactory()
    {
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    /// <summary>The next <see cref="CreateDbContextAsync"/> calls fail while this many are left.</summary>
    public int FailNextCreations { get; set; }

    public ComicMaintainerDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite($"Data Source={_path}").Options);

    public Task<ComicMaintainerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextCreations > 0)
        {
            FailNextCreations--;
            throw new InvalidOperationException("database unavailable");
        }

        return Task.FromResult(CreateDbContext());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file harms nothing.
        }
    }
}

/// <summary>A target that records what it was sent and can be made to fail.</summary>
internal sealed class RecordingChannel : INotificationChannel
{
    public RecordingChannel(string target) => Target = target;

    public string Target { get; }

    public List<NotificationMessage> Sent { get; } = new();

    public Func<NotificationMessage, CancellationToken, Task>? OnSend { get; set; }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (OnSend is not null)
        {
            await OnSend(message, cancellationToken);
        }

        Sent.Add(message);
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    public StubHttpClientFactory(HttpMessageHandler handler) => Client = new HttpClient(handler);

    public HttpClient Client { get; }

    public string? RequestedName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        RequestedName = name;
        return Client;
    }
}

/// <summary>Answers requests from a delegate and records them, bodies included.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public List<CapturedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));
        return await _respond(request, cancellationToken);
    }

    public sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
}

internal static class NotificationTestHttp
{
    public static HttpResponseMessage Response(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A channel-ready HTTP client over <paramref name="handler"/>.</summary>
    public static NotificationHttpClient ClientOver(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler));

    public static TestOptionsMonitor<AppSettings> Options(AppSettings settings) => new(settings);
}
