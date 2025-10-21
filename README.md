
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
  - **Server-side preferences**: Theme and pagination settings persist across browsers and devices
  - **Job resumption**: Batch processing jobs automatically resume after page refresh or browser restart
- Logs all actions to `ComicMaintainer.log`
- Containerized with Docker for easy deployment
- **Supports custom user and group IDs (PUID/PGID) for proper file permissions**

## How It Works
1. The watcher service monitors a specified directory for new or changed `.cbz`/`.cbr` files.
2. When a file is detected and stable, it runs `process_file.py` to:
   - **Check if the file is already normalized**: If the metadata (title, series) and filename already match the expected format, the file is immediately marked as processed without making any changes
   - Read and update comic metadata using ComicTagger (if normalization is needed)
   - Rename the file using the configured filename format (e.g., `{series} - Chapter {issue}` → `Batman - Chapter 0001.cbz` or `.cbr` depending on original format) (if normalization is needed)
   - If a file with the new name already exists, the file is marked as a duplicate and, if `DUPLICATE_DIR` is set, moved to the duplicate directory preserving the original parent folder
3. All actions and errors are logged.

## Usage

### Build the Docker image
```sh
docker build -t iceburn1/comictagger-watcher:latest .
```

**Permissions Note:** By default, the container runs as user `nobody` (UID 99) and group `users` (GID 100). You can customize these by setting the `PUID` and `PGID` environment variables to match your host user. This ensures that files created or modified by the container have the correct ownership on your host system.


### Run the container

**Basic usage:**
```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -v <host_dir_for_duplicates>:/duplicates \
  -v <host_dir_for_config>:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**With custom user/group (recommended for host-mounted directories):**
```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -v <host_dir_for_duplicates>:/duplicates \
  -v <host_dir_for_config>:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

- Replace `<host_dir_to_watch>` with the path to your comics folder.
- Replace `<host_dir_for_config>` with the path to store persistent configuration and cache data.
- `WATCHED_DIR` **must** be set to the directory to watch (usually `/watched_dir` if using the example above).
- Optionally, mount a host directory to `/duplicates` to persist duplicates.
- **Required**: Mount a host directory to `/Config` to persist:
  - Unified database (`/Config/store/comicmaintainer.db`) containing:
    - File list cache for improved performance
    - Processing markers (processed files, duplicates, web-modified files)
    - Metadata (last sync timestamp, configuration)
  - Configuration settings (filename format, watcher enabled, log rotation)
  - User preferences (theme, pagination settings) stored in SQLite
  - Active job tracking for batch processing resumption
  - Log files (stored in `/Config/Log/`)
- The `-p 5000:5000` flag exposes the web interface on port 5000.
- Set `PUID` and `PGID` to match your host user for proper file permissions (use `id -u` and `id -g` on Linux/macOS).
- Access the web interface at `http://localhost:5000`

### Environment Variables
- `WATCHED_DIR`: **(Required)** Directory to watch for comics. The service will not start if this is not set.
- `PROCESS_SCRIPT`: Script to run for processing (default: `/app/process_file.py`)
- `DUPLICATE_DIR`: Directory where duplicates are moved (required for duplicate handling)
- `WEB_PORT`: Port for the web interface (default: `5000`)
- `GUNICORN_WORKERS`: Number of Gunicorn worker processes (default: `2`). Job state is shared across workers via SQLite.
- `PUID`: User ID to run the service as (default: `99` for user `nobody`)
- `PGID`: Group ID to run the service as (default: `100` for group `users`)
- `LOG_MAX_BYTES`: Maximum log file size in bytes before rotation (default: `5242880` = 5MB). Can also be configured via the Settings UI.
- `MAX_WORKERS`: Number of concurrent worker threads for file processing (default: `4`). Recommendations:
  - For CPU-bound systems: 2-4 workers
  - For systems with fast storage: 4-8 workers
  - For systems with slow storage: 2-4 workers

#### Debug Logging and Error Reporting (Optional)
- `DEBUG_MODE`: Enable extensive debug logging throughout the application (default: `false`). Set to `true` to enable detailed debug output including function entry/exit, parameter values, and operation details.
- `GITHUB_TOKEN`: GitHub Personal Access Token for automatic issue creation on errors (optional). When set, errors will automatically create GitHub issues with full context and stack traces.
- `GITHUB_REPOSITORY`: GitHub repository in `owner/repo` format (default: `mleenorris/ComicMaintainer`). Used for issue creation.
- `GITHUB_ISSUE_ASSIGNEE`: Username to assign auto-generated issues to (default: `copilot`). Issues are also tagged with `bug` and `auto-generated` labels.

