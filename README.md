
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
  - **Robust batch processing**: Three-layer defense system ensures progress updates are never stuck
    - Real-time SSE updates for instant feedback
    - Automatic reconnection and status sync when connection drops
    - Watchdog timer detects stuck jobs and auto-polls status after 60 seconds of inactivity
- Logs all actions to `ComicMaintainer.log` (with optional separate debug log when `DEBUG_MODE=true`)
- Containerized with Docker for easy deployment
- **Supports custom user and group IDs (PUID/PGID) for proper file permissions**
- **Installable Web App (PWA)**: Install the web interface as a standalone app on your device
  - Works on desktop (Windows, macOS, Linux) and mobile (iOS, Android)
  - App-like experience with dedicated window
  - Add to home screen on mobile devices
  - Offline-ready with cached assets
  - Easy access from your app drawer or desktop

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

### Docker Images

Pre-built Docker images are available on Docker Hub:

- **`iceburn1/comictagger-watcher:latest`** - Built from the `master` branch with the latest features and updates
- **`iceburn1/comictagger-watcher:stable`** - Built from the `stable` branch, providing a tested baseline for production deployments
- **`iceburn1/comictagger-watcher:<version>`** - Specific version tags (e.g., `1.0.23`) built from `master` branch releases

For production environments, using the `stable` tag is recommended for a more reliable experience.

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
- Replace `<host_dir_for_config>` with the path to store persistent configuration data.
- `WATCHED_DIR` **must** be set to the directory to watch (usually `/watched_dir` if using the example above).
- Optionally, mount a host directory to `/duplicates` to persist duplicates.
- **Required**: Mount a host directory to `/Config` to persist:
  - Unified database (`/Config/store/comicmaintainer.db`) containing:
    - File list for fast access
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
- `DB_CACHE_SIZE_MB`: SQLite database cache size in megabytes (default: `64`). Higher values improve read performance but use more RAM. Recommendations:
  - Small libraries (<1000 files): 32MB
  - Medium libraries (1000-5000 files): 64MB (default)
  - Large libraries (>5000 files): 128-256MB
  - See [Performance Tuning Guide](docs/PERFORMANCE_TUNING.md) for detailed recommendations

#### HTTPS/SSL Configuration (Optional)
For direct HTTPS support without a reverse proxy:
- `SSL_CERTFILE`: Path to SSL certificate file (e.g., `/Config/ssl/cert.crt`)
- `SSL_KEYFILE`: Path to SSL private key file (e.g., `/Config/ssl/cert.key`)
- `SSL_CA_CERTS`: Path to CA certificate bundle (optional, for certificate chains)

