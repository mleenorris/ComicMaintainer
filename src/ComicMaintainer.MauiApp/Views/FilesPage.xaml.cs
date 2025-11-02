using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.MauiApp.Views;

public partial class FilesPage : ContentPage
{
    private readonly FilesViewModel _viewModel;

    public FilesPage(FilesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadFilesCommand.ExecuteAsync(null);
    }
}
