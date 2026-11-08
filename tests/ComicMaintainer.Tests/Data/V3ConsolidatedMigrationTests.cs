using ComicMaintainer.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ComicMaintainer.Tests.Data;

/// <summary>
/// Applies the consolidated 3.0.0 migration to a database as the last 2.x release left it, and
/// checks the steps that are not simply the model difference: API keys are discarded rather than
/// carried over, and the existing rows get their owners and processing flags.
/// </summary>
public sealed class V3ConsolidatedMigrationTests : IAsyncLifetime
{
    private const string LastV2Migration = "20261027203049_AddEmailDeliverySentAtIndex";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<ComicMaintainerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ComicMaintainerDbContext>().UseSqlite(_connection).Options;
        await using var db = new ComicMaintainerDbContext(_options);
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(LastV2Migration);

        await InsertAsync("AspNetRoles", new() { ["Id"] = "role-admin", ["Name"] = "Admin", ["NormalizedName"] = "ADMIN" });
        await InsertAsync("AspNetUsers", new()
        {
            ["Id"] = "admin",
            ["UserName"] = "admin",
            ["NormalizedUserName"] = "ADMIN",
            ["IsActive"] = true,
            ["CreatedAt"] = "2021-01-01 00:00:00",
            ["ApiKey"] = "plain-text-key",
        });
        await InsertAsync("AspNetUserRoles", new() { ["UserId"] = "admin", ["RoleId"] = "role-admin" });
        await InsertAsync("EreaderDevices", new() { ["Id"] = 1, ["Name"] = "Kindle", ["EmailAddress"] = "kindle@example.com" });
        await InsertComicAsync("/lib/Tower/Tower 001.cbz", isDuplicate: true, isProcessed: false);
        await InsertComicAsync("/lib/Saga/Saga 001.cbz", isDuplicate: false, isProcessed: false);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Migrate_DropsPlainTextApiKeysInsteadOfMovingThemIntoTheNewColumns()
    {
        await MigrateToLatestAsync();

        var columns = await GetColumnsAsync("AspNetUsers");
        Assert.DoesNotContain("ApiKey", columns);
        Assert.Contains("ApiKeyHash", columns);
        Assert.Contains("ApiKeyCreatedAt", columns);
        Assert.Contains("ApiKeyLastUsedAt", columns);

        // An account from 2.x loads: the plain-text key did not end up in a date column.
        await using var db = new ComicMaintainerDbContext(_options);
        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.Null(user.ApiKeyHash);
        Assert.Null(user.ApiKeyCreatedAt);
        Assert.Null(user.ApiKeyLastUsedAt);
    }

    [Fact]
    public async Task Migrate_GivesExistingDevicesToTheFirstAdministrator()
    {
        await MigrateToLatestAsync();

        await using var db = new ComicMaintainerDbContext(_options);
        var device = await db.EreaderDevices.AsNoTracking().SingleAsync();
        Assert.Equal("admin", device.OwnerUserId);

        // The address is unique per owner now, so another user can save the same one.
        db.EreaderDevices.Add(new EreaderDeviceEntity { Name = "Other", EmailAddress = "kindle@example.com", OwnerUserId = "someone-else" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Migrate_QueuesUnprocessedDuplicatesForOneMorePass()
    {
        await MigrateToLatestAsync();

        await using var db = new ComicMaintainerDbContext(_options);
        var pending = await db.ComicFiles.AsNoTracking()
            .Where(e => e.IsProcessingPending)
            .Select(e => e.FilePath)
            .ToListAsync();
        Assert.Equal(new[] { "/lib/Tower/Tower 001.cbz" }, pending);
    }

    private async Task MigrateToLatestAsync()
    {
        await using var db = new ComicMaintainerDbContext(_options);
        await db.Database.MigrateAsync();
    }

    private Task InsertComicAsync(string path, bool isDuplicate, bool isProcessed) =>
        InsertAsync("ComicFiles", new()
        {
            ["FilePath"] = path,
            ["FileName"] = Path.GetFileName(path),
            ["Directory"] = Path.GetDirectoryName(path),
            ["IsDuplicate"] = isDuplicate,
            ["IsProcessed"] = isProcessed,
            ["MetadataSource"] = "Scanned",
        });

    /// <summary>
    /// Inserts a row, giving every required column the row does not name a value of its type, so
    /// the test only spells out the columns it is about.
    /// </summary>
    private async Task InsertAsync(string table, Dictionary<string, object?> values)
    {
        var row = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await pragma.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = reader.GetString(1);
                var type = reader.GetString(2);
                var required = reader.GetInt32(3) != 0;
                var isPrimaryKey = reader.GetInt32(5) != 0;
                if (required && !isPrimaryKey && !row.ContainsKey(column))
                {
                    row[column] = string.Equals(type, "INTEGER", StringComparison.OrdinalIgnoreCase) ? 0 : "2024-01-01 00:00:00";
                }
            }
        }

        await using var insert = _connection.CreateCommand();
        var names = row.Keys.ToList();
        insert.CommandText =
            $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) " +
            $"VALUES ({string.Join(", ", names.Select((_, i) => $"$p{i}"))})";
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"$p{i}", row[names[i]] ?? DBNull.Value);
        }

        await insert.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> GetColumnsAsync(string table)
    {
        await using var pragma = _connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await pragma.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
