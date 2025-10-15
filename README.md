
# ComicTagger Watcher Service

This service automatically watches a directory for new or changed comic archive files (`.cbz`/`.cbr`), tags them using ComicTagger, and manages duplicates. It is designed to run in a Docker container and is fully automated. **The container supports custom user and group IDs (PUID/PGID) to ensure proper file permissions when working with host-mounted directories.**

## Features
- Watches a directory for file changes (create, modify, move/rename, delete)
- Processes `.cbz` and `.cbr` files only
- Uses ComicTagger to set comic metadata (title, issue, series, etc.)
- Renames files based on customizable filename format templates
- Handles duplicate files: moves them to a duplicate directory, preserving the original folder structure
- **Processing Status Tracking**: Both the watcher and web interface automatically mark files as processed
- **Duplicate File Tracking**: Files that would have the same name after processing are automatically marked as duplicates
- **Web Interface** for managing comic files:
  - One-click button to process all files in the watched directory
  - Process only selected files with the "Process Selected" button
  - Folder selection: click folder checkbox to select all files in a folder
  - View and edit tags for individual files
  - Batch update tags for multiple selected files
  - Configurable filename format with support for metadata placeholders
  - Smart handling to prevent watcher conflicts with web-modified files
  - **Filter by processing status**: View all files, only processed files, only unprocessed files, or only duplicates
  - **Scan for unmarked files**: Quickly identify how many files haven't been processed yet
  - **Visual status indicators**: Each file shows ✅ (processed), ⚠️ (unprocessed), or 🔁 (duplicate) icon
- Logs all actions to `ComicMaintainer.log`
- Containerized with Docker for easy deployment
- **Supports custom user and group IDs (PUID/PGID) for proper file permissions**

## How It Works
1. The watcher service monitors a specified directory for new or changed `.cbz`/`.cbr` files.
2. When a file is detected and stable, it runs `process_file.py` to:
   - **Check if the file is already normalized**: If the metadata (title, series) and filename already match the expected format, the file is immediately marked as processed without making any changes
   - Read and update comic metadata using ComicTagger (if normalization is needed)
   - Rename the file using the configured filename format (e.g., `{series} - Chapter {issue}.cbz` → `Batman - Chapter 0001.cbz`) (if normalization is needed)
   - If a file with the new name already exists, the file is marked as a duplicate and, if `DUPLICATE_DIR` is set, moved to the duplicate directory preserving the original parent folder
3. All actions and errors are logged.

## Usage

### Docker Compose (Recommended)

The easiest way to run ComicMaintainer is using Docker Compose, which automatically sets up PostgreSQL and the application:

1. Create a `.env` file with your configuration:
```env
WATCHED_DIR=/path/to/comics
DUPLICATE_DIR=/path/to/duplicates
CACHE_DIR=/path/to/config
PUID=1000
PGID=1000
POSTGRES_PASSWORD=your_secure_password
```

2. Start the services:
```sh
docker-compose up -d
```

3. Access the web interface at `http://localhost:5000`

### Docker Standalone (Advanced)

If you prefer to run the application without Docker Compose, you need to provide a PostgreSQL database.

**Build the Docker image:**
```sh
docker build -t iceburn1/comictagger-watcher:latest .
```

**Permissions Note:** By default, the container runs as user `nobody` (UID 99) and group `users` (GID 100). You can customize these by setting the `PUID` and `PGID` environment variables to match your host user. This ensures that files created or modified by the container have the correct ownership on your host system.

**Run with external PostgreSQL:**
```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -v <host_dir_for_duplicates>:/duplicates \
  -v <host_dir_for_config>:/config \
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -e CACHE_DIR=/config \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -e DATABASE_URL=postgresql://user:password@host:5432/database \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

- Replace `<host_dir_to_watch>` with the path to your comics folder.
- Replace `<host_dir_for_config>` with the path to store persistent configuration and cache data.
- `WATCHED_DIR` **must** be set to the directory to watch (usually `/watched_dir` if using the example above).
- `DATABASE_URL` **must** be set to a valid PostgreSQL connection string.
- Optionally, mount a host directory to `/duplicates` to persist duplicates.
- **Recommended**: Mount a host directory to `/config` (or your chosen path) and set `CACHE_DIR` to persist:
  - Marker files (processed files, duplicates, web-modified files)
  - Configuration settings (filename format, watcher enabled, log rotation)
  - File list cache for improved performance
- The `-p 5000:5000` flag exposes the web interface on port 5000.
- Set `PUID` and `PGID` to match your host user for proper file permissions (use `id -u` and `id -g` on Linux/macOS).
- Access the web interface at `http://localhost:5000`

