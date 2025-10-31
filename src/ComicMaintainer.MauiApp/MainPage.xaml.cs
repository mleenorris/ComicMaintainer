using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.MauiApp;

public partial class MainPage : ContentPage
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IFileWatcherService _watcher;

    public MainPage(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IFileWatcherService watcher)
    {
        InitializeComponent();
        _fileStore = fileStore;
        _processor = processor;
        _watcher = watcher;
        
        // Start file watcher
        Task.Run(async () => await _watcher.StartAsync());
    }

    private async void OnBrowseClicked(object sender, EventArgs e)
    {
        try
        {
            var files = await _fileStore.GetAllFilesAsync();
            var fileCount = files.Count();
            StatusLabel.Text = $"Found {fileCount} comic files";
            
            // TODO: Navigate to file browser page
            await DisplayAlert("Browse", $"Found {fileCount} files", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to browse files: {ex.Message}", "OK");
        }
    }

    private async void OnProcessClicked(object sender, EventArgs e)
    {
        try
        {
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var fileList = files.Select(f => f.FilePath).ToList();
            
            if (!fileList.Any())
            {
                await DisplayAlert("Info", "No unprocessed files found", "OK");
                return;
            }

            var jobId = await _processor.ProcessFilesAsync(fileList);
            StatusLabel.Text = $"Processing job started: {jobId}";
            
            // TODO: Navigate to job status page
            await DisplayAlert("Processing", $"Started processing {fileList.Count} files", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to start processing: {ex.Message}", "OK");
        }
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        // TODO: Navigate to settings page
        await DisplayAlert("Settings", "Settings page coming soon", "OK");
    }
}
