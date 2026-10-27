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
    // PRAGMAs applied to every opened connection, as three commands so that a
    // failure of one does not skip the others.
    // - busy_timeout=5000: 5s wait before SQLITE_BUSY for concurrent writers;
    //   set first so the switch to WAL waits for other connections too
    private const string BusyTimeoutPragma = "PRAGMA busy_timeout=5000;";

    // - journal_mode=WAL: writers don't block readers. The mode is stored in the
    //   database file, so the switch fails on a read-only database or when
    //   another connection holds a lock during the first switch; the connection
    //   then keeps its current journal mode.
    private const string JournalModePragma = "PRAGMA journal_mode=WAL;";

    // - synchronous=NORMAL: safe with WAL, much faster than FULL
    // - temp_store=MEMORY: keep temp tables/indexes in RAM
    // - cache_size=-65536: ~64 MiB page cache (negative value = kibibytes)
    // - mmap_size=268435456: 256 MiB memory-mapped I/O window
    // mmap_size stays last because it returns a row: ExecuteNonQuery ignores
    // errors from the statements after the first one that returns a row.
    private const string ConnectionPragmas =
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA cache_size=-65536;" +
        "PRAGMA mmap_size=268435456;";

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
            Execute(connection, BusyTimeoutPragma);

            try
            {
                Execute(connection, JournalModePragma);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to switch SQLite journal mode to WAL");
            }

            Execute(connection, ConnectionPragmas);
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
            await ExecuteAsync(connection, BusyTimeoutPragma, cancellationToken).ConfigureAwait(false);

            try
            {
                await ExecuteAsync(connection, JournalModePragma, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to switch SQLite journal mode to WAL");
            }

            await ExecuteAsync(connection, ConnectionPragmas, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to apply SQLite PRAGMAs");
        }
    }

    private static void Execute(DbConnection connection, string pragmas)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = pragmas;
        cmd.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(DbConnection connection, string pragmas, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