### Environment Variables
- `WATCHED_DIR`: **(Required)** Directory to watch for comics. The service will not start if this is not set.
- `DATABASE_URL`: **(Required)** PostgreSQL connection string (format: `postgresql://user:password@host:5432/database`)
- `PROCESS_SCRIPT`: Script to run for processing (default: `/app/process_file.py`)
- `DUPLICATE_DIR`: Directory where duplicates are moved (required for duplicate handling)
- `WEB_PORT`: Port for the web interface (default: `5000`)
- `CACHE_DIR`: **(Recommended)** Directory for persistent configuration and cache data (default: `/app/cache`). Mount a host directory here to persist:
  - Marker files tracking processed/duplicate files
  - Configuration settings (filename format, watcher state, log rotation)
  - File list cache for performance optimization
- `PUID`: User ID to run the service as (default: `99` for user `nobody`)
- `PGID`: Group ID to run the service as (default: `100` for group `users`)
- `GUNICORN_WORKERS`: Number of Gunicorn worker processes (default: `2`). With PostgreSQL backend, multiple workers are fully supported.
- `POSTGRES_PASSWORD`: PostgreSQL password (used in docker-compose.yml)
- `LOG_MAX_BYTES`: Maximum log file size in bytes before rotation (default: `5242880` = 5MB). Can also be configured via the Settings UI.

## Web Interface
The service includes a web-based interface for managing your comic files:

### Features
- **Optimized for Large Libraries**: Pagination (100 files per page) and caching ensure fast loading even with thousands of files
- **Search Functionality**: Find files across all pages by searching file names and paths - pagination automatically adjusts to show only matching results
- **Asynchronous Processing** (New!): Files are processed concurrently in the background for faster completion
- **Process All Files**: One-click button to process all comic files in the watched directory asynchronously
- **Process Selected Files**: Process only the files you've selected with checkboxes, with concurrent execution
- **Folder Selection**: Click the checkbox next to any folder name to select/deselect all files in that folder
- **View/Edit Individual Tags**: Use the Actions dropdown menu on any file to view and edit its metadata tags
- **Batch Update**: Select multiple files and update common tags (series, publisher, year, writer) for all of them at once
- **Filename Format Settings**: Configure how files are renamed when processed using customizable templates
- **Smart Processing**: Files modified through the web interface are marked to prevent the watcher from re-processing them automatically
- **Processing Status Tracking**: Both the watcher and web interface mark files as processed, with visual indicators (✅ for processed, ⚠️ for unprocessed)
- **Duplicate File Tracking**: Files detected as duplicates are automatically marked with a 🔁 icon
- **Filter by Status**: Easily filter files to show all files, only marked (processed), only unmarked (unprocessed), or only duplicates - filters work across all pages
- **Scan for Unmarked Files**: Quickly identify how many files have not been processed yet

### Usage
1. Access the web interface at `http://localhost:5000` (or your configured host/port)
2. The interface will display all `.cbz` and `.cbr` files in your watched directory, organized by folder
3. Navigate through pages using the pagination controls at the bottom if you have many files
4. **Use the search box** to find files across all pages - search works on both file names and paths
5. Use the checkboxes to select files for batch operations:
   - Check individual files one at a time
   - Check the folder checkbox to select/deselect all files in that folder
   - Use "Select All" to select everything
6. Click "Process All Files" to run the standard processing on all files
7. Click "Process Selected" to run processing only on your selected files
8. Use the "Actions" dropdown menu on any file to:
   - **View/Edit**: View and edit tags for the file
   - **Process**: Run full processing (rename + normalize metadata)
   - **Rename**: Rename the file based on metadata
   - **Normalize**: Normalize metadata only
   - **Delete**: Remove the file
10. Select multiple files and click "Update Selected" to batch update common tags
11. Use the **three-dot menu (⋮)** in the top-right header to access:
    - **Settings**: Configure the filename format for renamed files, theme, watcher, and log rotation
    - **View Logs**: View application logs directly in the browser
    - **Toggle Theme**: Switch between light and dark mode
    - **Refresh**: Update the file list (automatically clears cache)
    - **Scan Unmarked**: See a count of processed vs unprocessed files
