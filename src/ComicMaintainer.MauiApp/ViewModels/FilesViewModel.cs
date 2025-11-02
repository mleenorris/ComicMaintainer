using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicMaintainer.Core.Models;
using ComicMaintainer.MauiApp.Services;

namespace ComicMaintainer.MauiApp.ViewModels;

public partial class FilesViewModel : ObservableObject
{
    private readonly IApiService _apiService;

    [ObservableProperty]
    private ObservableCollection<ComicFile> _files = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = "all";

    [ObservableProperty]
    private int _currentPage = 1;

    public FilesViewModel(IApiService apiService)
    {
        _apiService = apiService;
    }

    [RelayCommand]
    private async Task LoadFiles()
    {
        IsLoading = true;

        try
        {
            var files = await _apiService.GetFilesAsync(
                page: CurrentPage,
                filter: SelectedFilter == "all" ? null : SelectedFilter,
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

    partial void OnSearchTextChanged(string value)
    {
        Task.Run(async () => await LoadFiles());
    }

    partial void OnSelectedFilterChanged(string value)
    {
        Task.Run(async () => await LoadFiles());
    }
}
