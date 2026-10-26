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
        Assert.Equal(1L, ExecuteScalar(connection, "PRAGMA synchronous;"));
        Assert.Equal(2L, ExecuteScalar(connection, "PRAGMA temp_store;"));
        Assert.Equal(-65536L, ExecuteScalar(connection, "PRAGMA cache_size;"));
        Assert.Equal(5000L, ExecuteScalar(connection, "PRAGMA busy_timeout;"));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAConnection_LogsAndCarriesOnWhenThePragmasCannotBeApplied(bool openAsync)
    {
        var logger = new CapturingLogger();
        var path = CreateRollbackJournalDatabase();
        await using var db = CreateContext($"Data Source={path};Mode=ReadOnly;Pooling=False", new SqlitePragmaInterceptor(logger));

        await OpenAsync(db, openAsync);

        // Switching a read-only database to WAL fails; the connection must
        // still be usable rather than the open failing outright.
        var connection = db.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1L, ExecuteScalar(connection, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("delete", ExecuteScalar(connection, "PRAGMA journal_mode;"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Failed to apply SQLite PRAGMAs", entry.Message);
        var sqliteError = Assert.IsType<SqliteException>(entry.Exception);
        Assert.Equal(8, sqliteError.SqliteErrorCode); // SQLITE_READONLY
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
