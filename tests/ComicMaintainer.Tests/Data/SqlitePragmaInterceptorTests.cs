using System.Data;
using ComicMaintainer.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Data;

public sealed class SqlitePragmaInterceptorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-sqlite-pragmas-{Guid.NewGuid():N}");

    public SqlitePragmaInterceptorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAConnection_AppliesEveryPragma(bool openAsync)
    {
        var logger = new CapturingLogger();
        await using var db = CreateContext($"Data Source={CreateRollbackJournalDatabase()};Pooling=False", new SqlitePragmaInterceptor(logger));

        await OpenAsync(db, openAsync);

        var connection = db.Database.GetDbConnection();
        Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        AssertConnectionPragmasApplied(connection);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAReadOnlyConnection_LogsTheFailedWalSwitchAndAppliesTheOtherPragmas(bool openAsync)
    {
        var logger = new CapturingLogger();
        var path = CreateRollbackJournalDatabase();
        await using var db = CreateContext($"Data Source={path};Mode=ReadOnly;Pooling=False", new SqlitePragmaInterceptor(logger));

        await OpenAsync(db, openAsync);

        // Switching a read-only database to WAL fails; the connection must
        // still be usable, with every other PRAGMA, rather than the open
        // failing outright.
        var connection = db.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1L, ExecuteScalar(connection, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("delete", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        AssertConnectionPragmasApplied(connection);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Failed to switch SQLite journal mode to WAL", entry.Message);
        var sqliteError = Assert.IsType<SqliteException>(entry.Exception);
        Assert.Equal(8, sqliteError.SqliteErrorCode); // SQLITE_READONLY
    }

    // Regression test for #871: every PRAGMA after journal_mode=WAL was skipped
    // when another connection held a lock during the first switch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAConnectionToALockedDatabase_LogsTheFailedWalSwitchAndAppliesTheOtherPragmas(bool openAsync)
    {
        var logger = new CapturingLogger();
        var path = CreateRollbackJournalDatabase();
        await using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
        writer.Open();
        // A writer, unlike a reader, fails the switch without waiting out
        // busy_timeout, so the open gives up after Default Timeout.
        await using var write = writer.BeginTransaction(deferred: false);
        await using var db = CreateContext($"Data Source={path};Default Timeout=1;Pooling=False", new SqlitePragmaInterceptor(logger));

        await OpenAsync(db, openAsync);

        var connection = db.Database.GetDbConnection();
        Assert.Equal("delete", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        AssertConnectionPragmasApplied(connection);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Failed to switch SQLite journal mode to WAL", entry.Message);
        var sqliteError = Assert.IsType<SqliteException>(entry.Exception);
        Assert.Equal(5, sqliteError.SqliteErrorCode); // SQLITE_BUSY
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAConnection_WaitsForAReaderToFinishBeforeSwitchingToWal(bool openAsync)
    {
        var logger = new CapturingLogger();
        var path = CreateRollbackJournalDatabase();
        await using var reader = new SqliteConnection($"Data Source={path};Pooling=False");
        reader.Open();
        await using var read = reader.BeginTransaction(deferred: true);
        Assert.Equal(1L, ExecuteScalar(reader, "SELECT COUNT(*) FROM items;"));
        await using var db = CreateContext($"Data Source={path};Default Timeout=1;Pooling=False", new SqlitePragmaInterceptor(logger));

        // The reader finishes once Default Timeout has run out but well within
        // busy_timeout, so the switch only waits for it when busy_timeout is
        // already set.
        var opened = Task.Run(() => OpenAsync(db, openAsync));
        await Task.Delay(TimeSpan.FromSeconds(2));
        read.Commit();
        await opened;

        var connection = db.Database.GetDbConnection();
        Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        AssertConnectionPragmasApplied(connection);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAConnection_ToleratesAFailureWithoutALogger(bool openAsync)
    {
        await using var db = CreateContext(
            $"Data Source={CreateRollbackJournalDatabase()};Mode=ReadOnly;Pooling=False",
            new SqlitePragmaInterceptor());

        await OpenAsync(db, openAsync);

        Assert.Equal(1L, ExecuteScalar(db.Database.GetDbConnection(), "SELECT COUNT(*) FROM items;"));
    }

    [Fact]
    public async Task ConnectionOpened_DoesNothingForAConnectionThatIsNotOpen()
    {
        var logger = new CapturingLogger();
        var interceptor = new SqlitePragmaInterceptor(logger);
        await using var connection = new SqliteConnection("Data Source=:memory:");

        // The event data is only forwarded to the no-op base implementation.
        interceptor.ConnectionOpened(connection, eventData: null!);
        await interceptor.ConnectionOpenedAsync(connection, eventData: null!);

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionOpened_LogsAFailureToApplyTheOtherPragmas(bool openAsync)
    {
        var logger = new CapturingLogger();
        var interceptor = new SqlitePragmaInterceptor(logger);
        await using var connection = new SqliteConnection($"Data Source={CreateRollbackJournalDatabase()};Pooling=False");
        connection.Open();
        // Neither the journal mode nor synchronous can change inside a transaction.
        await using var transaction = connection.BeginTransaction(deferred: true);

        if (openAsync)
        {
            await interceptor.ConnectionOpenedAsync(connection, eventData: null!);
        }
        else
        {
            interceptor.ConnectionOpened(connection, eventData: null!);
        }

        Assert.Equal(5000L, ExecuteScalar(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(
            new[] { "Failed to switch SQLite journal mode to WAL", "Failed to apply SQLite PRAGMAs" },
            logger.Entries.Select(entry => entry.Message));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.IsType<SqliteException>(entry.Exception);
        });
    }

    [Fact]
    public async Task ConnectionOpenedAsync_PropagatesCancellation()
    {
        var logger = new CapturingLogger();
        var interceptor = new SqlitePragmaInterceptor(logger);
        await using var connection = new SqliteConnection($"Data Source={CreateRollbackJournalDatabase()};Pooling=False");
        connection.Open();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interceptor.ConnectionOpenedAsync(connection, eventData: null!, new CancellationToken(canceled: true)));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ConnectionOpened_ToleratesAFailureToApplyTheOtherPragmasWithoutALogger()
    {
        var interceptor = new SqlitePragmaInterceptor();
        await using var connection = new SqliteConnection($"Data Source={CreateRollbackJournalDatabase()};Pooling=False");
        connection.Open();
        await using var transaction = connection.BeginTransaction(deferred: true);

        interceptor.ConnectionOpened(connection, eventData: null!);
        await interceptor.ConnectionOpenedAsync(connection, eventData: null!);

        Assert.Equal(5000L, ExecuteScalar(connection, "PRAGMA busy_timeout;"));
    }

    // Every PRAGMA but journal_mode, which a connection gets whether or not the
    // switch to WAL works.
    private static void AssertConnectionPragmasApplied(System.Data.Common.DbConnection connection)
    {
        Assert.Equal(5000L, ExecuteScalar(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1L, ExecuteScalar(connection, "PRAGMA synchronous;"));
        Assert.Equal(2L, ExecuteScalar(connection, "PRAGMA temp_store;"));
        Assert.Equal(-65536L, ExecuteScalar(connection, "PRAGMA cache_size;"));
        Assert.Equal(268435456L, ExecuteScalar(connection, "PRAGMA mmap_size;"));
    }

    private string CreateRollbackJournalDatabase()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY); INSERT INTO items VALUES (1);";
        command.ExecuteNonQuery();
        return path;
    }

    private static ComicMaintainerDbContext CreateContext(string connectionString, SqlitePragmaInterceptor interceptor)
        => new(new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options);

    private static async Task OpenAsync(ComicMaintainerDbContext db, bool openAsync)
    {
        if (openAsync)
        {
            await db.Database.OpenConnectionAsync();
        }
        else
        {
            db.Database.OpenConnection();
        }
    }

    private static object? ExecuteScalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class CapturingLogger : ILogger<SqlitePragmaInterceptor>
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
