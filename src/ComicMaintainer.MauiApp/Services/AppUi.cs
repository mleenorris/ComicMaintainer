namespace ComicMaintainer.MauiApp.Services;

/// <summary>The dialogs the view models show, kept behind an interface so that they do not depend on MAUI.</summary>
public interface IUserDialogs
{
    Task ShowAlertAsync(string title, string message);

    /// <summary>Asks a yes-or-no question and returns whether the user accepted.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);

    /// <summary>Offers a choice and returns the chosen action, or null when the user dismissed it.</summary>
    Task<string?> ChooseActionAsync(string title, string cancel, params string[] actions);
}

/// <summary>Moves between the app's two top-level pages.</summary>
public interface INavigator
{
    Task ShowSignInAsync();

    Task ShowLibraryAsync();
}

/// <summary>The app's light or dark appearance.</summary>
public interface IThemeService
{
    bool IsDarkMode { get; set; }
}
