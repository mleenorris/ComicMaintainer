using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using Microsoft.Extensions.Logging;

namespace ComicMaintainer.MauiApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Configure app settings
        builder.Services.Configure<AppSettings>(options =>
        {
            // For mobile, we'll use app-specific storage
            var appDataPath = FileSystem.AppDataDirectory;
            options.WatchedDirectory = Path.Combine(appDataPath, "watched");
            options.DuplicateDirectory = Path.Combine(appDataPath, "duplicates");
            options.ConfigDirectory = Path.Combine(appDataPath, "config");
            options.WatcherEnabled = true;
        });

        // Register services
        builder.Services.AddSingleton<IFileStoreService, FileStoreService>();
        builder.Services.AddSingleton<IComicProcessorService, ComicProcessorService>();
        builder.Services.AddSingleton<IFileWatcherService, FileWatcherService>();

        // Register pages
        builder.Services.AddSingleton<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
