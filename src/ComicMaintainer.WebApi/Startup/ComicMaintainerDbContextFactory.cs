using ComicMaintainer.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.WebApi.Startup;

// Helper class to create DbContext instances independently without lifetime conflicts
internal class ComicMaintainerDbContextFactory : IDbContextFactory<ComicMaintainerDbContext>
{
    private readonly string _connectionString;
    private readonly ILoggerFactory? _loggerFactory;

    public ComicMaintainerDbContextFactory(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        _connectionString = connectionString;
        _loggerFactory = loggerFactory;
    }

    public ComicMaintainerDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ComicMaintainerDbContext>();
        optionsBuilder.UseComicMaintainerSqlite(_connectionString, _loggerFactory);
        return new ComicMaintainerDbContext(optionsBuilder.Options);
    }
}
