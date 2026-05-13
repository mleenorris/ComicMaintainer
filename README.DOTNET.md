# ComicMaintainer - .NET Version (v2.0)

**This is the main version of ComicMaintainer**, built on .NET 9.0 and C#. It provides a robust, production-ready solution for managing comic archive files with extensive testing, performance optimizations, and modern web technologies.

This version has been converted from the original Python implementation and offers the same functionality plus additional features, including improved performance, better test coverage, and the ability to be deployed as a web application or packaged as a mobile app (Android/iOS) using .NET MAUI.

## Overview

ComicMaintainer is a service that automatically watches a directory for new or changed comic archive files (`.cbz`/`.cbr`), processes them, and provides a web interface for managing your comic collection.

## Features

- **File Watching**: Automatically monitors directories for comic file changes
- **Comic Processing**: Processes `.cbz` and `.cbr` files
- **Web Interface**: Full-featured web UI for managing comics
- **Batch Processing**: Process multiple files at once
- **Metadata Management**: View and edit comic metadata
- **Series Library View**: Browse comics as cover-based series cards and drill into issue lists
- **External Alias Grouping**: Optionally enrich series grouping with ComicVine aliases so alternate titles collapse into one series
- **Duplicate Detection**: Automatically identifies and handles duplicate files
- **RESTful API**: Clean API for integration with other tools
- **Cross-Platform**: Runs on Windows, Linux, macOS
- **Docker Support**: Easy deployment with Docker
- **Mobile Ready**: Can be packaged as a mobile app using .NET MAUI

## Architecture

The solution is organized into multiple projects:

### ComicMaintainer.Core
Core business logic and domain models:
- Models for comic files, metadata, and processing jobs
- Interfaces for services
- Service implementations:
  - `FileStoreService`: Manages file tracking
  - `FileWatcherService`: Monitors directory changes
  - `ComicProcessorService`: Processes comic files

### ComicMaintainer.WebApi
ASP.NET Core Web API application:
- RESTful API endpoints
- Static file serving for the web UI
- Hosted services for background tasks
- Controllers:
  - `FilesController`: File management endpoints
  - `JobsController`: Batch job status endpoints
  - `WatcherController`: File watcher control endpoints

### ComicMaintainer.MauiApp
.NET MAUI application for Android/iOS:
- Cross-platform mobile app
- Native UI for mobile devices
- Server connection configuration
- Browse and manage files remotely
- Uses the same Core library as the web API

## Getting Started

### Prerequisites

- .NET 9.0 SDK or later
- Docker (optional, for containerized deployment)

### Building Locally

1. Clone the repository:
```bash
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
git checkout csharp
```

2. Build the solution:
```bash
dotnet build ComicMaintainer.sln
```

3. Run the application:
```bash
cd src/ComicMaintainer.WebApi
dotnet run
```

4. Access the web interface at `http://localhost:5000`

### Using Docker

1. Build the Docker image:
```bash
docker build -f Dockerfile.dotnet -t comicmaintainer-dotnet:latest .
```

2. Run the container:
```bash
docker run -d \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -p 5000:5000 \
  comicmaintainer-dotnet:latest
```

### Using Docker Compose

1. Create your directory structure:
```bash
mkdir -p test_comics duplicates config
```

2. Start the service:
```bash
docker-compose -f docker-compose.dotnet.yml up -d
```

3. Access the web interface at `http://localhost:5000`

### Windows Installation (Native)

For Windows users who prefer to run without Docker:

