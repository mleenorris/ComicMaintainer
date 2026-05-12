using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.Core.Data;

/// <summary>
/// EF Core <see cref="DbConnectionInterceptor"/> that applies SQLite PRAGMAs
/// to every opened connection. This enables WAL mode, larger page cache,
/// in-memory temp tables, memory-mapped I/O and a busy timeout, which together
/// substantially reduce contention and improve read/write throughput.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    // PRAGMAs applied to every opened connection.
    // - journal_mode=WAL: writers don't block readers
    // - synchronous=NORMAL: safe with WAL, much faster than FULL
    // - temp_store=MEMORY: keep temp tables/indexes in RAM
    // - cache_size=-65536: ~64 MiB page cache (negative value = kibibytes)
    // - mmap_size=268435456: 256 MiB memory-mapped I/O window
    // - busy_timeout=5000: 5s wait before SQLITE_BUSY for concurrent writers
    private const string PragmaScript =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA cache_size=-65536;" +
        "PRAGMA mmap_size=268435456;" +
        "PRAGMA busy_timeout=5000;";

    private readonly ILogger<SqlitePragmaInterceptor>? _logger;

    public SqlitePragmaInterceptor(ILogger<SqlitePragmaInterceptor>? logger = null)
    {
        _logger = logger;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyPragmas(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaScript;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to apply SQLite PRAGMAs");
        }
    }

    private async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaScript;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to apply SQLite PRAGMAs");
        }
    }
}
