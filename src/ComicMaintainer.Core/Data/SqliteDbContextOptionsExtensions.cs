using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Data;

/// <summary>
/// Centralized configuration of <see cref="DbContextOptionsBuilder"/> for the
/// SQLite provider. Ensures every place that constructs a context (DI registration,
/// design-time factory, ad-hoc singleton factory) uses the same connection-string
/// hardening, PRAGMA interceptor and optional query logging.
/// </summary>
public static class SqliteDbContextOptionsExtensions
{
    /// <summary>
    /// Environment variable that, when set to a truthy value ("1", "true",
    /// "yes", "on"), enables EF Core SQL command logging at Information level.
    /// Always-on logging is intentionally avoided because it is expensive.
    /// </summary>
    public const string QueryLoggingEnvVar = "EFCORE_QUERY_LOGGING";

    /// <summary>
    /// Normalize a SQLite connection string to enable connection pooling and a
    /// sensible default command timeout. Microsoft.Data.Sqlite already enables
    /// pooling by default, but we set it explicitly for clarity and forward
    /// compatibility.
    /// </summary>
    public static string NormalizeSqliteConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            // 30s default command timeout; PRAGMA busy_timeout handles writer waits at the SQLite level.
            DefaultTimeout = 30,
        };

        // NOTE: Cache=Shared is intentionally NOT enabled. Combined with WAL mode
        // it changes locking semantics (shared-cache uses table-level locks instead
        // of database-level), which has historically caused subtle correctness
        // regressions. Connection pooling already eliminates the cost of repeated
        // open/close, which is the main thing we wanted from a "shared" mode.

        return builder.ConnectionString;
    }

    /// <summary>
    /// Apply the standard ComicMaintainer SQLite configuration: pooled/shared
    /// connection, PRAGMA interceptor, and opt-in query logging via the
    /// <see cref="QueryLoggingEnvVar"/> environment variable.
    /// </summary>
    public static DbContextOptionsBuilder UseComicMaintainerSqlite(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        ILoggerFactory? loggerFactory = null)
    {
        var normalized = NormalizeSqliteConnectionString(connectionString);

        optionsBuilder.UseSqlite(normalized);

        optionsBuilder.AddInterceptors(
            new SqlitePragmaInterceptor(loggerFactory?.CreateLogger<SqlitePragmaInterceptor>()));

        if (IsQueryLoggingEnabled())
        {
            if (loggerFactory is not null)
            {
                optionsBuilder.UseLoggerFactory(loggerFactory);
            }

            optionsBuilder.EnableSensitiveDataLogging(false);
            optionsBuilder.EnableDetailedErrors();
            // LogTo writes the SQL commands to stderr/console when no logger factory is wired.
            optionsBuilder.LogTo(
                Console.WriteLine,
                new[] { Microsoft.EntityFrameworkCore.DbLoggerCategory.Database.Command.Name },
                Microsoft.Extensions.Logging.LogLevel.Information);
        }

        return optionsBuilder;
    }

    private static bool IsQueryLoggingEnabled()
    {
        var value = Environment.GetEnvironmentVariable(QueryLoggingEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
