using System.Collections.Concurrent;
using System.Data.Common;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Services;
using ComicMaintainer.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComicMaintainer.Tests.Data;

/// <summary>
/// Checks that the <c>ComicFiles</c> / <c>ProcessingHistory</c> indexes match the queries the
/// application actually runs: the paged file listings must read in index order for every
/// sort, indexes no query uses must be gone, and the planner statistics refresh must work.
/// </summary>
public sealed class ComicFileIndexTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly QueryPlanRecorder _plans = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_plans));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 50; i++)
        {
            db.ComicFiles.Add(new ComicFileEntity
            {
                FilePath = $"/library/Saga/Saga {i:000}.cbz",
                FileName = $"Saga {i:000}.cbz",
                Directory = "/library/Saga",
                FileSize = 1000 + i,
                LastModified = now.AddMinutes(-i),
                IsDuplicate = i == 7,
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData("date", "desc", "IX_ComicFiles_LastModified")]
    [InlineData("date", "asc", "IX_ComicFiles_LastModified")]
    [InlineData("size", "desc", "IX_ComicFiles_FileSize")]
    [InlineData("size", "asc", "IX_ComicFiles_FileSize")]
    [InlineData("name", "asc", "IX_ComicFiles_FileName")]
    public async Task GetFilesPageAsync_EverySort_ReadsThePageInIndexOrder(string sort, string direction, string index)
    {
        var service = CreateService();
        _plans.Clear();

        var page = await service.GetFilesPageAsync(sort: sort, direction: direction, page: 1, perPage: 10);

        Assert.Equal(10, page.Files.Count);
        var plan = _plans.PlanFor("ORDER BY");
        // Regression: date and size had no index, so every page scanned and sorted the
        // whole table ("USE TEMP B-TREE FOR ORDER BY").
        Assert.Contains(index, plan);
        Assert.DoesNotContain("TEMP B-TREE FOR ORDER BY", plan);
    }

    [Fact]
    public async Task DuplicatesFilter_UsesThePartialDuplicateIndex()
    {
        var service = CreateService();
        _plans.Clear();

        var page = await service.GetFilesPageAsync(filter: "duplicates", page: 1, perPage: 10);

        Assert.Single(page.Files);
        Assert.Contains(_plans.All(), plan => plan.Contains("IX_ComicFiles_IsDuplicate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Model_HasNoIndexesThatNoQueryUses()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var comicFileIndexes = IndexedColumns(db, typeof(ComicFileEntity));
        var historyIndexes = IndexedColumns(db, typeof(ProcessingHistoryEntity));

        foreach (var unused in new[] { "IsRead", "IsProcessed", "IsRenamed", "IsNormalized", "UpdatedAt", "CreatedAt", "Directory" })
        {
            Assert.DoesNotContain(unused, comicFileIndexes);
        }

        Assert.Contains("Directory,FileName", comicFileIndexes);
        Assert.Contains("LastModified", comicFileIndexes);
        Assert.Contains("FileSize", comicFileIndexes);
        Assert.DoesNotContain("FilePath", historyIndexes);
        Assert.DoesNotContain("Success", historyIndexes);
        Assert.Contains("Timestamp", historyIndexes);
    }

    [Fact]
    public async Task OptimizeAsync_WritesPlannerStatisticsForTheLibraryTables()
    {
        Assert.True(await SqliteStatisticsMaintenance.OptimizeAsync(_factory));

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_stat1 WHERE tbl = 'ComicFiles'";
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0);
    }

    [Fact]
    public async Task OptimizeAsync_NonSqliteProvider_DoesNothing()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseInMemoryDatabase($"optimize_{Guid.NewGuid()}"));
        await using var provider = services.BuildServiceProvider();

        Assert.False(await SqliteStatisticsMaintenance.OptimizeAsync(
            provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>()));
    }

    private FileStoreService CreateService() => new(
        new TestOptionsMonitor<AppSettings>(new AppSettings { WatchedDirectory = "/library" }),
        NullLogger<FileStoreService>.Instance,
        _factory);

    private static HashSet<string> IndexedColumns(ComicMaintainerDbContext db, Type entityType) =>
        db.Model.FindEntityType(entityType)!
            .GetIndexes()
            .Select(index => string.Join(',', index.Properties.Select(p => p.Name)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Records SQLite's query plan for every reader command, by running the same statement
    /// (with the same parameters) under <c>EXPLAIN QUERY PLAN</c> just before it executes.
    /// </summary>
    private sealed class QueryPlanRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Sql, string Plan)> _plans = new();

        public void Clear() => _plans.Clear();

        public IEnumerable<string> All() => _plans.Select(p => p.Plan);

        public string PlanFor(string sqlFragment) =>
            _plans.Last(p => p.Sql.Contains(sqlFragment, StringComparison.Ordinal)).Plan;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ComicFiles", StringComparison.Ordinal))
            {
                await using var explain = command.Connection!.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
                foreach (DbParameter parameter in command.Parameters)
                {
                    var copy = explain.CreateParameter();
                    copy.ParameterName = parameter.ParameterName;
                    copy.Value = parameter.Value;
                    explain.Parameters.Add(copy);
                }

                var details = new List<string>();
                await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        details.Add(reader.GetString(reader.FieldCount - 1));
                    }
                }

                _plans.Enqueue((command.CommandText, string.Join('\n', details)));
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
