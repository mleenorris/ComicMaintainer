using ComicMaintainer.MauiApp.ViewModels;

namespace ComicMaintainer.MauiApp.Views;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
