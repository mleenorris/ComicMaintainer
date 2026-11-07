using System.Data.Common;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Covers the growth limits of <see cref="ProcessingHistoryService"/> against real SQLite:
/// no-op outcomes are not recorded, <see cref="ProcessingHistoryService.PruneAsync"/> enforces
/// the age and size limits in batches, and the history pages reuse a counted total instead of
/// counting the whole table for every page.
/// </summary>
public sealed class ProcessingHistoryRetentionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CountStatementInterceptor _counts = new();
    private readonly FakeTimeProvider _time = new(Now);
    private ServiceProvider _provider = null!;
    private IDbContextFactory<ComicMaintainerDbContext> _factory = null!;
    private ProcessingHistoryService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ComicMaintainerDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_counts));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ComicMaintainerDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _service = new ProcessingHistoryService(_factory, NullLogger<ProcessingHistoryService>.Instance, _time);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- no-op outcomes ------------------------------------------------------------------

    [Theory]
    [InlineData("Normalize", "File already normalized")]
    [InlineData("Rename", "File already correctly named")]
    public async Task AddHistoryEntryAsync_DoesNotRecordANoOp(string action, string note)
    {
        // What ComicProcessorService logs when a file needed nothing: the same values before and after.
        await _service.AddHistoryEntryAsync(Entry(action, success: true, note, before: "Saga 001.cbz", after: "Saga 001.cbz"));

        Assert.Equal(0, await CountRowsAsync());
    }

    [Fact]
    public async Task AddHistoryEntryAsync_RecordsChangesFailuresAndPlainOutcomes()
    {
        // A rename that changed the file name.
        await _service.AddHistoryEntryAsync(Entry("Rename", success: true, note: null, before: "saga_1.cbz", after: "Saga 001.cbz"));
        // A normalize that changed metadata (no note: something was done).
        var normalized = Entry("Normalize", success: true, note: null, before: "Saga 001.cbz", after: "Saga 001.cbz");
        normalized.AfterSeries = "Saga";
        await _service.AddHistoryEntryAsync(normalized);
        // A failure, even with identical values.
        await _service.AddHistoryEntryAsync(Entry("Rename", success: false, "Target file already exists", before: "a.cbz", after: "a.cbz"));
        // An outcome without before/after values, like the per-file "Process" summary.
        await _service.AddHistoryEntryAsync(Entry("Process", success: true, note: null, before: null, after: null));
        // A note on an entry whose values differ is a change, not a no-op.
        var changedWithNote = Entry("Normalize", success: true, "Series resolved", before: "a.cbz", after: "a.cbz");
        changedWithNote.BeforeYear = 2001;
        changedWithNote.AfterYear = 2002;
        await _service.AddHistoryEntryAsync(changedWithNote);

        Assert.Equal(5, await CountRowsAsync());
    }

    // ---- PruneAsync ----------------------------------------------------------------------

    [Fact]
    public async Task PruneAsync_RemovesEntriesOlderThanTheRetention()
    {
        await SeedAsync(Now.AddDays(-91), Now.AddDays(-90).AddMinutes(-1), Now.AddDays(-89), Now.AddHours(-1));

        var removed = await _service.PruneAsync(retentionDays: 90, maxEntries: 0);

        Assert.Equal(2, removed);
        var kept = await TimestampsAsync();
        Assert.Equal(new[] { Now.AddDays(-89).UtcDateTime, Now.AddHours(-1).UtcDateTime }, kept);
    }

    [Fact]
    public async Task PruneAsync_KeepsOnlyTheNewestEntriesUpToTheCap_IncludingTiedTimestamps()
    {
        var tied = Now.AddDays(-2);
        await SeedAsync(Now.AddDays(-5), tied, tied, tied, Now.AddDays(-1));

        var removed = await _service.PruneAsync(retentionDays: 0, maxEntries: 3);

        Assert.Equal(2, removed);
        await using var db = await _factory.CreateDbContextAsync();
        var kept = await db.ProcessingHistory.OrderBy(h => h.Id).Select(h => new { h.Id, h.Timestamp }).ToListAsync();
        // The newest entry and the two most recently inserted of the tied ones survive.
        Assert.Equal(new[] { 3, 4, 5 }, kept.Select(k => k.Id));
        Assert.Equal(new[] { tied.UtcDateTime, tied.UtcDateTime, Now.AddDays(-1).UtcDateTime }, kept.Select(k => k.Timestamp));
    }

    [Fact]
    public async Task PruneAsync_DeletesALargeBacklogInBatches()
    {
        var count = ProcessingHistoryService.PruneBatchSize * 2 + 10;
        await SeedManyAsync(count, Now.AddDays(-400));
        await SeedAsync(Now.AddMinutes(-5));
        _counts.Reset();

        var removed = await _service.PruneAsync(retentionDays: 30, maxEntries: 0);

        Assert.Equal(count, removed);
        Assert.Equal(1, await CountRowsAsync());
        Assert.Equal(3, _counts.Deletes);
    }

    [Fact]
    public async Task PruneAsync_WithBothLimitsDisabled_DoesNothing()
    {
        await SeedAsync(Now.AddYears(-5), Now.AddYears(-4));
        _counts.Reset();

        Assert.Equal(0, await _service.PruneAsync(retentionDays: 0, maxEntries: 0));

        Assert.Equal(2, await CountRowsAsync());
        Assert.Equal(0, _counts.Deletes);
    }

    // ---- counted total -------------------------------------------------------------------

    [Fact]
    public async Task GetHistoryAsync_CountsTheTableOnceAndKeepsTheTotalUpToDate()
    {
        await SeedAsync(Now.AddDays(-3), Now.AddDays(-2), Now.AddDays(-1));
        _counts.Reset();

        var (_, first) = await _service.GetHistoryAsync(limit: 1, offset: 0);
        var (_, second) = await _service.GetHistoryAsync(limit: 1, offset: 1);
        var (page, third) = await _service.GetHistoryAsync(limit: 1, offset: 2);

        Assert.Equal(3, first);
        Assert.Equal(3, second);
        Assert.Equal(3, third);
        Assert.Single(page);
        Assert.Equal(1, _counts.Counts);

        // Entries added through the service are added to the counted total.
        await _service.AddHistoryEntryAsync(Entry("Process", success: true, note: null, before: null, after: null));
        Assert.Equal(4, (await _service.GetHistoryAsync()).total);
        Assert.Equal(1, _counts.Counts);

        // Pruning counts again on the next page.
        await _service.PruneAsync(retentionDays: 0, maxEntries: 2);
        Assert.Equal(2, (await _service.GetHistoryAsync()).total);
        Assert.Equal(2, _counts.Counts);
    }

    [Fact]
    public async Task GetHistoryAsync_RecountsOnceTheCountedTotalIsOld()
    {
        await SeedAsync(Now.AddDays(-1));
        Assert.Equal(1, (await _service.GetHistoryAsync()).total);

        // Deleted outside the service, as the factory reset does.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.ProcessingHistory.ExecuteDeleteAsync();
        }
        Assert.Equal(1, (await _service.GetHistoryAsync()).total);

        _time.Advance(ProcessingHistoryService.TotalCountLifetime);
        Assert.Equal(0, (await _service.GetHistoryAsync()).total);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static ProcessingHistoryEntry Entry(string action, bool success, string? note, string? before, string? after) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = "/library/Saga/" + (after ?? "Saga 001.cbz"),
        Action = action,
        Timestamp = Now.UtcDateTime,
        Success = success,
        ErrorMessage = note,
        BeforeFilename = before,
        AfterFilename = after,
        BeforeSeries = before is null ? null : "Saga",
        AfterSeries = after is null ? null : "Saga",
        BeforeIssue = before is null ? null : "1",
        AfterIssue = after is null ? null : "1",
    };

    private async Task SeedAsync(params DateTimeOffset[] timestamps)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var timestamp in timestamps)
        {
            db.ProcessingHistory.Add(new ProcessingHistoryEntity
            {
                EntryId = Guid.NewGuid(),
                FilePath = "/library/Saga/Saga 001.cbz",
                Action = "Process",
                Timestamp = timestamp.UtcDateTime,
                Success = true,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedManyAsync(int count, DateTimeOffset timestamp)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {0})
            INSERT INTO ProcessingHistory (EntryId, FilePath, Action, Timestamp, Success)
            SELECT lower(hex(randomblob(16))), '/library/Saga/Saga 001.cbz', 'Process', {1}, 1 FROM n
            """,
            count, timestamp.UtcDateTime);
    }

    private async Task<int> CountRowsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ProcessingHistory.CountAsync();
    }

    private async Task<List<DateTime>> TimestampsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ProcessingHistory.OrderBy(h => h.Timestamp).Select(h => h.Timestamp).ToListAsync();
    }

    /// <summary>Counts the COUNT(*) and DELETE statements sent to the database.</summary>
    private sealed class CountStatementInterceptor : DbCommandInterceptor
    {
        private int _counts;
        private int _deletes;

        public int Counts => Volatile.Read(ref _counts);
        public int Deletes => Volatile.Read(ref _deletes);

        public void Reset()
        {
            Interlocked.Exchange(ref _counts, 0);
            Interlocked.Exchange(ref _deletes, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            var sql = command.CommandText;
            if (sql.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase) && sql.Contains("ProcessingHistory", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _counts);
            }
            if (sql.TrimStart().StartsWith("DELETE FROM \"ProcessingHistory\"", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _deletes);
            }
        }
    }
}
