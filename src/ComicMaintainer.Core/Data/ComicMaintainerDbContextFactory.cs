using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ComicMaintainer.Core.Data;

/// <summary>
/// Design-time factory for creating ComicMaintainerDbContext instances during migrations
/// </summary>
public class ComicMaintainerDbContextFactory : IDesignTimeDbContextFactory<ComicMaintainerDbContext>
{
    public ComicMaintainerDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ComicMaintainerDbContext>();
        optionsBuilder.UseSqlite("Data Source=comicmaintainer.db");
        
        return new ComicMaintainerDbContext(optionsBuilder.Options);
    }
}
