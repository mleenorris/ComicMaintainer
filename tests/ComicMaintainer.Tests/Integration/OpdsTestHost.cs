using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models.Auth;
using ComicMaintainer.Tests.Helpers;
using ComicMaintainer.Tests.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// The application with the OPDS catalog on, a real library folder and a real database. Each request comes from an
/// address of its own unless it names one with <see cref="ClientHeader"/>, so the shared limit on credential checks
/// only affects the tests about it.
/// </summary>
internal sealed class OpdsTestHost : IAsyncDisposable
{
    public const string Password = "Passw0rdOk";
    public const string ClientHeader = "X-Test-Client";

    private readonly string _root = Path.Join(Path.GetTempPath(), "ComicMaintainer.Tests", "Opds-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public OpdsTestHost(bool enabled = true, string? basePath = null, bool authelia = false)
    {
        WatchedDirectory = Directory.CreateDirectory(Path.Join(_root, "watched")).FullName;
        DuplicateDirectory = Directory.CreateDirectory(Path.Join(_root, "duplicates")).FullName;
        var configDirectory = Directory.CreateDirectory(Path.Join(_root, "config")).FullName;

        var settings = new Dictionary<string, string?>
        {
            ["AppSettings:WatchedDirectory"] = WatchedDirectory,
            ["AppSettings:DuplicateDirectory"] = DuplicateDirectory,
            ["AppSettings:ConfigDirectory"] = configDirectory,
            ["AppSettings:FileCoverCacheDirectory"] = Path.Join(configDirectory, "file-covers"),
            ["AppSettings:WatcherEnableRename"] = "false",
            ["AppSettings:WatcherEnableNormalize"] = "false",
            ["AppSettings:OpdsEnabled"] = enabled ? "true" : "false",
            ["CONFIG_DIR"] = configDirectory,
        };
        if (basePath is not null)
        {
            settings["AppSettings:BasePath"] = basePath;
        }

        if (authelia)
        {
            settings["AutheliaSettings:Enabled"] = "true";
            settings["AutheliaSettings:TrustedProxies"] = "127.0.0.1/32";
        }

        _factory = new TestWebApplicationFactory(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>());
        });
        Client = _factory.CreateClient();
    }

    public HttpClient Client { get; }

    public string WatchedDirectory { get; }

    public string DuplicateDirectory { get; }

    public IServiceProvider Services => _factory.Services;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TestIO.TryDeleteDirectory(_root);
    }

    public async Task<ApplicationUser> CreateUserAsync(string role = "User")
    {
        var name = "opds-" + Guid.NewGuid().ToString("N")[..12];
        var user = new ApplicationUser { UserName = name, Email = name + "@example.test", IsActive = true };
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    public async Task<string> CreateApiKeyAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var (success, key, error) = await scope.ServiceProvider.GetRequiredService<IAuthService>().GenerateApiKeyAsync(userId);
        Assert.True(success, error);
        return key!;
    }

    public async Task ChangeUserAsync(string userId, Func<UserManager<ApplicationUser>, ApplicationUser, Task> change)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await change(users, (await users.FindByIdAsync(userId))!);
    }

    /// <summary>Writes a comic under the library and tracks it. Page contents are short text, not images.</summary>
    public async Task<string> AddComicAsync(string relativePath, params (string Name, string Content)[] pages)
    {
        var path = Path.Join(WatchedDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, ArchiveEntryReaderTests.BuildZip(pages
            .Select(p => (p.Name, Encoding.ASCII.GetBytes(p.Content), CompressionLevel.NoCompression))
            .ToArray()));
        await Services.GetRequiredService<IFileStoreService>().AddFileAsync(path);
        return path;
    }

    /// <summary>A tracked row for an arbitrary path, as a stale or restored database could hold.</summary>
    public async Task<int> AddTrackedRowAsync(string filePath)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>().CreateDbContextAsync();
        var row = new ComicFileEntity
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Directory = Path.GetDirectoryName(filePath) ?? string.Empty,
        };
        db.ComicFiles.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<int> FileIdAsync(string filePath) =>
        (await Services.GetRequiredService<IFileStoreService>().GetFileIdsAsync([filePath]))[filePath];

    public async Task<ReadingProgressEntity?> ProgressAsync(string userId, string filePath)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>().CreateDbContextAsync();
        return await db.ReadingProgresses.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId && p.ContentId == filePath);
    }

    public async Task<HttpResponseMessage> GetAsync(string url, string? user = null, string? secret = null, string? client = null, Action<HttpRequestMessage>? configure = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (user is not null)
        {
            request.Headers.Authorization = BasicHeader(user, secret!);
        }

        if (client is not null)
        {
            request.Headers.Add(ClientHeader, client);
        }

        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    public static AuthenticationHeaderValue BasicHeader(string user, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));

    public static async Task<XDocument> ReadFeedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        private int _requests;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var request = Interlocked.Increment(ref _requests);
                context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(ClientHeader, out var client)
                    ? IPAddress.Parse(client.ToString())
                    : new IPAddress([203, 0, (byte)(request >> 8), (byte)request]);
                return nextMiddleware();
            });
            next(app);
        };
    }
}

/// <summary>Sets up the host once for a test class, and has it dispose at the end.</summary>
public sealed class OpdsHostFixture : IAsyncLifetime
{
    internal OpdsTestHost Host { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Host = new OpdsTestHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();
}
