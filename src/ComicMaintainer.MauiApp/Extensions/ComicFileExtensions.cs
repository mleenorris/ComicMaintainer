using ComicMaintainer.Core.Models;

namespace ComicMaintainer.MauiApp.Extensions;

public static class ComicFileExtensions
{
    public static string GetFolderName(this ComicFile file)
    {
        if (string.IsNullOrEmpty(file.Directory))
            return "/";
        
        var parts = file.Directory.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : "/";
    }

    public static string GetStatus(this ComicFile file)
    {
        if (file.IsDuplicate)
            return "Duplicate";
        if (file.IsProcessed)
            return "Processed";
        return "Unprocessed";
    }
}
