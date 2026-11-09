using ComicMaintainer.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Data;

/// <summary>
/// The legacy global read-status table is dropped once nothing reads it; the per-user read
/// state that replaced it (backfilled by AddPerUserFileReadStatus) is left alone.
/// </summary>
public sealed class RemoveFileReadStatusMigrationTests : IAsyncLifetime
{
    private const string V300Migration = "20261108215642_V3_0_0";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
        await using var db = new ComicMaintainerDbContext(_options);
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(V300Migration);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Migrate_DropsTheLegacyTableAndKeepsPerUserReadState()
    {
        await ExecuteAsync(
            """
            INSERT INTO FileReadStatuses (FilePath, IsRead, CurrentPage, CreatedAt, UpdatedAt)
            VALUES ('/lib/Saga 001.cbz', 1, 5, '2024-01-01 00:00:00', '2024-01-01 00:00:00');
            INSERT INTO UserFileReadStatuses (UserId, FilePath, IsRead, CurrentPage, CreatedAt, UpdatedAt)
            VALUES ('reader', '/lib/Saga 001.cbz', 1, 5, '2024-01-01 00:00:00', '2024-01-01 00:00:00');
            """);

        await using (var db = new ComicMaintainerDbContext(_options))
        {
            await db.Database.MigrateAsync();
        }

        Assert.False(await TableExistsAsync("FileReadStatuses"));
        await using var check = new ComicMaintainerDbContext(_options);
        var row = await check.UserFileReadStatuses.AsNoTracking().SingleAsync();
        Assert.Equal("reader", row.UserId);
        Assert.Equal("/lib/Saga 001.cbz", row.FilePath);
        Assert.True(row.IsRead);
        Assert.Equal(5, row.CurrentPage);
    }

    [Fact]
    public async Task Model_NoLongerMapsTheLegacyTable()
    {
        await using var db = new ComicMaintainerDbContext(_options);

        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName());

        Assert.DoesNotContain("FileReadStatuses", tables);
        Assert.Contains("UserFileReadStatuses", tables);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