1. Download the latest Windows release from [GitHub Releases](https://github.com/mleenorris/ComicMaintainer/releases)
   - `ComicMaintainer-vX.X.X-win-x64.zip` (64-bit, recommended)
   - `ComicMaintainer-vX.X.X-win-x86.zip` (32-bit)

2. Extract to a folder (e.g., `C:\ComicMaintainer`)

3. Create a `.env` file with your configuration:
   ```
   WATCHED_DIR=C:\Comics\ToProcess
   DUPLICATE_DIR=C:\Comics\Duplicates
   WEB_PORT=5000
   ```

4. Run `ComicMaintainer.WebApi.exe`

5. Access the web interface at `http://localhost:5000`

**Running as a Windows Service:**
Use [NSSM](https://nssm.cc/) to run ComicMaintainer as a Windows service that starts automatically:
```cmd
nssm install ComicMaintainer "C:\ComicMaintainer\ComicMaintainer.WebApi.exe"
nssm start ComicMaintainer
```

### Android App

The .NET MAUI Android app allows you to manage your comics from your mobile device:

1. Download `ComicMaintainer-vX.X.X-android.apk` from [GitHub Releases](https://github.com/mleenorris/ComicMaintainer/releases)

2. Install the APK on your Android device (requires Android 5.0+)

3. Configure the server URL in the app's Settings:
   - Local network: `http://192.168.1.100:5000`
   - Remote: `https://your-domain.com`

4. Browse and manage your comic files remotely

**App Features:**
- Browse comic files
- Process individual files
- Filter by status (all, processed, unprocessed, duplicates)
- Search functionality
- Real-time connection status
- Material Design UI

## Configuration

Configuration can be set via:

1. **appsettings.json** (for local development)
2. **Environment variables** (for Docker/production)

### Configuration Options

| Setting | Environment Variable | Default | Description |
|---------|---------------------|---------|-------------|
| WatchedDirectory | WATCHED_DIR | /watched_dir | Directory to watch for comic files |
| DuplicateDirectory | DUPLICATE_DIR | /duplicates | Directory for duplicate files |
| ConfigDirectory | CONFIG_DIR | /Config | Directory for configuration data |
| FilenameFormat | FILENAME_FORMAT | {series} - Chapter {issue} | Template for file naming |
| IssueNumberPadding | ISSUE_NUMBER_PADDING | 4 | Number of digits for issue numbers |
| MaxWorkers | MAX_WORKERS | 4 | Maximum concurrent processing jobs |
| WatcherEnabled | WATCHER_ENABLED | true | Enable/disable file watcher |
| WebPort | WEB_PORT | 5000 | Web server port |
| PUID | PUID | 99 | User ID for file permissions |
| PGID | PGID | 100 | Group ID for file permissions |
| EnableExternalSeriesMetadata | ENABLE_EXTERNAL_SERIES_METADATA | false | Enable ComicVine alias lookups for series grouping |
| ComicVineApiKey | COMICVINE_API_KEY | _(empty)_ | ComicVine API key used for external series alias enrichment |
| ComicVineBaseUrl | COMICVINE_BASE_URL | https://comicvine.gamespot.com/api | Override the ComicVine API base URL if needed |
| EnableMangaDexMetadata | ENABLE_MANGADEX_METADATA | false | Enable MangaDex lookups for manga/manhwa series aliases |
| MangaDexBaseUrl | MANGADEX_BASE_URL | https://api.mangadex.org | Override the MangaDex API base URL if needed |
| EnableSuwayomiMetadata | ENABLE_SUWAYOMI_METADATA | false | Query a Suwayomi-Server sidecar for series matches across community-maintained source extensions |
| SuwayomiBaseUrl | SUWAYOMI_BASE_URL | http://suwayomi:4567 | Base URL of the Suwayomi sidecar (the GraphQL endpoint is `<base>/api/graphql`) |
| SuwayomiSourceIds | SUWAYOMI_SOURCE_IDS | _(empty)_ | Comma-separated numeric Suwayomi source IDs to query; empty queries every installed source |
| SuwayomiUsername | SUWAYOMI_USERNAME | _(empty)_ | Optional Basic-auth username, only required if the Suwayomi sidecar has auth enabled |
| SuwayomiPassword | SUWAYOMI_PASSWORD | _(empty)_ | Optional Basic-auth password for the Suwayomi sidecar |

### External Metadata Providers

ComicMaintainer can enrich series with canonical titles and aliases from
external providers. Providers are tried in order: **ComicVine → MangaDex →
Suwayomi**. Each is independently optional.

#### Suwayomi sidecar

[Suwayomi-Server](https://github.com/Suwayomi/Suwayomi-Server) is a
Tachiyomi/Mihon-derived self-hosted server that loads community-maintained
source "extensions" (MangaDex, Comick, Bato, Weebcentral, MangaPlus, and many
more) and exposes them through a single GraphQL API. ComicMaintainer talks to
it over HTTP — extensions are NOT loaded in-process.

To enable:

1. Run Suwayomi as a sidecar container. An example service block is included
   (commented) in `docker-compose.dotnet.yml`. The default image is
   `ghcr.io/suwayomi/suwayomi-server:stable` listening on port 4567.
2. Open the Suwayomi web UI (e.g. `http://localhost:4567`), add an extension
   repository (such as `https://github.com/keiyoushi/extensions-source`), and
   install the source extensions you want.
3. Note the numeric source IDs from Suwayomi's Sources screen and set
   `SUWAYOMI_SOURCE_IDS` to a comma-separated list (recommended). Leaving it
   blank causes ComicMaintainer to query every installed source, which is
   slower and noisier.
4. Set `ENABLE_SUWAYOMI_METADATA=true` and `SUWAYOMI_BASE_URL=http://suwayomi:4567`
   on the `comicmaintainer-dotnet` service.
5. If you've enabled Basic auth on Suwayomi, also set `SUWAYOMI_USERNAME` /
   `SUWAYOMI_PASSWORD`.

The provider's health appears in the standard provider-health UI alongside
ComicVine and MangaDex.

## API Endpoints

For a complete, ready-to-use API collection with all endpoints, see:
- **[Postman Collection](POSTMAN_API_COLLECTION.md)** - Import into Postman for easy API testing and validation

### Files API

- `GET /api/files` - Get all files (optional ?filter=processed|unprocessed|duplicates)
- `GET /api/files/counts` - Get file statistics
- `GET /api/files/metadata?filePath={path}` - Get file metadata
- `PUT /api/files/metadata?filePath={path}` - Update file metadata
- `GET /api/files/series` - Get grouped series cards with issue lists for the library view
- `POST /api/files/process?filePath={path}` - Process a single file
- `POST /api/files/process-batch` - Process multiple files
- `POST /api/files/mark-processed?filePath={path}` - Mark file as processed

### Jobs API

- `GET /api/jobs/{jobId}` - Get batch job status

### Watcher API

- `GET /api/watcher/status` - Get watcher status
- `POST /api/watcher/enable` - Enable/disable watcher

**Note**: The above is a brief overview. See the [Postman Collection](POSTMAN_API_COLLECTION.md) for the complete API documentation with 52 endpoints organized into 9 categories.

## Development

### Project Structure

```
ComicMaintainer/
├── src/
│   ├── ComicMaintainer.Core/          # Core business logic
│   │   ├── Models/                    # Domain models
│   │   ├── Interfaces/                # Service interfaces
│   │   ├── Services/                  # Service implementations
│   │   └── Configuration/             # Configuration classes
│   ├── ComicMaintainer.WebApi/        # Web API project
│   │   ├── Controllers/               # API controllers
│   │   ├── Services/                  # Web-specific services
│   │   └── wwwroot/                   # Static files (HTML/JS/CSS)
│   └── ComicMaintainer.MauiApp/       # Mobile app (future)
├── Dockerfile.dotnet                  # Docker configuration
├── docker-compose.dotnet.yml          # Docker Compose configuration
└── ComicMaintainer.sln               # Solution file
```

### Adding Features

1. Define interfaces in `ComicMaintainer.Core/Interfaces`
2. Implement services in `ComicMaintainer.Core/Services`
3. Register services in `ComicMaintainer.WebApi/Program.cs`
4. Create controllers in `ComicMaintainer.WebApi/Controllers`

## Differences from Python Version

### Advantages of .NET Version

1. **Performance**: Generally faster execution, especially for I/O operations
2. **Type Safety**: Strong typing catches errors at compile time
3. **Tooling**: Excellent IDE support (Visual Studio, VS Code, Rider)
4. **Mobile Support**: Can be packaged as native Android/iOS apps with .NET MAUI
5. **Deployment**: Single executable or container deployment
6. **Memory Management**: Automatic garbage collection with predictable behavior
7. **Async/Await**: Built-in async support throughout the stack

### Implementation Notes

1. **Comic Processing**: ✅ Fully implemented with SharpCompress integration, ComicInfo.xml support from PR 405
2. **File Storage**: ✅ Entity Framework Core with SQLite for persistence
3. **Event Broadcasting**: ✅ SignalR for WebSocket communication
4. **Authentication**: ✅ ASP.NET Core Identity with JWT and API key support
5. **Authorization**: ✅ Role-based access control with Admin, User, and ReadOnly roles
6. **Security**: ✅ Path validation middleware, log sanitization, Docker hardening

### Security Features

- **Path Injection Protection**: Middleware validates all file paths against allowed directories
- **Log Forging Prevention**: Automatic sanitization of user input in logs
- **Non-Root Container**: Docker image runs as dedicated user (UID 1000)
- **Secrets Management**: Environment-based configuration for sensitive data
- **JWT Security**: HMAC SHA256 signing with configurable expiration

See [SECURITY_FIXES_DOTNET.md](SECURITY_FIXES_DOTNET.md) for complete security documentation.

## Future Enhancements

### Planned Features

1. **Full Comic Processing**: ✅ **Implemented**
   - ✅ Integration with SharpCompress for archive manipulation
   - ✅ ComicInfo.xml metadata parsing and writing
   - ✅ File renaming based on templates
   - ✅ Duplicate detection and handling

2. **.NET MAUI Mobile App**:
   - Native Android app
   - iOS app support
   - Shared UI components with web version

3. **Enhanced Storage**: ✅ **Implemented**
   - ✅ Entity Framework Core integration
   - ✅ SQLite database for persistence
   - ✅ Database migrations
   - 🔲 Migration from Python's file-based storage (future enhancement)

4. **Real-Time Updates**: ✅ **Implemented**
   - ✅ SignalR for WebSocket communication
   - ✅ ProgressHub for broadcasting updates
   - ✅ Job subscription support
   - 🔲 Live job progress updates (needs integration with ComicProcessorService)
   - 🔲 Real-time file status changes (needs integration with FileStoreService)

5. **Authentication & Authorization**: ✅ **Implemented**
   - ✅ ASP.NET Core Identity integration
   - ✅ User management with email and password
   - ✅ JWT token authentication
   - ✅ API key authentication
   - ✅ Role-based access control (Admin, User, ReadOnly)
   - ✅ Automatic role and admin user seeding
   - ✅ Authentication endpoints (login, register, change password, API key generation)
   - ✅ Security hardening (path validation, log sanitization)
   - ✅ Docker security best practices (non-root user, secrets management)

## Contributing

Contributions are welcome! Please follow these guidelines:

1. Fork the repository
2. Create a feature branch
3. Write tests for new functionality
4. Ensure all tests pass
5. Submit a pull request

## License

[Same license as the original Python version]

## Acknowledgments

- Original Python version by mleenorris
- Converted to .NET for enhanced cross-platform support and mobile capabilities
