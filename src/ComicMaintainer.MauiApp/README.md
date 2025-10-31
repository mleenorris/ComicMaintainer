# ComicMaintainer MAUI App

A cross-platform mobile and desktop application for managing comic collections, built with .NET MAUI.

## Overview

The ComicMaintainer MAUI App provides a native mobile experience for Android, iOS, macOS, and Windows platforms. It integrates with the ComicMaintainer.Core library to provide comic file management capabilities on mobile devices.

## Supported Platforms

- **Android**: API 21+ (Android 5.0+)
- **iOS**: iOS 11.0+
- **macOS**: macOS 10.13+ (via Mac Catalyst)
- **Windows**: Windows 10.0.17763.0+

## Project Structure

```
ComicMaintainer.MauiApp/
├── App.xaml                     # Application definition
├── App.xaml.cs
├── AppShell.xaml                # Shell navigation
├── AppShell.xaml.cs
├── MainPage.xaml                # Main page UI
├── MainPage.xaml.cs
├── MauiProgram.cs              # App startup and DI configuration
├── Platforms/                   # Platform-specific code
│   ├── Android/
│   │   ├── AndroidManifest.xml
│   │   ├── MainActivity.cs
│   │   └── MainApplication.cs
│   ├── iOS/
│   │   ├── AppDelegate.cs
│   │   ├── Info.plist
│   │   └── Program.cs
│   ├── MacCatalyst/
│   │   ├── AppDelegate.cs
│   │   ├── Info.plist
│   │   └── Program.cs
│   └── Windows/
│       ├── App.xaml
│       ├── App.xaml.cs
│       └── app.manifest
└── Resources/                   # App resources
    ├── AppIcon/                # Application icons
    ├── Splash/                 # Splash screen
    ├── Images/                 # Image assets
    ├── Fonts/                  # Custom fonts
    └── Styles/                 # XAML styles
        ├── Colors.xaml
        └── Styles.xaml
```

## Prerequisites

- .NET 9.0 SDK or later
- .NET MAUI workload installed
- Platform-specific SDKs:
  - Android: Android SDK (installed with MAUI workload)
  - iOS/macOS: Xcode (macOS only)
  - Windows: Windows App SDK

## Installation

### Install .NET MAUI Workload

```bash
dotnet workload install maui
```

This installs:
- .NET MAUI framework
- Android SDK and build tools
- iOS SDK (on macOS)
- Windows App SDK (on Windows)

## Building

### Build for All Platforms

```bash
dotnet build src/ComicMaintainer.MauiApp
```

### Build for Specific Platform

```bash
# Android
dotnet build src/ComicMaintainer.MauiApp -f net9.0-android

# iOS (macOS only)
dotnet build src/ComicMaintainer.MauiApp -f net9.0-ios

# macOS (macOS only)
dotnet build src/ComicMaintainer.MauiApp -f net9.0-maccatalyst

# Windows (Windows only)
dotnet build src/ComicMaintainer.MauiApp -f net9.0-windows10.0.19041.0
```

## Running

### Run on Android Emulator

```bash
dotnet build -t:Run -f net9.0-android src/ComicMaintainer.MauiApp
```

### Run on iOS Simulator (macOS only)

```bash
dotnet build -t:Run -f net9.0-ios src/ComicMaintainer.MauiApp
```

### Run on Windows

```bash
dotnet build -t:Run -f net9.0-windows10.0.19041.0 src/ComicMaintainer.MauiApp
```

## Features

### Current Features

- **Browse Comics**: View all comic files in the watched directory
- **Process Comics**: Batch process unprocessed comic files
- **Settings**: Configure app settings and directories
- **File Watcher**: Automatically detect new comic files

### Planned Features

- File browser page with detailed comic information
- Job status page with real-time progress tracking
- Comic reader/viewer integration
- Server sync capabilities
- Advanced filtering and search

## Architecture

### Dependency Injection