**Example with debug logging and GitHub integration:**
```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -v <host_dir_for_config>:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DEBUG_MODE=true \
  -e GITHUB_TOKEN=ghp_your_token_here \
  -e GITHUB_ISSUE_ASSIGNEE=your_username \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Note:** GitHub issue creation is rate-limited internally to prevent duplicate issues for the same error. The error handling system caches up to 100 unique errors to avoid creating duplicate issues.

## Web Interface
The service includes a web-based interface for managing your comic files:

### Features
- **Optimized for Large Libraries**: Pagination (100 files per page) and caching ensure fast loading even with thousands of files
- **Search Functionality**: Find files across all pages by searching file names and paths - pagination automatically adjusts to show only matching results
- **Asynchronous Processing**: Files are processed concurrently in the background for faster completion
- **Real-time Progress Updates**: Job progress is pushed via Server-Sent Events (SSE) for instant feedback without polling
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
- **Database-driven architecture**: SQLite database with WAL mode provides excellent performance (< 10ms queries for 5000 files)
- **No caching overhead**: Removed in-memory caching layers for simpler, more maintainable code
- **Fast database queries**: File list queries take < 2ms for 1000 files, < 10ms for 5000 files
- **Efficient filtering and sorting**: In-memory filtering and sorting completes in < 1ms for 5000 files
- **Search debouncing**: 300ms delay reduces API calls by 87% while typing
- **Real-time updates via Server-Sent Events (SSE)**: 100% event-driven architecture with zero polling. All updates (file processing, watcher status, job progress) are pushed instantly to clients via SSE. Background tasks use event-based timers and file system watchers instead of sleep-based polling
- Files are loaded in pages of 100 to ensure fast initial load times
- Pagination controls allow easy navigation through large libraries
- Search and filters are applied server-side before pagination for efficient handling of large libraries
- **SQLite-based file store**: File list is managed in a SQLite database for atomic operations, better concurrency, and excellent performance (160k+ lookups/sec). Adding and removing files is seamless and instant.
- **Single source of truth**: All data is loaded directly from database, eliminating cache consistency issues
- **Manual database statistics**: API endpoint available to check database statistics (`GET /api/cache/stats`)
- See [FILE_LIST_IMPROVEMENTS.md](FILE_LIST_IMPROVEMENTS.md), [PERFORMANCE_IMPROVEMENTS.md](PERFORMANCE_IMPROVEMENTS.md), [docs/EVENT_BROADCASTING_SYSTEM.md](docs/EVENT_BROADCASTING_SYSTEM.md), and [docs/PROGRESS_CALLBACKS.md](docs/PROGRESS_CALLBACKS.md) for detailed performance metrics and architecture

### Filename Format Configuration
The filename format can be customized through the web interface Settings modal. The format uses placeholders that are replaced with actual metadata values:

**Available Placeholders:**
- `{series}` - Series name
- `{issue}` - Issue number (padded based on settings, default 4 digits, e.g., 0001, or 0071.4 for decimals)
- `{issue_no_pad}` - Issue number (no padding, e.g., 1, or 71.4 for decimals)
- `{title}` - Issue title
- `{volume}` - Volume number
- `{year}` - Publication year
- `{publisher}` - Publisher name

**Examples:**
- `{series} - Chapter {issue}` → `Batman - Chapter 0005.cbz` or `.cbr` (default, with 4-digit padding)
- `{series} - Chapter {issue}` → `Manga - Chapter 0071.4.cbz` or `.cbr` (decimal chapters)
- `{series} v{volume} #{issue_no_pad}` → `Batman v1 #5.cbz` or `.cbr`
- `{series} ({year}) - {title}` → `Batman (2023) - Dark Knight.cbz` or `.cbr`
- `{series} #{issue} - {title}` → `Batman #0005 - Dark Knight.cbz` or `.cbr`

