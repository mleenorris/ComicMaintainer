# .NET MAUI Android App Setup Guide

This guide explains how to add a .NET MAUI Android app project to the ComicMaintainer solution.

## Prerequisites

- .NET 9.0 SDK or later
- .NET MAUI workload installed
- Android SDK (automatically installed with MAUI workload)
- Visual Studio 2022 (Windows/Mac) or Visual Studio Code with C# Dev Kit

## Installing .NET MAUI Workload

If you haven't installed the .NET MAUI workload yet, run:

```bash
dotnet workload install maui
```

This will install:
- .NET MAUI framework
- Android SDK and emulators
- iOS SDK (on Mac)
- Windows App SDK (on Windows)

## Creating the MAUI Project

### Option 1: Using .NET CLI

1. Navigate to the solution directory:
```bash
cd /path/to/ComicMaintainer
```

2. Create the MAUI project:
```bash
dotnet new maui -n ComicMaintainer.MauiApp -o src/ComicMaintainer.MauiApp
```

3. Add the project to the solution:
```bash
dotnet sln add src/ComicMaintainer.MauiApp/ComicMaintainer.MauiApp.csproj
```

4. Add reference to the Core library:
```bash
cd src/ComicMaintainer.MauiApp
dotnet add reference ../ComicMaintainer.Core/ComicMaintainer.Core.csproj
```

### Option 2: Using Visual Studio

1. Open `ComicMaintainer.sln` in Visual Studio
2. Right-click on the solution → Add → New Project
3. Select ".NET MAUI App" template
4. Name it "ComicMaintainer.MauiApp"
5. Choose location: `src/ComicMaintainer.MauiApp`
6. Right-click on the MAUI project → Add → Project Reference
7. Select "ComicMaintainer.Core"

## Project Structure

After creation, your MAUI project should look like:

```
src/ComicMaintainer.MauiApp/
├── MauiProgram.cs              # App startup and DI configuration
├── App.xaml                     # Application definition
├── App.xaml.cs
├── AppShell.xaml                # App navigation shell
├── AppShell.xaml.cs
├── MainPage.xaml                # Main page UI
├── MainPage.xaml.cs
├── Platforms/                   # Platform-specific code
│   ├── Android/
│   ├── iOS/
│   ├── MacCatalyst/
│   └── Windows/
├── Resources/                   # App resources
│   ├── AppIcon/
│   ├── Images/
│   ├── Fonts/
│   └── Styles/
└── ComicMaintainer.MauiApp.csproj
```

## Configuring the MAUI App

### 1. Update MauiProgram.cs

Replace the default `MauiProgram.cs` with:

```csharp
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
```

### 2. Update MainPage.xaml

Replace the default `MainPage.xaml` with a comic file browser UI:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="ComicMaintainer.MauiApp.MainPage"
             Title="Comic Maintainer">

    <ScrollView>
        <VerticalStackLayout Spacing="25" Padding="30,0" VerticalOptions="Center">
            
            <Label 
                Text="Comic Maintainer"
                SemanticProperties.HeadingLevel="Level1"
                FontSize="32"
                HorizontalOptions="Center" />

            <Label 
                Text="Manage your comic collection on the go"
                SemanticProperties.HeadingLevel="Level2"
                SemanticProperties.Description="Welcome message"
                FontSize="18"
                HorizontalOptions="Center" />

            <Button 
                x:Name="BrowseBtn"
                Text="Browse Comics"
                SemanticProperties.Hint="Browse comic files"
                Clicked="OnBrowseClicked"
                HorizontalOptions="Center" />

            <Button 
                x:Name="ProcessBtn"
                Text="Process All"
                SemanticProperties.Hint="Process all unprocessed comics"
                Clicked="OnProcessClicked"
                HorizontalOptions="Center" />

            <Button 
                x:Name="SettingsBtn"
                Text="Settings"
                SemanticProperties.Hint="Open settings"
                Clicked="OnSettingsClicked"
                HorizontalOptions="Center" />

            <Label x:Name="StatusLabel"
                   Text=""
                   FontSize="14"
                   HorizontalOptions="Center" />

        </VerticalStackLayout>
    </ScrollView>

</ContentPage>
```

### 3. Update MainPage.xaml.cs

```csharp
using ComicMaintainer.Core.Interfaces;

namespace ComicMaintainer.MauiApp;

public partial class MainPage : ContentPage
{
    private readonly IFileStoreService _fileStore;
    private readonly IComicProcessorService _processor;
    private readonly IFileWatcherService _watcher;

    public MainPage(
        IFileStoreService fileStore,
        IComicProcessorService processor,
        IFileWatcherService watcher)
    {
        InitializeComponent();
        _fileStore = fileStore;
        _processor = processor;
        _watcher = watcher;
        
        // Start file watcher
        Task.Run(async () => await _watcher.StartAsync());
    }