12. Use the **expand/collapse button (▼/▶)** at the top of the file list to expand or collapse all folders at once
13. Use the filter buttons to view:
    - **All Files**: Show all files in the directory
    - **Unmarked Only**: Show only files that haven't been processed yet
    - **Marked Only**: Show only files that have been processed
    - **Duplicates Only**: Show only files marked as duplicates
    - Search and filters can be combined and work across all pages
14. Look for the status icon next to each filename:
    - ✅ = processed
    - ⚠️ = not processed yet
    - 🔁 = duplicate file

### Performance
- **Optimized search and filtering**: ~90% faster than before with smart caching and debouncing
- **Search debouncing**: 300ms delay reduces API calls by 87% while typing
- **Metadata caching**: File status (processed/duplicate) cached for 5 seconds, reducing disk I/O by 90%
- **Cache warming on startup**: All caches (file list and metadata) are preloaded automatically when the service starts, eliminating "cold start" delays
- Files are loaded in pages of 100 to ensure fast initial load times
- File list is cached on service startup and maintained in memory
- Cache does not expire based on time, providing instant page navigation
- Pagination controls allow easy navigation through large libraries
- Search and filters are applied server-side before pagination for efficient handling of large libraries
- **Incremental cache updates**: Instead of invalidating the entire cache when files change, individual file changes (add, remove, rename) are applied incrementally, significantly improving performance for large libraries
- **Smart cache invalidation**: Cache is only invalidated when the watcher processes files, ensuring the cache stays fresh while maximizing performance
- **Manual cache control**: API endpoints available to manually trigger cache warming (`POST /api/cache/prewarm`) or check cache statistics (`GET /api/cache/stats`)
- See [PERFORMANCE_IMPROVEMENTS.md](PERFORMANCE_IMPROVEMENTS.md) for detailed performance metrics and architecture

### Filename Format Configuration
The filename format can be customized through the web interface Settings modal. The format uses placeholders that are replaced with actual metadata values:

**Available Placeholders:**
- `{series}` - Series name
- `{issue}` - Issue number (padded to 4 digits, e.g., 0001, or 0071.4 for decimals)
- `{issue_no_pad}` - Issue number (no padding, e.g., 1, or 71.4 for decimals)
- `{title}` - Issue title
- `{volume}` - Volume number
- `{year}` - Publication year
- `{publisher}` - Publisher name

**Examples:**
- `{series} - Chapter {issue}.cbz` → `Batman - Chapter 0005.cbz` (default)
- `{series} - Chapter {issue}.cbz` → `Manga - Chapter 0071.4.cbz` (decimal chapters)
- `{series} v{volume} #{issue_no_pad}.cbz` → `Batman v1 #5.cbz`
- `{series} ({year}) - {title}.cbz` → `Batman (2023) - Dark Knight.cbz`
- `{series} #{issue} - {title}.cbz` → `Batman #0005 - Dark Knight.cbz`

**Note:** Decimal chapter numbers (e.g., 71.4, 71.11) are preserved without trailing zeros.

The filename format setting is saved in `config.json` (located in `CACHE_DIR`) and applies to both web interface processing and watcher service processing. **Mount `CACHE_DIR` as a volume to persist this configuration across container restarts.**

## API Endpoints

The web interface exposes several REST API endpoints:

### Asynchronous Processing (New!)
The service now supports asynchronous file processing, allowing multiple files to be processed concurrently in the background:

- **POST** `/api/jobs/process-all` - Start async processing of all files
  - Returns: `{"job_id": "...", "total_items": 123}`
  - Files are processed concurrently using a thread pool (default: 4 workers)
  
- **POST** `/api/jobs/process-selected` - Start async processing of selected files
  - Body: `{"files": ["path/to/file1.cbz", "path/to/file2.cbz"]}`
  - Returns: `{"job_id": "...", "total_items": 2}`
  
- **GET** `/api/jobs/<job_id>` - Get status and results of a job
  - Returns: Job status including progress, results, and any errors
  
- **GET** `/api/jobs` - List all jobs
  - Returns: `{"jobs": [...]}`
  
- **DELETE** `/api/jobs/<job_id>` - Delete a job from history
  
- **POST** `/api/jobs/<job_id>/cancel` - Cancel a running job

**Benefits of Async Processing:**
- ✅ **Concurrent execution**: Multiple files processed simultaneously
- ✅ **Non-blocking**: Web interface remains responsive during processing
- ✅ **Progress tracking**: Real-time status updates via polling
- ✅ **Scalable**: Handles large libraries efficiently

**Note:** The original streaming endpoints (`/api/process-all?stream=true`, etc.) remain available for backward compatibility, but the async endpoints are now used by default in the web interface.

