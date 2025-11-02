using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.Core.Models;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class FilesViewModel : ObservableObject, IDisposable
{
    private readonly IApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<ComicFile> _files = new();

    [ObservableProperty]
    private ObservableCollection<ComicFile> _selectedFiles = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "📚 All";

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private bool _hasSelectedFiles;

    [ObservableProperty]
    private string _selectionStatusText = "No files selected";

    public FilesViewModel(IApiService apiService)
    {
        _apiService = apiService;
        SelectedFiles.CollectionChanged += (s, e) => UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        HasSelectedFiles = SelectedFiles.Count > 0;
        SelectionStatusText = SelectedFiles.Count == 0 
            ? "No files selected" 
            : $"{SelectedFiles.Count} file(s) selected";
    }

    [RelayCommand]
    private async Task LoadFiles()
    {
        IsLoading = true;

        try
        {
            // Convert filter text to API filter value
            string? apiFilter = SelectedFilter switch
            {
                "⚠️ Unmarked" => "unprocessed",
                "✅ Marked" => "processed",
                "🔁 Duplicates" => "duplicate",
                _ => null
            };

            var files = await _apiService.GetFilesAsync(
                page: CurrentPage,
                filter: apiFilter,
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText
            );

            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
            }
        }
        catch (Exception ex)
        {
            await Application.Current!.MainPage!.DisplayAlert("Error", $"Failed to load files: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ProcessFile(ComicFile file)
    {
        try
        {
            var result = await _apiService.ProcessFileAsync(file.FilePath);
            if (result)
            {
                await Application.Current!.MainPage!.DisplayAlert("Success", "File processed successfully", "OK");
                await LoadFiles();
            }
            else
            {
                await Application.Current!.MainPage!.DisplayAlert("Error", "Failed to process file", "OK");
            }
        }
        catch (Exception ex)
        {
            await Application.Current!.MainPage!.DisplayAlert("Error", $"Error: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        CurrentPage++;
        await LoadFiles();
    }

    [RelayCommand]
    private async Task PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await LoadFiles();
        }
    }

    [RelayCommand]
    private async Task RefreshFiles()
    {
        CurrentPage = 1;
        await LoadFiles();
    }

    private CancellationTokenSource? _searchCts;

    async partial void OnSearchTextChanged(string value)
    {
        // Cancel any pending search and dispose the previous CancellationTokenSource
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        
        try
        {
            // Debounce search for 500ms
            await Task.Delay(500, _searchCts.Token);
            await LoadFiles();
        }
        catch (TaskCanceledException)
        {
            // Ignore cancellation
        }
    }

    async partial void OnSelectedFilterChanged(string value)
    {
        CurrentPage = 1;
        await LoadFiles();
    }

    [RelayCommand]
    private async Task ShowFileActions(ComicFile file)
    {
        var action = await Application.Current!.MainPage!.DisplayActionSheet(
            file.FileName,
            "Cancel",
            null,
            "🚀 Process",
            "📝 View Details",
            "🗑️ Delete");

        switch (action)
        {
            case "🚀 Process":
                await ProcessFile(file);
                break;
            case "📝 View Details":
                await ShowFileDetails(file);
                break;
            case "🗑️ Delete":
                await DeleteFile(file);
                break;
        }
    }

    private async Task ShowFileDetails(ComicFile file)
    {
        var details = $"File: {file.FileName}\n" +
                     $"Directory: {file.Directory}\n" +
                     $"Size: {file.FileSize:N0} bytes\n" +
                     $"Status: {(file.IsDuplicate ? "Duplicate" : file.IsProcessed ? "Processed" : "Unprocessed")}\n" +
                     $"Last Modified: {file.LastModified:g}";

        await Application.Current!.MainPage!.DisplayAlert("File Details", details, "OK");
    }

    [RelayCommand]
    private async Task DeleteFile(ComicFile file)
    {
        var confirm = await Application.Current!.MainPage!.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete {file.FileName}?",
            "Delete",
            "Cancel");

        if (confirm)
        {
            // TODO: Implement delete functionality
            await Application.Current!.MainPage!.DisplayAlert("Info", "Delete functionality not yet implemented", "OK");
        }
    }

    [RelayCommand]
    private async Task ProcessSelectedFiles()
    {
        if (!SelectedFiles.Any())
        {
            await Application.Current!.MainPage!.DisplayAlert("Info", "No files selected", "OK");
            return;
        }

        var confirm = await Application.Current!.MainPage!.DisplayAlert(
            "Confirm Process",
            $"Process {SelectedFiles.Count} file(s)?",
            "Process",
            "Cancel");

        if (confirm)
        {
            try
            {
                var files = SelectedFiles.Select(f => f.FilePath).ToList();
                var jobId = await _apiService.StartBatchProcessAsync(files);
                await Application.Current!.MainPage!.DisplayAlert("Success", $"Processing started. Job ID: {jobId}", "OK");
                SelectedFiles.Clear();
                await LoadFiles();
            }
            catch (Exception ex)
            {
                await Application.Current!.MainPage!.DisplayAlert("Error", $"Failed to start processing: {ex.Message}", "OK");
            }
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedFiles()
    {
        if (!SelectedFiles.Any())
        {
            await Application.Current!.MainPage!.DisplayAlert("Info", "No files selected", "OK");
            return;
        }

        var confirm = await Application.Current!.MainPage!.DisplayAlert(
            "Confirm Delete",
            $"Are you sure you want to delete {SelectedFiles.Count} file(s)?",
            "Delete",
            "Cancel");

        if (confirm)
        {
            // TODO: Implement bulk delete functionality
            await Application.Current!.MainPage!.DisplayAlert("Info", "Bulk delete functionality not yet implemented", "OK");
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
