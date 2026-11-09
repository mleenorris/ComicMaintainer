using ComicMaintainer.MauiApp.Services;
using ComicMaintainer.MauiApp.Services.Platform;
using ComicMaintainer.MauiApp.ViewModels;
using ComicMaintainer.MauiApp.Views;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.MauiApp;

public static class MauiProgram
{
    // Written in full because inside this namespace the bare name MauiApp is the namespace, not the type.
    public static Microsoft.Maui.Hosting.MauiApp CreateMauiApp()
    {
        var builder = Microsoft.Maui.Hosting.MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Redirects are not followed: see ApiTransport.
        builder.Services.AddHttpClient(ApiTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        // Register services
        builder.Services.AddSingleton<ApiTransport>();
        builder.Services.AddSingleton<ICredentialStore, SecureCredentialStore>();
        builder.Services.AddSingleton<ISettingsService, PreferencesSettingsService>();
        builder.Services.AddSingleton<ISessionManager, SessionManager>();
        builder.Services.AddSingleton<IApiService, ApiService>();
        builder.Services.AddSingleton<IUserDialogs, MauiUserDialogs>();
        builder.Services.AddSingleton<INavigator, ShellNavigator>();
        builder.Services.AddSingleton<IThemeService, MauiThemeService>();

        // Register ViewModels
        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<FilesViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Register Views
        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
