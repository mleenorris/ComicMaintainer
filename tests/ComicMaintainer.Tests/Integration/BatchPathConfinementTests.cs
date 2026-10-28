using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Models;
using ComicMaintainer.WebApi.Controllers;
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
/// The batch endpoints act on file paths taken from the request body. A file outside
/// the watched and duplicate directories, such as one next to the database in the
/// config directory, must come through every one of them unchanged: the endpoint
/// either rejects the request or runs a job that reports the file as failed.
/// <c>POST /api/jobs/delete-selected</c> used to delete such files (#840).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BatchPathConfinementTests : IClassFixture<BatchPathConfinementTests.LibraryHost>
{
    private readonly LibraryHost _host;

    public BatchPathConfinementTests(LibraryHost host) => _host = host;

    // Each request body names the file through the PATH placeholder.
    public static TheoryData<string, string> BatchEndpoints => new()
    {
        { "/api/jobs/process-selected", """{ "Files": [PATH], "ForceReprocess": true }""" },
        { "/api/jobs/rename-selected", """{ "Files": [PATH], "ForceReprocess": true }""" },
        { "/api/jobs/normalize-selected", """{ "Files": [PATH], "ForceReprocess": true }""" },
        { "/api/jobs/update-metadata-selected", """{ "Files": [PATH], "Metadata": { "Series": "Overwritten" } }""" },
        { "/api/jobs/remove-metadata-selected", """{ "Files": [PATH] }""" },
        { "/api/jobs/delete-selected", """{ "Files": [PATH] }""" },
        { "/api/process-selected", """{ "Files": [PATH] }""" },
        { "/api/rename-selected?forceReprocess=true", """{ "Files": [PATH] }""" },
        { "/api/normalize-selected?forceReprocess=true", """{ "Files": [PATH] }""" },
        { "/api/files/process-batch", """[PATH]""" },
        { "/api/files/tags", """{ "Files": [PATH], "Metadata": { "Series": "Overwritten" } }""" },
    };

    [Theory]
    [MemberData(nameof(BatchEndpoints))]
    public async Task AComicOutsideTheLibraryComesThroughUnchanged(string url, string body)
    {
        var outside = _host.WriteComic(_host.ConfigDirectory);
        var before = await File.ReadAllBytesAsync(outside);

        using var response = await _host.PostAsync(url, body.Replace("PATH", JsonSerializer.Serialize(outside)));

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            Assert.Contains(JobsController.OutsideAllowedDirectoryError, await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var job = await _host.WaitForJobAsync(response);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal(1, job.FailedFiles);
        }

        Assert.True(File.Exists(outside), $"{url} removed the file");
        Assert.Equal(before, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task DeleteSelectedRejectsAFileOutsideTheLibrary()
    {
        var outside = Path.Join(_host.ConfigDirectory, $"victim-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "keep me");

        using var response = await _host.PostAsync(
            "/api/jobs/delete-selected", JsonSerializer.Serialize(new { Files = new[] { outside } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JobsController.OutsideAllowedDirectoryError, json.RootElement.GetProperty("error").GetString());
        Assert.Equal("keep me", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task DeleteSelectedDeletesFilesInTheWatchedAndDuplicateDirectories()
    {
        var inLibrary = _host.WriteComic(_host.WatchedDirectory);
        var duplicate = _host.WriteComic(_host.DuplicateDirectory);

        using var response = await _host.PostAsync(
            "/api/jobs/delete-selected", JsonSerializer.Serialize(new { Files = new[] { inLibrary, duplicate } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = await _host.WaitForJobAsync(response);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.ProcessedFiles);
        Assert.False(File.Exists(inLibrary));
        Assert.False(File.Exists(duplicate));
    }

    /// <summary>
    /// One app for the whole class, with its own watched, duplicate and config
    /// directories, where every request is authorized.
    /// </summary>
    public sealed class LibraryHost : IDisposable
    {
        private readonly string _root = Path.Join(AppContext.BaseDirectory, "batch-path-confinement-tests", Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public LibraryHost()
        {
            var overrides = new Dictionary<string, string?>
            {
                ["AppSettings:WatchedDirectory"] = WatchedDirectory,
                ["AppSettings:DuplicateDirectory"] = DuplicateDirectory,
                ["AppSettings:ConfigDirectory"] = ConfigDirectory,
                ["AppSettings:TempFileDirectory"] = Path.Join(_root, "temp"),
            };
            foreach (var directory in overrides.Values)
            {
                Directory.CreateDirectory(directory!);
            }

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
            _client = _factory.CreateClient();
        }

        public string WatchedDirectory => Path.Join(_root, "watched");

        public string DuplicateDirectory => Path.Join(_root, "duplicates");

        public string ConfigDirectory => Path.Join(_root, "config");

        public Task<HttpResponseMessage> PostAsync(string url, string json) =>
            _client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        /// <summary>
        /// Waits for the job named in an accepted request's response to finish.
        /// </summary>
        public async Task<ProcessingJob> WaitForJobAsync(HttpResponseMessage response)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var jobId = (json.RootElement.TryGetProperty("job_id", out var id) ? id : json.RootElement.GetProperty("jobId")).GetGuid();
            var processor = _factory.Services.GetRequiredService<IComicProcessorService>();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var job = processor.GetJob(jobId);
                if (job is { Status: not (JobStatus.Queued or JobStatus.Running) })
                {
                    return job;
                }

                Assert.True(DateTime.UtcNow < deadline, $"Job {jobId} did not finish in time");
                await Task.Delay(25);
            }
        }

        /// <summary>
        /// Writes a tagged comic under a new name to <paramref name="directory"/>.
        /// </summary>
        public string WriteComic(string directory)
        {
            var path = Path.Join(directory, $"Comic {Guid.NewGuid():N}.cbz");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
                {
                    writer.Write("<ComicInfo><Series>Victim</Series><Number>1</Number><Title>Chapter 1</Title></ComicInfo>");
                }

                using var page = archive.CreateEntry("page001.jpg").Open();
                page.Write([0xFF, 0xD8, 0xFF, 0xE0]);
            }

            return path;
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            SqliteConnection.ClearAllPools();
            // A leftover directory must not fail the tests it served; it lives under the
            // test output directory, which is discarded with the build, and is reported here.
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{nameof(BatchPathConfinementTests)}: could not delete {_root}: {ex.Message}");
            }
        }
    }

    private sealed class AllowAllAuthorizationHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) => next(context);
    }
}
