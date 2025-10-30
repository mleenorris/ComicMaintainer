# QuickStart Guide - .NET Version

Get up and running with ComicMaintainer .NET in 5 minutes!

## Prerequisites

- .NET 9.0 SDK (or Docker)
- Git

## Option 1: Docker (Easiest)

```bash
# Clone the repository
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
git checkout csharp

# Start the application
docker-compose -f docker-compose.dotnet.yml up -d

# Access the web interface
open http://localhost:5000
```

That's it! The application is now running and watching the `./test_comics` directory.

## Option 2: Local Development

```bash
# Clone the repository
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
git checkout csharp

# Build the solution
dotnet build ComicMaintainer.sln

# Run the application
cd src/ComicMaintainer.WebApi
dotnet run

# Access the web interface
open http://localhost:5000
```

## Configuration

Set environment variables before running:

```bash
export WATCHED_DIR=/path/to/your/comics
export DUPLICATE_DIR=/path/to/duplicates
export CONFIG_DIR=/path/to/config

# Run
dotnet run
```

Or create an `appsettings.json` file:

```json
{
  "AppSettings": {
    "WatchedDirectory": "/path/to/your/comics",
    "DuplicateDirectory": "/path/to/duplicates",
    "ConfigDirectory": "/path/to/config"
  }
}
```

## Using the Web Interface

1. **Browse Files**: See all comic files in your watched directory
2. **Filter**: Show only processed, unprocessed, or duplicate files
3. **Process**: Click "Process All" or select specific files
4. **View Metadata**: Click on any file to see/edit metadata
5. **Settings**: Configure filename format and other options

## API Examples

### Get all files
```bash
curl http://localhost:5000/api/files
```

### Get file counts
```bash
curl http://localhost:5000/api/files/counts
```

### Process files
```bash
curl -X POST http://localhost:5000/api/files/process-batch \
  -H "Content-Type: application/json" \
  -d '["file1.cbz", "file2.cbz"]'
```

### Check watcher status
```bash
curl http://localhost:5000/api/watcher/status
```

## Testing the API

1. Open Swagger UI at `http://localhost:5000/swagger`
2. Try out the endpoints interactively
3. See request/response examples

## Troubleshooting

### Port already in use
```bash
# Use a different port
dotnet run --urls "http://localhost:5001"
```

### Permission errors with Docker
```bash
# Set PUID/PGID to match your user
docker run -e PUID=$(id -u) -e PGID=$(id -g) ...
```

### Files not being detected
1. Check the watcher status: `GET /api/watcher/status`
2. Verify the directory path is correct
3. Check file permissions

## Next Steps

- Read [README.DOTNET.md](README.DOTNET.md) for full documentation
- Check [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md) if migrating from Python
- See [MAUI_ANDROID_SETUP.md](MAUI_ANDROID_SETUP.md) for mobile app setup
- Review [CSHARP_CONVERSION_SUMMARY.md](CSHARP_CONVERSION_SUMMARY.md) for technical details

## Getting Help

- 📖 [Documentation](README.DOTNET.md)
- 🐛 [Report Issues](https://github.com/mleenorris/ComicMaintainer/issues)
- 💬 [Discussions](https://github.com/mleenorris/ComicMaintainer/discussions)

Enjoy managing your comics with ComicMaintainer .NET! 🎉