**Issue Number Padding:**
The padding for the `{issue}` placeholder is configurable in Settings (default: 4 digits). This allows you to control how issue numbers are formatted:
- **Padding 4** (default): Issue 5 → `0005`, Issue 71.4 → `0071.4`
- **Padding 3**: Issue 5 → `005`, Issue 71.4 → `071.4`
- **Padding 0**: Issue 5 → `5`, Issue 71.4 → `71.4` (no padding)
- **Padding 6**: Issue 5 → `000005`, Issue 71.4 → `000071.4`

**Note:** 
- The `{issue_no_pad}` placeholder is always unpadded regardless of the padding setting.
- Decimal chapter numbers (e.g., 71.4, 71.11) are preserved without trailing zeros.
- The original file extension (`.cbz` or `.cbr`) is automatically preserved when renaming files.

The filename format and padding settings are saved in `config.json` (located in `/Config`) and apply to both web interface processing and watcher service processing. **Mount `/Config` as a volume to persist this configuration across container restarts.**

## API Endpoints

The web interface exposes several REST API endpoints:

### Asynchronous Processing (New!)
The service now supports asynchronous file processing with persistent job storage in SQLite:

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
- ✅ **Progress tracking**: Real-time status updates via SSE (Server-Sent Events) with minimal fallback polling
- ✅ **Persistent storage**: Jobs survive server restarts (stored in SQLite)
- ✅ **Multi-worker support**: Multiple Gunicorn workers can share job state
- ✅ **Scalable**: Handles large libraries efficiently with horizontal scaling
- ✅ **Page refresh protection**: Warning dialog prevents accidental interruption; jobs auto-resume on return

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
- Configured with 1 worker process to ensure job state consistency (jobs stored in-memory)
- Concurrent file processing provided by ThreadPoolExecutor (4 threads) within the worker
- 600-second timeout (10 minutes) to accommodate batch processing operations on large libraries
- **Cache coordination**: File-based locking prevents cache rebuild conflicts
- **Non-blocking cache rebuilds**: Serves stale cache instead of waiting when cache is being rebuilt
- No development server warnings - ready for production deployment

## Data Persistence

The application stores all persistent data in `/Config`. **To preserve your data across container restarts, mount this directory as a volume.**

### What is Persisted

When you mount `/Config`, the following data is preserved:

1. **File Store Database** - Tracks all comic files in the watched directory
   - Located in `/Config/file_store/files.db` (SQLite database)
   - Provides atomic operations for file list management
   - Automatically syncs with filesystem on startup

2. **Marker Database** - Track which files have been processed, duplicates, and web modifications
   - Located in `/Config/markers/markers.db` (SQLite database)
   - Automatically migrates from legacy JSON files on first startup
   - More efficient and reliable than JSON files

3. **Configuration Settings** - Saved via the web interface Settings menu
   - Located at `/Config/config.json`
   - Includes: filename format template, watcher state, log rotation settings

4. **Log Files** - Application logs
   - Located in `/Config/Log/`
   - `ComicMaintainer.log` with automatic rotation

5. **Cache Files** - Performance optimization data
   - In-memory caches backed by SQLite databases for reliability

### Example with Persistence

