using ComicMaintainer.Core.Models;
using ComicMaintainer.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace ComicMaintainer.WebApi.Controllers;

public partial class FilesController
{
    /// <summary>
    /// Lists folders that look like they hold the same series, so the user can
    /// combine them. Discovery lives in <see cref="Core.Interfaces.IFolderCombineService"/>.
    /// </summary>
    [HttpGet("combinable-folders")]
    public async Task<ActionResult<object>> GetCombinableFolders(CancellationToken cancellationToken = default)
    {
        try
        {
            var groups = await _folderCombine.GetCombinableFolderGroupsAsync(cancellationToken);
            var dtos = groups.Select(ToCombinableFolderGroupDto).ToList();
            return Ok(new { groups = dtos });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Error retrieving combinable folders");
            return StatusCode(500, "Error retrieving combinable folders");
        }
    }

    private static CombinableFolderGroupDto ToCombinableFolderGroupDto(CombinableFolderGroup group)
    {
        return new CombinableFolderGroupDto
        {
            GroupKey = group.GroupKey,
            SeriesName = group.SeriesName,
            Volume = group.Volume,
            TotalFileCount = group.TotalFileCount,
            SuggestedDestinationDirectory = group.SuggestedDestinationDirectory,
            SuggestionReason = group.SuggestionReason,
            Folders = group.Folders.Select(f => new CombinableFolderDto
            {
                Directory = f.Directory,
                FileCount = f.FileCount,
                TotalSize = f.TotalSize,
                NewestFileAddedAt = f.NewestFileAddedAt,
                OldestFileAddedAt = f.OldestFileAddedAt,
                NewestFileModifiedAt = f.NewestFileModifiedAt,
                SampleFileNames = f.SampleFileNames,
                IsSuggested = string.Equals(f.Directory, group.SuggestedDestinationDirectory, StringComparison.OrdinalIgnoreCase)
            }).ToList()
        };
    }

    public class CombinableFolderDto
    {
        public string Directory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime NewestFileAddedAt { get; set; }
        public DateTime OldestFileAddedAt { get; set; }
        public DateTime NewestFileModifiedAt { get; set; }
        public List<string> SampleFileNames { get; set; } = new();
        public bool IsSuggested { get; set; }
    }

    public class CombinableFolderGroupDto
    {
        public string GroupKey { get; set; } = string.Empty;
        public string SeriesName { get; set; } = string.Empty;
        public string? Volume { get; set; }
        public int TotalFileCount { get; set; }
        public string SuggestedDestinationDirectory { get; set; } = string.Empty;
        public string SuggestionReason { get; set; } = string.Empty;
        public List<CombinableFolderDto> Folders { get; set; } = new();
    }
}
