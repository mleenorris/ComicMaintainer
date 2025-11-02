using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.MauiApp.Views;

public partial class MainPage : ContentPage
{
    private readonly FilesViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;

    public MainPage(FilesViewModel viewModel, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadFilesCommand.ExecuteAsync(null);
    }

    private async void OnSettingsMenuClicked(object? sender, EventArgs e)
    {
        var settingsPage = _serviceProvider.GetRequiredService<SettingsPage>();
        await Navigation.PushAsync(settingsPage);
    }
}