```sh
docker run -d \
  -v /host/comics:/watched_dir \
  -v /host/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Important**: The `/host/config` directory on your host will contain the marker database, configuration, logs, and cache. Make sure it's backed up if you want to preserve your processing history and settings.

### Migrating from Previous Versions

**Marker Storage Migration (JSON to SQLite):**
- Marker data has been migrated from JSON files to a SQLite database for better performance and reliability
- On first startup, existing JSON marker files will be automatically imported into the database
- Original JSON files are preserved as backups (e.g., `processed_files.json.migrated.TIMESTAMP`)
- No action required - migration happens automatically

**Previous Versions:**
If upgrading from a version where configuration and logs were stored in `/app` or used the `CACHE_DIR` environment variable:
- Your old configuration will not be automatically migrated
- The application will use default settings on first run
- To preserve settings: manually copy configuration files from the old container to `/Config` in the new setup
- **Note**: The `CACHE_DIR` environment variable has been removed. All persistent data is now stored in `/Config` by default.

## Logging
- All actions and errors are logged to `ComicMaintainer.log` (located in `/Config/Log/ComicMaintainer.log`).
- **Log Rotation**: Log files are automatically rotated when they reach a configurable size limit (default: 5MB)
  - Up to 3 backup files are kept (`ComicMaintainer.log.1`, `.2`, `.3`)
  - The rotation limit can be configured:
    - Via the **Settings** menu in the web interface (changes take effect on restart)
    - Via the `LOG_MAX_BYTES` environment variable (in bytes, e.g., `LOG_MAX_BYTES=10485760` for 10MB)
  - View logs directly in the web interface via the "View Logs" option in the settings menu

### Debug Logging
- **Debug Mode**: Enable extensive debug logging by setting `DEBUG_MODE=true` environment variable
- When enabled, debug logs include:
  - Function entry and exit with parameters and return values
  - Detailed operation tracking (file checks, cache operations, metadata processing)
  - Variable values and state at key decision points
  - Performance insights for troubleshooting
- Debug logs are written to the same log file as regular logs but with DEBUG level
- Useful for troubleshooting issues, understanding file processing flow, and monitoring system behavior
- Example: `docker run -e DEBUG_MODE=true ...`

### Automatic Error Reporting
- **GitHub Issue Creation**: Errors can automatically create GitHub issues when configured
- Set `GITHUB_TOKEN` environment variable with a Personal Access Token (needs `repo` scope)
- Each error generates a detailed issue with:
  - Full stack trace and error context
  - Timestamp and error ID for tracking
  - Additional diagnostic information (file paths, operation details)
  - Automatic assignment to configured user (default: `copilot`)
  - Tagged with `bug` and `auto-generated` labels
- Duplicate detection prevents creating multiple issues for the same error
- Rate limiting built-in to avoid API abuse
- Example: `docker run -e GITHUB_TOKEN=ghp_xxx -e GITHUB_ISSUE_ASSIGNEE=username ...`

## GitHub Actions / CI
- The repository includes a GitHub Actions workflow to automatically build and push the Docker image to Docker Hub on every push or pull request to `master`.
- Automated security scanning runs on every push, pull request, and weekly schedule

## Security

This project implements automated security vulnerability scanning to ensure code and dependency safety.

### Security Scanning

The project uses multiple security scanning tools:

1. **Bandit** - Scans Python code for common security issues
2. **pip-audit** - Checks dependencies for known vulnerabilities
3. **Trivy** - Scans Docker images for OS and library vulnerabilities

Security scans run automatically:
- On every push and pull request
- Weekly scheduled scans (Mondays at 9:00 AM UTC)
- Manual trigger available via GitHub Actions

### Running Security Scans Locally

```bash
# Install security tools
pip install -r requirements-dev.txt

# Scan code for security issues
bandit -r src/

# Check dependencies for vulnerabilities
pip-audit -r requirements.txt

# Scan Docker image
docker build -t comictagger-watcher:scan .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image comictagger-watcher:scan
```

For more information, see [SECURITY.md](SECURITY.md).

### Security Best Practices

- Use custom PUID/PGID for proper file permissions
- Expose only necessary ports
- Use reverse proxy with HTTPS for external access
- Keep the Docker image updated regularly
- Review [SECURITY.md](SECURITY.md) for detailed security guidelines

## Performance & Reliability

### High-Performance Caching
The application uses an advanced caching system that:
- **Non-blocking cache operations**: Workers never block waiting for cache rebuild
- **Async background rebuilding**: Cache updates happen in background threads
- **Instant response times**: All API requests respond in <100ms even during cache rebuild
- **Multi-worker safe**: Designed for concurrent access by multiple Gunicorn workers
- **Automatic recovery**: Frontend receives real-time cache updates via SSE events

See [docs/WORKER_TIMEOUT_FIX.md](docs/WORKER_TIMEOUT_FIX.md) for technical details on the non-blocking cache architecture.

### Event-Driven Architecture
The application is 100% event-driven with zero polling:
- **Frontend**: All updates received via Server-Sent Events (SSE) - no polling loops
- **Backend**: Uses file system watchers (watchdog) and event-based timers instead of sleep polling
- **Watcher**: Main loop uses Event.wait() instead of sleep(1) polling
- **Cleanup tasks**: Scheduled with threading.Timer instead of infinite sleep loops
- **Job tracking**: Real-time progress updates via SSE, no fallback polling needed
- **Resource efficient**: Eliminates unnecessary CPU usage from polling loops

## Requirements
- Docker
- (Optional) Docker Hub account for pushing images

## License
MIT
