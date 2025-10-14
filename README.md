
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
   - Read and update comic metadata using ComicTagger
   - Rename the file using the configured filename format (e.g., `{series} - Chapter {issue}.cbz` → `Batman - Chapter 0001.cbz`)
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
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

- Replace `<host_dir_to_watch>` with the path to your comics folder.
- `WATCHED_DIR` **must** be set to the directory to watch (usually `/watched_dir` if using the example above).
- Optionally, mount a host directory to `/duplicates` to persist duplicates.
- The `-p 5000:5000` flag exposes the web interface on port 5000.
- Set `PUID` and `PGID` to match your host user for proper file permissions (use `id -u` and `id -g` on Linux/macOS).
- Access the web interface at `http://localhost:5000`

### Environment Variables
- `WATCHED_DIR`: **(Required)** Directory to watch for comics. The service will not start if this is not set.
- `PROCESS_SCRIPT`: Script to run for processing (default: `/process_file.py`)
- `DUPLICATE_DIR`: Directory where duplicates are moved (required for duplicate handling)
- `WEB_PORT`: Port for the web interface (default: `5000`)
- `PUID`: User ID to run the service as (default: `99` for user `nobody`)
- `PGID`: Group ID to run the service as (default: `100` for group `users`)

## Web Interface
The service includes a web-based interface for managing your comic files:

### Features
- **Optimized for Large Libraries**: Pagination (100 files per page) and caching ensure fast loading even with thousands of files
- **Search Functionality**: Find files across all pages by searching file names and paths - pagination automatically adjusts to show only matching results
- **Process All Files**: One-click button to process all comic files in the watched directory
- **Process Selected Files**: Process only the files you've selected with checkboxes
- **Folder Selection**: Click the checkbox next to any folder name to select/deselect all files in that folder
- **View/Edit Individual Tags**: Click on any file to view and edit its metadata tags
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
8. Click "View/Edit" on any file to see and modify its tags
9. Use the "Actions" dropdown menu on any file to:
   - **Process**: Run full processing (rename + normalize metadata)
   - **Rename**: Rename the file based on metadata
   - **Normalize**: Normalize metadata only
   - **Delete**: Remove the file
10. Select multiple files and click "Update Selected" to batch update common tags
11. Use the **three-dot menu (⋮)** in the top-right header to access:
    - **Settings**: Configure the filename format for renamed files and theme
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

The filename format setting is saved in `config.json` and applies to both web interface processing and watcher service processing.

## API Endpoints

The web interface exposes several REST API endpoints:

### Version Information
- **GET** `/api/version` - Returns the current version of the application
  ```json
  {
    "version": "1.0.0"
  }
  ```
  
The version is displayed in the web interface header for easy identification of the running instance.

## ComicTagger Integration
- ComicTagger is installed in the container from the **develop branch** and used via its Python API.
- The service supports both `.cbz` (zip) and `.cbr` (rar) files.
- **Note**: The code is compatible with both master and develop branch APIs of ComicTagger, automatically detecting which version is in use.

## Production Server
- The web interface runs on **Gunicorn**, a production-ready WSGI server for Python web applications
- Configured with 4 worker processes for handling multiple concurrent requests
- 120-second timeout to accommodate large file processing operations
- **Worker coordination**: File-based locking ensures only one worker rebuilds caches at a time, preventing worker blocking
- **Non-blocking cache rebuilds**: Workers serve stale cache instead of waiting when another worker is rebuilding
- No development server warnings - ready for production deployment

## Logging
- All actions and errors are logged to `ComicMaintainer.log` (in the container working directory).

## GitHub Actions / CI
- The repository includes a GitHub Actions workflow to automatically build and push the Docker image to Docker Hub on every push or pull request to `master`.

## Requirements
- Docker
- (Optional) Docker Hub account for pushing images

## License
MIT