    private async void OnBrowseClicked(object sender, EventArgs e)
    {
        try
        {
            var files = await _fileStore.GetAllFilesAsync();
            var fileCount = files.Count();
            StatusLabel.Text = $"Found {fileCount} comic files";
            
            // TODO: Navigate to file browser page
            await DisplayAlert("Browse", $"Found {fileCount} files", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to browse files: {ex.Message}", "OK");
        }
    }

    private async void OnProcessClicked(object sender, EventArgs e)
    {
        try
        {
            var files = await _fileStore.GetFilteredFilesAsync("unprocessed");
            var fileList = files.Select(f => f.FilePath).ToList();
            
            if (!fileList.Any())
            {
                await DisplayAlert("Info", "No unprocessed files found", "OK");
                return;
            }

            var jobId = await _processor.ProcessFilesAsync(fileList);
            StatusLabel.Text = $"Processing job started: {jobId}";
            
            // TODO: Navigate to job status page
            await DisplayAlert("Processing", $"Started processing {fileList.Count} files", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to start processing: {ex.Message}", "OK");
        }
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        // TODO: Navigate to settings page
        await DisplayAlert("Settings", "Settings page coming soon", "OK");
    }
}
```

## Android-Specific Configuration

### 1. Update AndroidManifest.xml

Add required permissions in `Platforms/Android/AndroidManifest.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application android:allowBackup="true" android:icon="@mipmap/appicon" android:roundIcon="@mipmap/appicon_round" android:supportsRtl="true"></application>
    
    <!-- Permissions -->
    <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.INTERNET" />
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
    
    <uses-sdk android:minSdkVersion="21" android:targetSdkVersion="34" />
</manifest>
```

### 2. Request Runtime Permissions

Add permission handling code in `Platforms/Android/MainActivity.cs`:

```csharp
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ComicMaintainer.MauiApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        
        // Request storage permissions for Android 6.0+
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            RequestPermissions(new[] {
                Android.Manifest.Permission.ReadExternalStorage,
                Android.Manifest.Permission.WriteExternalStorage
            }, 0);
        }
    }
}
```

## Building and Running

### Build the Android App

```bash
# Build for Android
dotnet build src/ComicMaintainer.MauiApp -f net9.0-android

# Build release version
dotnet build src/ComicMaintainer.MauiApp -f net9.0-android -c Release
```

### Run on Emulator

```bash
# List available emulators
dotnet build -t:Run -f net9.0-android

# Or use Visual Studio:
# 1. Set ComicMaintainer.MauiApp as startup project
# 2. Select Android Emulator from device dropdown
# 3. Press F5 to run
```

### Deploy to Physical Device

1. Enable Developer Mode on your Android device
2. Enable USB Debugging
3. Connect device via USB
4. Run:
```bash
dotnet build -t:Run -f net9.0-android
```

## Creating APK for Distribution

### Debug APK
```bash
dotnet build src/ComicMaintainer.MauiApp -f net9.0-android -c Release
```

The APK will be in:
```
src/ComicMaintainer.MauiApp/bin/Release/net9.0-android/
```

### Signed Release APK

1. Create a keystore:
```bash
keytool -genkey -v -keystore comicmaintainer.keystore -alias comicmaintainer -keyalg RSA -keysize 2048 -validity 10000
```

2. Update the `.csproj` file:
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release' and '$(TargetFramework)' == 'net9.0-android'">
    <AndroidKeyStore>true</AndroidKeyStore>
    <AndroidSigningKeyStore>comicmaintainer.keystore</AndroidSigningKeyStore>
    <AndroidSigningKeyAlias>comicmaintainer</AndroidSigningKeyAlias>
    <AndroidSigningKeyPass>your-password</AndroidSigningKeyPass>
    <AndroidSigningStorePass>your-password</AndroidSigningStorePass>
</PropertyGroup>
```

3. Build signed APK:
```bash
dotnet publish src/ComicMaintainer.MauiApp -f net9.0-android -c Release
```

## Testing the App

### Unit Tests

Create a test project:
```bash
dotnet new xunit -n ComicMaintainer.MauiApp.Tests -o tests/ComicMaintainer.MauiApp.Tests
dotnet sln add tests/ComicMaintainer.MauiApp.Tests/ComicMaintainer.MauiApp.Tests.csproj
```

### Integration with Web API

The mobile app can connect to the Web API running on your local network or cloud:

```csharp
// In MauiProgram.cs, add HTTP client
builder.Services.AddHttpClient("ComicMaintainerApi", client =>
{
    client.BaseAddress = new Uri("http://your-server:5000");
});
```

## Next Steps

1. **File Browser Page**: Create a page to browse and manage comic files
2. **Job Status Page**: Show real-time progress of batch processing jobs
3. **Settings Page**: Configure app settings and server connection
4. **File Viewer**: Integrate a comic reader to view comics in the app
5. **Sync with Server**: Sync with the Web API for centralized management

## Troubleshooting

### MAUI Workload Not Found
```bash
dotnet workload restore
dotnet workload install maui
```

### Android SDK Issues
```bash
# Update Android SDK
dotnet build -t:InstallAndroidPlatform -f net9.0-android
```

### Build Errors
- Clean and rebuild: `dotnet clean && dotnet build`
- Delete `bin` and `obj` folders
- Clear NuGet cache: `dotnet nuget locals all --clear`

## Resources

- [.NET MAUI Documentation](https://docs.microsoft.com/dotnet/maui/)
- [.NET MAUI Android Guide](https://docs.microsoft.com/dotnet/maui/android/)
- [MAUI Community Toolkit](https://github.com/CommunityToolkit/Maui)
