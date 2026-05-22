# ComicMaintainer - .NET Version (v2.0)

**This is the main version of ComicMaintainer**, built on .NET 9.0 and C#. It provides a robust, production-ready solution for managing comic archive files with extensive testing, performance optimizations, and modern web technologies.

This version has been converted from the original Python implementation and offers the same functionality plus additional features, including improved performance, better test coverage, and the ability to be deployed as a web application or packaged as a mobile app (Android/iOS) using .NET MAUI.

## Overview

ComicMaintainer is a service that automatically watches a directory for new or changed comic archive files (`.cbz`/`.cbr`), processes them, and provides a web interface for managing your comic collection.

## Features

- **File Watching**: Automatically monitors directories for comic file changes
- **Comic Processing**: Processes `.cbz` and `.cbr` files
- **Web Interface**: Full-featured web UI for managing comics
- **Scheduled Jobs**: Manage recurring background jobs (interval, enable/disable, run now) from a dedicated UI page. Ships with a *Metadata Audit* job that walks every tracked file and reports any with a missing chapter/issue number or a series tag that doesn't match the expected resolved series name, plus a *Library Scan* job (`library-scan`) that reconciles additions/deletions and re-normalizes files whose series-metadata stamp is stale. New jobs can be added by registering a class that implements `IScheduledJobHandler`.
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
  - `FileWatcherService`: Live filesystem watcher (legacy; see "Library scan" below)
  - `ComicProcessorService`: Processes comic files
  - `SeriesNameResolver`: Single source of truth for the priority order used to
    resolve the expected `<Series>` value for a file (user-canonical cache →
    matched cache → localized-title back-reference → external lookup →
    existing metadata → folder name → `"Unknown Series"`). Same resolver is
    used by the normalize pipeline, the metadata audit, and the
    `/api/files/series-resolution` diagnostic endpoint.
  - `LibraryScanJobHandler`: Scheduled job (`library-scan`) that reconciles
    additions/deletions and re-normalizes files whose stamped
    `SeriesMetadataVersion` is older than the matching cache record's current
    `MetadataVersion`. This is the recommended replacement for the live
    filesystem watcher on volumes where inotify is unreliable (CIFS, some
    Docker bind mounts) and the only mechanism that automatically picks up
    metadata changes (canonical title edits, new aliases, language-preference
    changes) without an explicit rebuild.

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

## How per-file metadata is refreshed

Per-file metadata (the `<Series>`, `<Title>`, `<Issue>` values written to
`ComicInfo.xml` inside each archive) can be created or refreshed in four ways:

| # | Trigger | When it fires | What it writes |
|---|---------|---------------|----------------|
| 1 | **Library Scan** scheduled job (`library-scan`, recommended) | On its configured cadence (default: hourly, disabled) | Adds new files; reconciles deletions; force-normalizes any file whose `SeriesMetadataVersion` stamp is older than the matching cache record's current `MetadataVersion` (a "metadata-changed → retag every affected file" pass that no other path performs automatically). |
| 2 | Live FileSystemWatcher (legacy) | On OS filesystem events; only when `WatcherEnableRename` / `WatcherEnableNormalize` are true | Rename + normalize one file per event. **Does not** re-run when only metadata changes (no FS event is fired by an alias edit or language-preference change). Unreliable on CIFS / some Docker bind mounts where inotify isn't delivered. |
| 3 | On-demand batch jobs from the UI / API | User clicks a button or hits the API; `forceReprocess` overrides the "already processed" gate | Same normalize pipeline as #1 / #2. |
| 4 | Other scheduled audits (`metadata-audit`, `file-naming-audit`) | On their configured cadence | Only **records findings** unless `autoCorrect: true` is set, in which case the candidate files are queued through the normal normalize/rename pipeline. |

**The library scan is the only path that picks up metadata-only changes
automatically.** When a `SeriesMetadataCacheRecord` is mutated (canonical
title edited, alias added/removed, language preference changed, fresh
external match applied), its `MetadataVersion` is bumped. The next library
scan compares each affected file's stamped `SeriesMetadataVersion` against
the new value and force-normalizes any file whose stamp is lower. There is
no need to manually open `ComicInfo.xml` or re-run a full library refresh.

### Series-name resolution priority

When a normalize is performed, the series name written to `<Series>` is
resolved via `ISeriesNameResolver` using a single, documented priority list.
The first step to produce a non-empty title wins:

1. **UserCanonical** — a `SeriesMetadataCacheRecord` reachable from any
   candidate key (file's `<Series>`, folder name, or any existing alias)
   with `IsUserCanonical=true`. Wins unconditionally.
2. **MatchedCache** — a cache record with a successful or manual match,
   passed through `SeriesDisplayTitleResolver` so per-series
   `PreferredLanguage` → global `DefaultPreferredLanguage` → `CanonicalTitle`
   decides which string is emitted.
3. **LocalizedTitleBackref** — file's `<Series>` is already a localized
   variant present in the folder's record's `LocalizedTitles`.
4. **ExternalLookup** — live external provider lookup (no cached match
   yet); global preferred language applies.
5. **ExistingMetadata** — preserve the file's non-empty `<Series>` rather
   than overwriting with the folder name.
6. **FolderName** — the immediate parent directory's normalized name.
7. **UnknownSentinel** — `"Unknown Series"` final fallback.

You can preview the outcome for any tracked file (which step won, which
cache key matched, which language was applied) without modifying anything
by calling `GET /api/files/series-resolution?filePath={path}`.

## Architecture
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

## API Endpoints

For a complete, ready-to-use API collection with all endpoints, see:
- **[Postman Collection](POSTMAN_API_COLLECTION.md)** - Import into Postman for easy API testing and validation

### Files API

- `GET /api/files` - Get all files (optional ?filter=processed|unprocessed|duplicates)
- `GET /api/files/counts` - Get file statistics
- `GET /api/files/metadata?filePath={path}` - Get file metadata
- `PUT /api/files/metadata?filePath={path}` - Update file metadata
- `GET /api/files/series-resolution?filePath={path}` - Diagnostic: explain how the expected `<Series>` value for a file was resolved (which priority step in `ISeriesNameResolver` won, which cache key matched, which language was applied). Useful for investigating "stuck" series metadata.
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