The app uses Microsoft.Extensions.DependencyInjection for service registration in `MauiProgram.cs`:

```csharp
// Core services
builder.Services.AddSingleton<IFileStoreService, FileStoreService>();
builder.Services.AddSingleton<IComicProcessorService, ComicProcessorService>();
builder.Services.AddSingleton<IFileWatcherService, FileWatcherService>();

// Pages
builder.Services.AddSingleton<MainPage>();
```

### Data Storage

The app stores data in platform-specific app data directories:
- **Android**: `/data/data/com.comicmaintainer.app/files/`
- **iOS**: `~/Library/Application Support/`
- **Windows**: `%LOCALAPPDATA%\ComicMaintainer\`

## Android-Specific Configuration

### Permissions

The app requires the following Android permissions:
- `READ_EXTERNAL_STORAGE`: Read comic files
- `WRITE_EXTERNAL_STORAGE`: Write processed files
- `INTERNET`: Network communication
- `ACCESS_NETWORK_STATE`: Check network status

Permissions are requested at runtime in `MainActivity.cs` for Android 6.0+.

### Minimum SDK Version

- **minSdkVersion**: 21 (Android 5.0)
- **targetSdkVersion**: 34 (Android 14)

## Development

### Using Visual Studio

1. Open `ComicMaintainer.sln` in Visual Studio 2022
2. Set `ComicMaintainer.MauiApp` as the startup project
3. Select target platform (Android Emulator, iOS Simulator, etc.)
4. Press F5 to build and run

### Using VS Code

1. Install the C# Dev Kit extension
2. Open the solution folder
3. Use the Debug panel to select and run the project

### Using Command Line

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run (specify framework)
dotnet build -t:Run -f net9.0-android
```

## Deployment

### Android APK

#### Debug Build

```bash
dotnet build src/ComicMaintainer.MauiApp -f net9.0-android -c Release
```

APK location: `src/ComicMaintainer.MauiApp/bin/Release/net9.0-android/`

#### Signed Release Build

1. Create a keystore:
```bash
keytool -genkey -v -keystore comicmaintainer.keystore -alias comicmaintainer -keyalg RSA -keysize 2048 -validity 10000
```

2. Add signing configuration to `.csproj`:
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

### iOS App (macOS only)

Requires Apple Developer account and proper provisioning profiles.

```bash
dotnet publish src/ComicMaintainer.MauiApp -f net9.0-ios -c Release
```

## Troubleshooting

### MAUI Workload Issues

```bash
# Restore workload
dotnet workload restore

# Reinstall MAUI
dotnet workload install maui
```

### Android SDK Issues

```bash
# Install Android SDK tools
dotnet build -t:InstallAndroidPlatform -f net9.0-android
```

### Build Errors

```bash
# Clean build
dotnet clean

# Clear NuGet cache
dotnet nuget locals all --clear

# Rebuild
dotnet build
```

### Platform-Specific Issues

- **Android**: Ensure Android SDK is properly installed
- **iOS**: Requires macOS and Xcode
- **Windows**: Requires Windows 10/11 with Windows App SDK

## Integration with Web API

The MAUI app can connect to the ComicMaintainer Web API for centralized management:

```csharp
// In MauiProgram.cs
builder.Services.AddHttpClient("ComicMaintainerApi", client =>
{
    client.BaseAddress = new Uri("http://your-server:5000");
});
```

## Contributing

When contributing to the MAUI app:
1. Follow the existing code style
2. Test on multiple platforms
3. Update documentation for new features
4. Ensure backward compatibility with the Core library

## Resources

- [.NET MAUI Documentation](https://docs.microsoft.com/dotnet/maui/)
- [.NET MAUI Android Guide](https://docs.microsoft.com/dotnet/maui/android/)
- [MAUI Community Toolkit](https://github.com/CommunityToolkit/Maui)
- [ComicMaintainer Documentation](../../README.md)

## License

See the main [LICENSE](../../LICENSE) file for details.
