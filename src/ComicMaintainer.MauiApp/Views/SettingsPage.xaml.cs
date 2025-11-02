using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.MauiApp.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
