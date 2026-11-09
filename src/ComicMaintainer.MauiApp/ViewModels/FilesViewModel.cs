using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.MauiApp.Extensions;
using ComicMaintainer.MauiApp.Models;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class FilesViewModel : ObservableObject, IDisposable
{
    private readonly IApiService _apiService;
    private readonly ISessionManager _session;
    private readonly IUserDialogs _dialogs;

    [ObservableProperty]
    private ObservableCollection<ComicFile> _files = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "📚 All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageText))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPage), nameof(PageText))]
    private int _totalPages = 1;

    [ObservableProperty]
    private bool _hasSelectedFiles;

    [ObservableProperty]
    private string _selectionStatusText = "No files selected";

    public FilesViewModel(IApiService apiService, ISessionManager session, IUserDialogs dialogs)
    {
        _apiService = apiService;
        _session = session;
        _dialogs = dialogs;
        SelectedFiles.CollectionChanged += (s, e) => UpdateSelectionStatus();
        _session.SessionEnded += OnSessionEnded;
    }

    /// <summary>
    /// The selected comics. CollectionView writes its selection into this list, which it requires to be a list of
    /// objects: bound to a list of <see cref="ComicFile"/> it would silently never select anything.
    /// </summary>
    public ObservableCollection<object> SelectedFiles { get; } = new();

    public bool HasPreviousPage => CurrentPage > 1;

    public bool HasNextPage => CurrentPage < TotalPages;

    public string PageText => $"Page {CurrentPage} of {TotalPages}";

    private List<string> SelectedPaths => SelectedFiles.OfType<ComicFile>().Select(file => file.FilePath).ToList();

    /// <summary>The API's value for an item of the filter picker, or null for no filter.</summary>
    public static string? ToApiFilter(string pickerItem)
    {
        // By the end of the text, which is free of the emoji and their invisible variation selectors.
        if (pickerItem.EndsWith("Unmarked", StringComparison.Ordinal))
        {
            return "unmarked";
        }

        if (pickerItem.EndsWith("Marked", StringComparison.Ordinal))
        {
            return "marked";
        }

        return pickerItem.EndsWith("Duplicates", StringComparison.Ordinal) ? "duplicates" : null;
    }

    private void UpdateSelectionStatus()
    {
        HasSelectedFiles = SelectedFiles.Count > 0;
        SelectionStatusText = SelectedFiles.Count == 0
            ? "No files selected"
            : $"{SelectedFiles.Count} file(s) selected";
    }

    // The next account must not see this one's library.
    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        Files.Clear();
        SelectedFiles.Clear();
        CurrentPage = 1;
        TotalPages = 1;
    }

    // A session that ended is explained on the sign-in page, which the app is already moving to.
    private Task ReportAsync(ApiException exception) =>
        exception.Kind == ApiErrorKind.Unauthorized
            ? Task.CompletedTask
            : _dialogs.ShowAlertAsync("Error", exception.Message);

    [RelayCommand]
    private async Task LoadFiles()
    {
        IsLoading = true;

        try
        {
            var result = await _apiService.GetFilesAsync(
                page: CurrentPage,
                filter: ToApiFilter(SelectedFilter),
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);

            Files.Clear();
            foreach (var file in result.Files)
            {
                Files.Add(file);
            }

            // The server clamps the page, for example when the library shrank since the last load.
            CurrentPage = result.Page;
            TotalPages = Math.Max(1, result.TotalPages);
        }
        catch (ApiException exception)
        {
            await ReportAsync(exception);
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
            await _apiService.ProcessFileAsync(file.FilePath);
            await _dialogs.ShowAlertAsync("Success", "File processed successfully");
            await LoadFiles();
        }
        catch (ApiException exception)
        {
            await ReportAsync(exception);
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (HasNextPage)
        {
            CurrentPage++;
            await LoadFiles();
        }
    }

    [RelayCommand]
    private async Task PreviousPage()
    {
        if (HasPreviousPage)
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
            CurrentPage = 1;
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
        var action = await _dialogs.ChooseActionAsync(
            file.FileName,
            "Cancel",
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

    private Task ShowFileDetails(ComicFile file)
    {
        var details = $"File: {file.FileName}\n" +
                     $"Directory: {file.Directory}\n" +
                     $"Size: {file.FileSize:N0} bytes\n" +
                     $"Status: {file.GetStatus()}\n" +
                     $"Last Modified: {file.LastModified:g}";

        return _dialogs.ShowAlertAsync("File Details", details);
    }

    [RelayCommand]
    private async Task DeleteFile(ComicFile file)
    {
        if (!await _dialogs.ConfirmAsync("Confirm Delete", $"Are you sure you want to delete {file.FileName}?", "Delete", "Cancel"))
        {
            return;
        }

        try
        {
            await _apiService.DeleteFileAsync(file.FilePath);
            await LoadFiles();
        }
        catch (ApiException exception)
        {
            await ReportAsync(exception);
        }
    }

    [RelayCommand]
    private async Task ProcessSelectedFiles()
    {
        if (SelectedFiles.Count == 0)
        {
            await _dialogs.ShowAlertAsync("Info", "No files selected");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Confirm Process", $"Process {SelectedFiles.Count} file(s)?", "Process", "Cancel"))
        {
            return;
        }

        try
        {
            var count = SelectedFiles.Count;
            await _apiService.StartBatchProcessAsync(SelectedPaths);
            await _dialogs.ShowAlertAsync("Success", $"Processing {count} file(s) on the server.");
            SelectedFiles.Clear();
            await LoadFiles();
        }
        catch (ApiException exception)
        {
            await ReportAsync(exception);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedFiles()
    {
        if (SelectedFiles.Count == 0)
        {
            await _dialogs.ShowAlertAsync("Info", "No files selected");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Confirm Delete", $"Are you sure you want to delete {SelectedFiles.Count} file(s)?", "Delete", "Cancel"))
        {
            return;
        }

        try
        {
            var count = SelectedFiles.Count;
            await _apiService.StartBatchDeleteAsync(SelectedPaths);
            await _dialogs.ShowAlertAsync("Success", $"Deleting {count} file(s) on the server.");
            SelectedFiles.Clear();
            await LoadFiles();
        }
        catch (ApiException exception)
        {
            await ReportAsync(exception);
        }
    }

    public void Dispose()
    {
        _session.SessionEnded -= OnSessionEnded;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
