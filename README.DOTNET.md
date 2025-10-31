# ComicMaintainer - .NET Version

This is the C# and .NET version of ComicMaintainer, converted from the original Python implementation. This version provides the same functionality as the Python version but is built on the .NET platform, making it deployable as a web application and as a mobile app (Android/iOS) using .NET MAUI.

## Overview

ComicMaintainer is a service that automatically watches a directory for new or changed comic archive files (`.cbz`/`.cbr`), processes them, and provides a web interface for managing your comic collection.

## Features

- **File Watching**: Automatically monitors directories for comic file changes
- **Comic Processing**: Processes `.cbz` and `.cbr` files
- **Web Interface**: Full-featured web UI for managing comics
- **Batch Processing**: Process multiple files at once
- **Metadata Management**: View and edit comic metadata
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
- ComicInfo.xml schema implementation
- Interfaces for services
- Service implementations:
  - `FileStoreService`: Manages file tracking
  - `FileWatcherService`: Monitors directory changes
  - `ComicProcessorService`: Processes comic files with full metadata support
  - `ComicArchive`: Comic archive handler (CBZ/CBR) - C# equivalent of Python's ComicTagger
  - `ComicFileProcessor`: Filename formatting and parsing utilities

### ComicMaintainer.WebApi
ASP.NET Core Web API application:
- RESTful API endpoints
- Static file serving for the web UI
- Hosted services for background tasks
- Controllers:
  - `FilesController`: File management endpoints
  - `JobsController`: Batch job status endpoints
  - `WatcherController`: File watcher control endpoints

### ComicMaintainer.MauiApp (Future)
.NET MAUI application for Android/iOS:
- Cross-platform mobile app
- Native UI for mobile devices
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

## API Endpoints

### Files API

- `GET /api/files` - Get all files (optional ?filter=processed|unprocessed|duplicates)
- `GET /api/files/counts` - Get file statistics
- `GET /api/files/{filePath}/metadata` - Get file metadata
- `PUT /api/files/{filePath}/metadata` - Update file metadata
- `POST /api/files/{filePath}/process` - Process a single file
- `POST /api/files/process-batch` - Process multiple files
- `POST /api/files/{filePath}/mark-processed` - Mark file as processed

### Jobs API

- `GET /api/jobs/{jobId}` - Get batch job status

### Watcher API

- `GET /api/watcher/status` - Get watcher status
- `POST /api/watcher/enable` - Enable/disable watcher

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

1. **Comic Processing**: ✅ **IMPLEMENTED** - Full ComicTagger functionality converted to C# using SharpCompress for archive handling. See [COMICTAGGER_CSHARP_CONVERSION.md](COMICTAGGER_CSHARP_CONVERSION.md) for details.
2. **File Storage**: Currently uses in-memory storage; can be extended to use Entity Framework Core with SQLite/PostgreSQL for persistence
3. **Event Broadcasting**: Would need to implement SignalR for real-time updates (equivalent to Python's Server-Sent Events)

### Dependencies

The .NET version uses the following NuGet packages:

**ComicMaintainer.Core:**
- SharpCompress 0.41.0 - Comic archive handling (CBZ/CBR)
- Microsoft.Extensions.Logging.Abstractions 9.0.10
- Microsoft.Extensions.Options 9.0.10

**ComicMaintainer.WebApi:**
- ASP.NET Core 9.0 (included in .NET SDK)
- Swashbuckle.AspNetCore (Swagger/OpenAPI)

For a detailed comparison of the ComicTagger conversion from Python to C#, see [COMICTAGGER_CSHARP_CONVERSION.md](COMICTAGGER_CSHARP_CONVERSION.md).

## Future Enhancements

### Planned Features

1. **Full Comic Processing**: ✅ **COMPLETED**
   - ✅ Integration with SharpCompress for archive manipulation
   - ✅ ComicInfo.xml metadata parsing and writing
   - ✅ File renaming based on templates
   - ✅ Chapter number parsing (including decimal chapters)
   - ✅ Normalization checking (skip already-processed files)
   - See [COMICTAGGER_CSHARP_CONVERSION.md](COMICTAGGER_CSHARP_CONVERSION.md)

2. **.NET MAUI Mobile App**:
   - Native Android app
   - iOS app support
   - Shared UI components with web version

3. **Enhanced Storage**:
   - Entity Framework Core integration
   - SQLite database for persistence
   - Migration from Python's file-based storage

4. **Real-Time Updates**:
   - SignalR for WebSocket communication
   - Live job progress updates
   - Real-time file status changes

5. **Authentication & Authorization**:
   - User management
   - API key authentication
   - Role-based access control

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
