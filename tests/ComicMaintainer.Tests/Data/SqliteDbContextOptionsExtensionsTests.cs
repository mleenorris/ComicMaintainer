using ComicMaintainer.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Tests.Data;

public sealed class SqliteDbContextOptionsExtensionsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"cm-sqlite-options-{Guid.NewGuid():N}");

    public SqliteDbContextOptionsExtensionsTests()
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
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeSqliteConnectionString_LeavesABlankConnectionStringUntouched(string connectionString)
    {
        Assert.Same(connectionString, SqliteDbContextOptionsExtensions.NormalizeSqliteConnectionString(connectionString));
    }

    [Fact]
    public void NormalizeSqliteConnectionString_ForcesPoolingAndTheDefaultTimeoutButKeepsEverythingElse()
    {
        var normalized = SqliteDbContextOptionsExtensions.NormalizeSqliteConnectionString(
            "Data Source=/data/comics.db;Mode=ReadOnly;Pooling=False;Default Timeout=5");

        var builder = new SqliteConnectionStringBuilder(normalized);
        Assert.Equal("/data/comics.db", builder.DataSource);
        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
        Assert.True(builder.Pooling);
        Assert.Equal(30, builder.DefaultTimeout);
        Assert.Equal(SqliteCacheMode.Default, builder.Cache);
    }

    [Fact]
    public void UseComicMaintainerSqlite_ConfiguresTheNormalizedConnectionAndOnePragmaInterceptor()
    {
        var builder = new DbContextOptionsBuilder<ComicMaintainerDbContext>();

        var returned = builder.UseComicMaintainerSqlite("Data Source=/data/comics.db");

        Assert.Same(builder, returned);
        var connection = new SqliteConnectionStringBuilder(RelationalOptionsExtension.Extract(builder.Options).ConnectionString);
        Assert.Equal("/data/comics.db", connection.DataSource);
        Assert.True(connection.Pooling);
        Assert.Equal(30, connection.DefaultTimeout);

        var interceptors = builder.Options.FindExtension<CoreOptionsExtension>()!.Interceptors;
        Assert.NotNull(interceptors);
        Assert.IsType<SqlitePragmaInterceptor>(Assert.Single(interceptors!));
    }

    [Fact]
    public void UseComicMaintainerSqlite_GivesTheInterceptorALoggerFromTheSuppliedFactory()
    {
        var databasePath = CreateRollbackJournalDatabase();
        var loggerFactory = new RecordingLoggerFactory();
        var builder = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
            .UseComicMaintainerSqlite($"Data Source={databasePath}", loggerFactory);
        var interceptor = Assert.IsType<SqlitePragmaInterceptor>(
            Assert.Single(builder.Options.FindExtension<CoreOptionsExtension>()!.Interceptors!));

        // A read-only connection cannot switch to WAL, so the interceptor has
        // something to report through the logger it was handed.
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        interceptor.ConnectionOpened(connection, eventData: null!);

        Assert.Contains(typeof(SqlitePragmaInterceptor).FullName!, loggerFactory.Categories);
        var warning = Assert.Single(loggerFactory.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(typeof(SqlitePragmaInterceptor).FullName, warning.Category);
        Assert.Contains("Failed to switch SQLite journal mode to WAL", warning.Message);
        Assert.IsType<SqliteException>(warning.Exception);
    }

    [Fact]
    public async Task UseComicMaintainerSqlite_AppliesThePragmasToEveryOpenedConnection()
    {
        var databasePath = CreateRollbackJournalDatabase();
        var connectionString = $"Data Source={databasePath}";
        var builder = new DbContextOptionsBuilder<ComicMaintainerDbContext>();
        builder.UseComicMaintainerSqlite(connectionString);
        var options = builder.Options;

        try
        {
            await using var db = new ComicMaintainerDbContext(options);
            await db.Database.OpenConnectionAsync();
            var connection = db.Database.GetDbConnection();

            Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode;"));
            Assert.Equal(5000L, ExecuteScalar(connection, "PRAGMA busy_timeout;"));
            await db.Database.CloseConnectionAsync();
        }
        finally
        {
            using var pooled = new SqliteConnection(SqliteDbContextOptionsExtensions.NormalizeSqliteConnectionString(connectionString));
            SqliteConnection.ClearPool(pooled);
        }
    }

    private string CreateRollbackJournalDatabase()
    {
        var path = Path.Join(_root, $"{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY); INSERT INTO items VALUES (1);";
        command.ExecuteNonQuery();
        return path;
    }

    private static object? ExecuteScalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly object _gate = new();
        private readonly List<string> _categories = new();
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<string> Categories
        {
            get
            {
                lock (_gate)
                {
                    return _categories.ToList();
                }
            }
        }

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            lock (_gate)
            {
                _categories.Add(categoryName);
            }

            return new RecordingLogger(this, categoryName);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private void Record(LogEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Record(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
}

/// <summary>
/// Query logging is switched by a process-wide environment variable, so these
/// tests run in a non-parallel collection and always restore the variable.
/// </summary>
[Collection(CollectionName)]
public sealed class SqliteQueryLoggingEnvironmentTests
{
    public const string CollectionName = "SqliteDbContextOptionsExtensionsTests.QueryLoggingEnvironment";

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("On")]
    public void UseComicMaintainerSqlite_EnablesQueryLoggingForTruthyValues(string value)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var core = Configure(value, loggerFactory);

        Assert.True(core.DetailedErrorsEnabled);
        Assert.False(core.IsSensitiveDataLoggingEnabled);
        Assert.Same(loggerFactory, core.LoggerFactory);
        Assert.NotNull(core.DbContextLogger);
    }

    [Fact]
    public void UseComicMaintainerSqlite_LogsToTheConsoleOnlyWhenNoLoggerFactoryIsSupplied()
    {
        var core = Configure("1", loggerFactory: null);

        Assert.True(core.DetailedErrorsEnabled);
        Assert.Null(core.LoggerFactory);
        Assert.NotNull(core.DbContextLogger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("enabled")]
    [InlineData(" 1")]
    public void UseComicMaintainerSqlite_LeavesQueryLoggingOffOtherwise(string? value)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var core = Configure(value, loggerFactory);

        Assert.False(core.DetailedErrorsEnabled);
        Assert.Null(core.LoggerFactory);
        Assert.Null(core.DbContextLogger);
        Assert.IsType<SqlitePragmaInterceptor>(Assert.Single(core.Interceptors!));
    }

    private static CoreOptionsExtension Configure(string? environmentValue, ILoggerFactory? loggerFactory)
    {
        var previous = Environment.GetEnvironmentVariable(SqliteDbContextOptionsExtensions.QueryLoggingEnvVar);
        Environment.SetEnvironmentVariable(SqliteDbContextOptionsExtensions.QueryLoggingEnvVar, environmentValue);
        try
        {
            var builder = new DbContextOptionsBuilder<ComicMaintainerDbContext>()
                .UseComicMaintainerSqlite("Data Source=/data/comics.db", loggerFactory);
            return builder.Options.FindExtension<CoreOptionsExtension>()!;
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqliteDbContextOptionsExtensions.QueryLoggingEnvVar, previous);
        }
    }

    [CollectionDefinition(CollectionName, DisableParallelization = true)]
    public sealed class QueryLoggingEnvironmentCollection
    {
    }
}
