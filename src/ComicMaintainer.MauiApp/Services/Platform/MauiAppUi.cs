namespace ComicMaintainer.MauiApp.Services.Platform;

/// <summary>Shows dialogs on the current page.</summary>
public sealed class MauiUserDialogs : IUserDialogs
{
    private static Page CurrentPage => Shell.Current.CurrentPage;

    public Task ShowAlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => CurrentPage.DisplayAlertAsync(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => CurrentPage.DisplayAlertAsync(title, message, accept, cancel));

    public Task<string?> ChooseActionAsync(string title, string cancel, params string[] actions) =>
        MainThread.InvokeOnMainThreadAsync<string?>(() => CurrentPage.DisplayActionSheetAsync(title, cancel, null, actions));
}

/// <summary>Moves between the Shell's two top-level pages, which also clears anything pushed on top of them.</summary>
public sealed class ShellNavigator : INavigator
{
    public Task ShowSignInAsync() => GoToAsync("//login");

    public Task ShowLibraryAsync() => GoToAsync("//library");

    private static Task GoToAsync(string route) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(route));
}

/// <summary>Applies the app's light or dark appearance.</summary>
public sealed class MauiThemeService : IThemeService
{
    public bool IsDarkMode
    {
        get => Application.Current?.UserAppTheme == AppTheme.Dark;
        set
        {
            if (Application.Current is { } application)
            {
                application.UserAppTheme = value ? AppTheme.Dark : AppTheme.Light;
            }
        }
    }
}
