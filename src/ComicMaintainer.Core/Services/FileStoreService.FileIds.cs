using Microsoft.EntityFrameworkCore;

namespace ComicMaintainer.Core.Services;

public partial class FileStoreService
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> GetFileIdsAsync(IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ComicFiles
            .AsNoTracking()
            .Where(f => filePaths.Contains(f.FilePath))
            .Select(f => new { f.FilePath, f.Id })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.FilePath, r => r.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<string?> GetFilePathAsync(int fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ComicFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.FilePath)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