**Note**: For production deployments, use certificates from a trusted Certificate Authority (e.g., Let's Encrypt). For development/testing, you can generate a self-signed certificate using the included script. See the [HTTPS Configuration](#https-configuration) section below for detailed setup instructions.

#### Reverse Proxy Support (Optional)
- `BASE_PATH`: Path prefix for subdirectory deployments (default: empty). Set to serve the application from a subdirectory, e.g., `/comics` to access at `example.com/comics`. Must start with a forward slash. The application automatically handles reverse proxy headers (`X-Forwarded-*`) for proper URL generation and **automatically enables security headers (HSTS, CSP) when accessed via HTTPS**. See [Reverse Proxy Guide](docs/REVERSE_PROXY.md) for detailed configuration examples (Nginx, Traefik, Apache, Caddy).

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

**Note:** GitHub issue creation creates detailed error reports automatically when errors occur.

## Web Interface
The service includes a web-based interface for managing your comic files:

### Features
- **Optimized for Large Libraries**: Pagination (100 files per page) and fast SQLite database ensure quick loading even with thousands of files
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
    - **Refresh**: Update the file list
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

### Installing as an App (PWA)

The web interface can be installed as a standalone application on your device, providing an app-like experience:

**Desktop Installation (Chrome, Edge, Brave):**
1. Open the web interface in your browser (`http://localhost:5000`)
2. Look for the install button in the browser's address bar (usually a ⊕ or ⬇️ icon)
3. Click "Install" or use the **three-dot menu (⋮)** in the header and select **"📱 Install App"**
4. The app will be installed and can be launched from your applications menu or desktop

**Mobile Installation (iOS Safari):**
1. Open the web interface in Safari
2. Tap the Share button (□ with arrow)
3. Scroll down and tap "Add to Home Screen"
4. Tap "Add" to confirm
5. The app icon will appear on your home screen

**Mobile Installation (Android Chrome):**
1. Open the web interface in Chrome
2. Look for the automatic install prompt that appears at the bottom of the screen
   - Alternatively, tap the three-dot menu (⋮) and select "Install app" or "Add to Home Screen"
   - Or use the custom **"📱 Install App"** button in the web interface settings menu
3. Tap "Install" to confirm
4. The app will be installed and available in your app drawer

**Benefits of Installing:**
- Dedicated app window without browser UI
- Faster access from home screen or app drawer
- Works offline with cached assets
- Full-screen experience on mobile devices
- Feels like a native app

### Performance
- **Fast initial page load**: HTML reduced from 217KB to 43KB by extracting CSS/JS to external cached files (see [PERFORMANCE_IMPROVEMENT_INITIAL_LOAD.md](PERFORMANCE_IMPROVEMENT_INITIAL_LOAD.md))
- **Aggressive browser caching**: Static assets cached for 1 year, reducing repeat visit load time by 80%
- **Optimized search and filtering**: Server-side processing with efficient database queries
- **Search debouncing**: 300ms delay reduces API calls by 87% while typing
- **Real-time updates via Server-Sent Events (SSE)**: 100% event-driven architecture with zero polling. All updates (file processing, watcher status, job progress) are pushed instantly to clients via SSE. Background tasks use event-based timers and file system watchers instead of sleep-based polling
- Files are loaded in pages of 100 to ensure fast initial load times
- Pagination controls allow easy navigation through large libraries
- Search and filters are applied server-side before pagination for efficient handling of large libraries
- **SQLite-based file store**: File list is managed in a SQLite database for atomic operations, better concurrency, and excellent performance (160k+ lookups/sec, <3ms reads for 5000 files)
- See [FILE_LIST_IMPROVEMENTS.md](FILE_LIST_IMPROVEMENTS.md), [docs/EVENT_BROADCASTING_SYSTEM.md](docs/EVENT_BROADCASTING_SYSTEM.md), and [docs/PROGRESS_CALLBACKS.md](docs/PROGRESS_CALLBACKS.md) for detailed performance metrics and architecture

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
- Configured with multiple worker processes for better concurrency
- Concurrent file processing provided by ThreadPoolExecutor (4 threads) within each worker
- 600-second timeout (10 minutes) to accommodate batch processing operations on large libraries
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
   - `ComicMaintainer.log` (regular application log) with automatic rotation
   - `ComicMaintainer_debug.log` (debug log, only when DEBUG_MODE=true) with automatic rotation

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

**Important**: The `/host/config` directory on your host will contain the marker database, configuration, and logs. Make sure it's backed up if you want to preserve your processing history and settings.

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

## HTTPS Configuration

The application supports HTTPS in two ways:

### Option 1: Direct HTTPS Support (Native)
Run the application with HTTPS directly without a reverse proxy:

#### Using Your Own Certificates (Production)

For production use, obtain certificates from a trusted Certificate Authority (e.g., Let's Encrypt):

```sh
docker run -d \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -v /path/to/certs:/certs \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/certs/fullchain.pem \
  -e SSL_KEYFILE=/certs/privkey.pem \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

Access the application at `https://your-domain:5000`

#### Using Self-Signed Certificates (Development/Testing)

For development or testing, generate a self-signed certificate:

**Step 1: Generate the certificate**
```sh
docker run --rm \
  -v /path/to/config:/Config \
  iceburn1/comictagger-watcher:latest \
  /generate_self_signed_cert.sh /Config/ssl 365 localhost
```

**Step 2: Run with HTTPS**
```sh
docker run -d \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/Config/ssl/selfsigned.crt \
  -e SSL_KEYFILE=/Config/ssl/selfsigned.key \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

Access the application at `https://localhost:5000` (you'll need to accept the browser security warning for self-signed certificates)

**Docker Compose Example with Self-Signed Cert:**
```yaml
version: '3.8'
services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    environment:
      - WATCHED_DIR=/watched_dir
      - SSL_CERTFILE=/Config/ssl/selfsigned.crt
      - SSL_KEYFILE=/Config/ssl/selfsigned.key
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    ports:
      - "5000:5000"
```

### Option 2: HTTPS via Reverse Proxy (Recommended for Production)

For production deployments, using a reverse proxy is recommended as it provides:
- Better security and SSL/TLS configuration options
- Built-in certificate management (e.g., automatic Let's Encrypt renewal)
- Additional features like caching, load balancing, and access control

See the [Reverse Proxy Setup Guide](docs/REVERSE_PROXY.md) for detailed configuration examples with Nginx, Traefik, Apache, and Caddy.

**Benefits of Reverse Proxy:**
- ✅ Automatic certificate renewal (Let's Encrypt)
- ✅ Advanced security features (rate limiting, WAF)
- ✅ Better performance (caching, compression)
- ✅ Centralized SSL/TLS management for multiple services
- ✅ Additional authentication layers

**Benefits of Direct HTTPS:**
- ✅ Simpler setup for single-service deployments
- ✅ No additional software required
- ✅ Good for development and testing
- ✅ Works well for private networks

## Logging

The application maintains two separate log files in `/Config/Log/`:

### Regular Application Log
- **File**: `ComicMaintainer.log`
- **Content**: Standard operational messages (INFO, WARNING, ERROR levels)
- All actions and errors are logged here
- View logs directly in the web interface via the "View Logs" option in the settings menu

### Debug Log (Optional)
- **File**: `ComicMaintainer_debug.log`
- **Created**: Only when `DEBUG_MODE=true` is set
- **Content**: All messages including detailed DEBUG level information
- When enabled, debug logs include:
  - Function entry and exit with parameters and return values
  - Detailed operation tracking (file checks, database operations, metadata processing)
  - Variable values and state at key decision points
  - Performance insights for troubleshooting
- Useful for troubleshooting issues, understanding file processing flow, and monitoring system behavior
- Example: `docker run -e DEBUG_MODE=true ...`

### Log Rotation
Both log files use automatic rotation:
- **Rotation trigger**: When file reaches configurable size limit (default: 5MB)
- **Backup count**: Up to 3 backup files are kept (`.log.1`, `.log.2`, `.log.3`)
- **Configuration**:
  - Via the **Settings** menu in the web interface (changes take effect on restart)
  - Via the `LOG_MAX_BYTES` environment variable (in bytes, e.g., `LOG_MAX_BYTES=10485760` for 10MB)

### Automatic Error Reporting
- **GitHub Issue Creation**: Errors can automatically create GitHub issues when configured
- Set `GITHUB_TOKEN` environment variable with a Personal Access Token (needs `repo` scope)
- Each error generates a detailed issue with:
  - Full stack trace and error context
  - Timestamp and error ID for tracking
  - Additional diagnostic information (file paths, operation details)
  - Automatic assignment to configured user (default: `copilot`)
  - Tagged with `bug` and `auto-generated` labels
- Example: `docker run -e GITHUB_TOKEN=ghp_xxx -e GITHUB_ISSUE_ASSIGNEE=username ...`

## GitHub Actions / CI
- The repository includes a GitHub Actions workflow to automatically build and push the Docker image to Docker Hub on every push or pull request to `master`.
- **Automatic Version Bumping**: Every merge to `master` automatically increments the patch version (e.g., 1.0.0 → 1.0.1), updates `CHANGELOG.md`, creates a Git tag, and publishes a GitHub release
- Docker images are tagged with both `latest` and the specific version number (e.g., `1.0.1`)
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

### High-Performance Database
The application uses SQLite for fast, reliable data access:
- **SQLite with WAL mode**: Provides extremely fast reads (<3ms for 5000 files) with excellent concurrency
- **Batch operations**: Marker data (processed/duplicate status) is fetched in batch for efficiency
- **On-demand processing**: Filter/search/sort operations are computed as needed
- **Multi-worker safe**: Designed for concurrent access by multiple Gunicorn workers
- **Real-time updates**: Frontend receives instant updates via Server-Sent Events (SSE)

This design provides excellent performance through efficient database queries without complex caching layers.

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

## Branches

- **`master`** - Main development branch with the latest features and updates
- **`stable`** - Stable branch based on PR #344 (mobile action bar fix), providing a tested baseline for production deployments

See [STABLE_BRANCH_CREATION.md](STABLE_BRANCH_CREATION.md) for details about the stable branch.

## Documentation

- **[API Documentation](docs/API.md)** - Complete REST API reference
- **[HTTPS Setup Guide](docs/HTTPS_SETUP.md)** - Configure HTTPS with native support or reverse proxy
- **[Reverse Proxy Setup Guide](docs/REVERSE_PROXY.md)** - Deploy behind Nginx, Traefik, Apache, or Caddy
- **[Performance Tuning Guide](docs/PERFORMANCE_TUNING.md)** - Optimize performance for your system
- **[Automated Versioning](docs/AUTOMATED_VERSIONING.md)** - How automatic version bumping works
- **[Contributing Guide](CONTRIBUTING.md)** - How to contribute to the project
- **[Debug Logging Guide](DEBUG_LOGGING_GUIDE.md)** - Debug logging and error reporting
- **[Security Policy](SECURITY.md)** - Security guidelines and vulnerability reporting
- **[Changelog](CHANGELOG.md)** - Version history and changes

## Quick Start with Docker Compose

Use the provided `docker-compose.yml` for easy setup:

```bash
# Edit docker-compose.yml to set your paths
# Then start the service
docker-compose up -d

# View logs
docker-compose logs -f

# Stop the service
docker-compose down
```

## License
MIT