### Version Information
- **GET** `/api/version` - Returns the current version of the application
  ```json
  {
    "version": "1.0.0"
  }
  ```
  
The version is displayed in the web interface header for easy identification of the running instance.

## Smart Processing
The service intelligently detects files that are already properly formatted to avoid unnecessary processing:

### Already Normalized Detection
When processing a file, the service first checks if:
1. **Title metadata** matches the expected format (`Chapter {issue_number}`)
2. **Series metadata** matches the folder name (with appropriate character conversions)
3. **Filename** matches the configured filename format template

If all three conditions are met, the file is marked as processed **without making any changes**. This:
- ✅ Prevents unnecessary file I/O operations
- ✅ Avoids redundant metadata updates
- ✅ Speeds up processing for large libraries
- ✅ Properly tracks already-correct files in the processing status

### When Processing is Skipped
Files are marked as processed without changes when:
- The metadata and filename are already in the correct format
- The file has been manually curated and matches the expected format
- A file was previously processed and hasn't been modified

### When Processing Occurs
Files are processed and updated when:
- Metadata (title/series) doesn't match the expected format
- The filename doesn't match the configured format
- The file is new or has been modified since last processing

## ComicTagger Integration
- ComicTagger is installed in the container from the **develop branch** and used via its Python API.
- The service supports both `.cbz` (zip) and `.cbr` (rar) files.
- **Note**: The code is compatible with both master and develop branch APIs of ComicTagger, automatically detecting which version is in use.

## Production Server
- The web interface runs on **Gunicorn**, a production-ready WSGI server for Python web applications
- Job state stored in **PostgreSQL** for persistence and scalability
- Configured with 2 worker processes by default (configurable via `GUNICORN_WORKERS` environment variable)
- Multiple workers are fully supported thanks to PostgreSQL shared job state
- Concurrent file processing provided by ThreadPoolExecutor (4 threads per worker)
- 600-second timeout (10 minutes) to accommodate batch processing operations on large libraries
- **Cache coordination**: File-based locking prevents cache rebuild conflicts
- **Non-blocking cache rebuilds**: Serves stale cache instead of waiting when cache is being rebuilt
- No development server warnings - ready for production deployment

## Data Persistence

The application stores all persistent data in `CACHE_DIR` (default: `/app/cache`). **To preserve your data across container restarts, mount this directory as a volume.**

### What is Persisted

When you mount `CACHE_DIR`, the following data is preserved:

1. **Marker Files** - Track which files have been processed, duplicates, and web modifications
   - Located in `CACHE_DIR/markers/`
   - `processed_files.json`, `duplicate_files.json`, `web_modified_files.json`

2. **Configuration Settings** - Saved via the web interface Settings menu
   - Located at `CACHE_DIR/config.json`
   - Includes: filename format template, watcher state, log rotation settings

3. **Cache Files** - Performance optimization data
   - File list cache, cache update markers

### Example with Persistence

```sh
docker run -d \
  -v /host/comics:/watched_dir \
  -v /host/config:/config \
  -e WATCHED_DIR=/watched_dir \
  -e CACHE_DIR=/config \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Important**: The `/host/config` directory on your host will contain all marker files, configuration, and cache. Make sure it's backed up if you want to preserve your processing history and settings.

### Migrating from Previous Versions

If upgrading from a version where `config.json` was stored in `/app`:
- Your old configuration will not be automatically migrated
- The application will use default settings on first run
- To preserve settings: manually copy `/app/config.json` from the old container to `$CACHE_DIR/config.json` in the new setup

## Logging
- All actions and errors are logged to `ComicMaintainer.log` (located in `/app/ComicMaintainer.log` within the container).
- **Log Rotation**: Log files are automatically rotated when they reach a configurable size limit (default: 5MB)
  - Up to 3 backup files are kept (`ComicMaintainer.log.1`, `.2`, `.3`)
  - The rotation limit can be configured:
    - Via the **Settings** menu in the web interface (changes take effect on restart)
    - Via the `LOG_MAX_BYTES` environment variable (in bytes, e.g., `LOG_MAX_BYTES=10485760` for 10MB)
  - View logs directly in the web interface via the "View Logs" option in the settings menu

## GitHub Actions / CI
- The repository includes a GitHub Actions workflow to automatically build and push the Docker image to Docker Hub on every push or pull request to `master`.

## Requirements
- Docker
- (Optional) Docker Hub account for pushing images

## License
MIT
