using System.Reflection;
using ComicMaintainer.WebApi.Controllers;
using ComicMaintainer.WebApi.Infrastructure;

namespace ComicMaintainer.Tests.Infrastructure;

/// <summary>
/// Pins the message each action gives the global exception handler. The web UI shows a
/// failure response's <c>error</c> field to the user, so these strings are what the user
/// reads when an action fails; losing one silently degrades that message to the generic
/// "request could not be completed".
/// </summary>
public class FailureMessageAttributeTests
{
#pragma warning disable CS0618 // The table covers the deprecated aliases until they are removed.
    [Theory]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetAdjacentFile), "Error finding adjacent file")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetComicInfo), "Error reading comic file")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetCover), "Error reading cover")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetPage), "Error reading page")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetPages), "Error reading comic file")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.GetProgress), "Error getting reading progress")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.MarkAsRead), "Error marking file as read")]
    [InlineData(typeof(ComicReaderController), nameof(ComicReaderController.SaveProgress), "Error saving reading progress")]
    [InlineData(typeof(EmailController), nameof(EmailController.DownloadCondensedBook), "Failed to build the condensed book")]
    [InlineData(typeof(FilesController), nameof(FilesController.CombineFolders), "Error combining folders")]
    [InlineData(typeof(FilesController), nameof(FilesController.DeleteFile), "Error deleting file")]
    [InlineData(typeof(FilesController), nameof(FilesController.DeleteFileByEncodedPath), "Error deleting file")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetCombinableFolders), "Error retrieving combinable folders")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetFileCounts), "Error retrieving file counts")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetFiles), "Error retrieving files")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetFileTags), "Error getting tags")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetFolderFiles), "Error retrieving folder files")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetRootFolderFiles), "Error retrieving folder files")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetFolders), "Error retrieving folders")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetMetadata), "Error retrieving metadata")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetSeries), "Error retrieving series library")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetSeriesFolders), "Error retrieving series folders")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetSeriesIssues), "Error retrieving series issues")]
    [InlineData(typeof(FilesController), nameof(FilesController.GetSeriesResolution), "Error resolving series")]
    [InlineData(typeof(FilesController), nameof(FilesController.MarkFileRead), "Error updating read status")]
    [InlineData(typeof(FilesController), nameof(FilesController.MarkFilesReadBatch), "Error updating read status")]
    [InlineData(typeof(FilesController), nameof(FilesController.MarkScopeRead), "Error updating read status")]
    [InlineData(typeof(FilesController), nameof(FilesController.PatchMetadata), "Error patching metadata")]
    [InlineData(typeof(FilesController), nameof(FilesController.PreviewCombineFolders), "Error building combine preview")]
    [InlineData(typeof(FilesController), nameof(FilesController.ProcessBatch), "Error starting batch processing")]
    [InlineData(typeof(FilesController), nameof(FilesController.ProcessFile), "Error processing file")]
    [InlineData(typeof(FilesController), nameof(FilesController.ProcessFileByEncodedPath), "Error processing file")]
    [InlineData(typeof(FilesController), nameof(FilesController.RemoveFileMetadata), "Error removing metadata")]
    [InlineData(typeof(FilesController), nameof(FilesController.RenameFileByEncodedPath), "Error renaming file")]
    [InlineData(typeof(FilesController), nameof(FilesController.RenameSingleFile), "Error renaming file")]
    [InlineData(typeof(FilesController), nameof(FilesController.ScanUnmarked), "Error scanning files")]
    [InlineData(typeof(FilesController), nameof(FilesController.ScanUnmarkedLegacy), "Error scanning files")]
    [InlineData(typeof(FilesController), nameof(FilesController.UpdateFileTags), "Error updating tags")]
    [InlineData(typeof(FilesController), nameof(FilesController.UpdateMetadata), "Error updating metadata")]
    [InlineData(typeof(FilesController), nameof(FilesController.UpdateTags), "Error updating tags")]
    [InlineData(typeof(LogsController), nameof(LogsController.GetLogFiles), "Error listing log files")]
    [InlineData(typeof(LogsController), nameof(LogsController.GetLogs), "Error reading log file")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.ApplyMatch), "Error applying match")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.ClearExternal), "Error clearing external metadata")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.GetProviders), "Error retrieving provider health")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.MatchUnmatched), "Error queuing metadata match")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.RefreshAll), "Error queuing metadata refresh")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.RefreshFolder), "Error queuing metadata refresh")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.RefreshOne), "Error refreshing metadata")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.RefreshSelected), "Error queuing metadata refresh")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.Search), "Error searching external metadata")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.SetAliases), "Error updating aliases")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.SetPreferredLanguage), "Error setting preferred language")]
    [InlineData(typeof(MetadataController), nameof(MetadataController.SetSeriesName), "Error setting series name")]
    [InlineData(typeof(OverviewController), nameof(OverviewController.Get), "Error building overview")]
    [InlineData(typeof(ProcessingHistoryController), nameof(ProcessingHistoryController.GetProcessingHistory), "Error retrieving processing history")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.ApplyCurrentToAll), "Error updating series covers")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.ApplyCurrentToSelected), "Error updating series covers")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.ApplyFromProvider), "Error applying provider image")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.Clear), "Error clearing series image")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.SearchCandidates), "Error searching provider image candidates")]
    [InlineData(typeof(SeriesImagesController), nameof(SeriesImagesController.Upload), "Error saving series image")]
    [InlineData(typeof(WatcherController), nameof(WatcherController.GetStatus), "Error retrieving watcher status")]
    [InlineData(typeof(WatcherController), nameof(WatcherController.GetWatcher), "Error retrieving watcher status")]
#pragma warning restore CS0618
    public void Action_DeclaresTheMessageForItsFailures(Type controller, string action, string expected)
    {
        var message = controller.GetMethods()
            .Where(m => m.Name == action)
            .Select(m => m.GetCustomAttribute<FailureMessageAttribute>())
            .OfType<FailureMessageAttribute>()
            .Single()
            .Message;

        Assert.Equal(expected, message);
    }
}
