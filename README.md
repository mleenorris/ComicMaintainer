
# ComicTagger Watcher Service

This service automatically watches a directory for new or changed comic archive files (`.cbz`/`.cbr`), tags them using ComicTagger, and manages duplicates. It is designed to run in a Docker container and is fully automated. **For security, the container runs as user `nobody` and group `users`, and all files inside the container are owned by this user/group.**

## Features
- Watches a directory for file changes (create, modify, move/rename, delete)
- Processes `.cbz` and `.cbr` files only
- Uses ComicTagger to set comic metadata (title, issue, series, etc.)
- Renames files to a standard format based on metadata
- Handles duplicate files: moves them to a duplicate directory, preserving the original folder structure
- **Web Interface** for managing comic files:
  - One-click button to process all files in the watched directory
  - View and edit tags for individual files
  - Batch update tags for multiple selected files
  - Smart handling to prevent watcher conflicts with web-modified files
- Logs all actions to `ComicMaintainer.log`
- Containerized with Docker for easy deployment
- **Runs as user `nobody` and group `users` for improved container security**

## How It Works
1. The watcher service monitors a specified directory for new or changed `.cbz`/`.cbr` files.
2. When a file is detected and stable, it runs `process_file.py` to:
   - Read and update comic metadata using ComicTagger
   - Rename the file to a standard format (e.g., `Series - Chapter 0001.cbz`)
   - If a file with the new name already exists, the duplicate is moved to a duplicate directory, preserving the original parent folder
3. All actions and errors are logged.

## Usage

### Build the Docker image
```sh
docker build -t iceburn1/comictagger-watcher:latest .
```

**Security Note:** The container runs as user `nobody` and group `users`. All files and directories inside the container are owned by this user/group. If you mount host directories, ensure permissions are compatible if you need to access files from the host.


### Run the container
```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -v <host_dir_for_duplicates>:/duplicates \
  -e WATCHED_DIR=/watched_dir \
  -e DUPLICATE_DIR=/duplicates \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

```sh
docker run -d \
  -v <host_dir_to_watch>:/watched_dir \
  -e WATCHED_DIR=/watched_dir \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

- Replace `<host_dir_to_watch>` with the path to your comics folder.
- `WATCHED_DIR` **must** be set to the directory to watch (usually `/watched_dir` if using the example above).
- Optionally, mount a host directory to `/duplicates` to persist duplicates.
- The `-p 5000:5000` flag exposes the web interface on port 5000.
- Access the web interface at `http://localhost:5000`
- **Note:** If you mount host directories, files created or modified in the container will be owned by `nobody:users` (UID 99, GID 100). Adjust host permissions if you need to access these files outside the container.

### Environment Variables
- `WATCHED_DIR`: **(Required)** Directory to watch for comics. The service will not start if this is not set.
- `PROCESS_SCRIPT`: Script to run for processing (default: `/process_file.py`)
- `DUPLICATE_DIR`: Directory where duplicates are moved (required for duplicate handling)
- `WEB_PORT`: Port for the web interface (default: `5000`)

## Web Interface
The service includes a web-based interface for managing your comic files:

### Features
- **Process All Files**: One-click button to process all comic files in the watched directory
- **View/Edit Individual Tags**: Click on any file to view and edit its metadata tags
- **Batch Update**: Select multiple files and update common tags (series, publisher, year, writer) for all of them at once
- **Smart Processing**: Files modified through the web interface are marked to prevent the watcher from re-processing them automatically

### Usage
1. Access the web interface at `http://localhost:5000` (or your configured host/port)
2. The interface will display all `.cbz` and `.cbr` files in your watched directory
3. Use the checkboxes to select files for batch operations
4. Click "Process All Files" to run the standard processing on all files
5. Click "View/Edit" on any file to see and modify its tags
6. Select multiple files and click "Update Selected" to batch update common tags

## ComicTagger Integration
- ComicTagger is installed in the container from the **develop branch** and used via its Python API.
- The service supports both `.cbz` (zip) and `.cbr` (rar) files.
- **Note**: The code is compatible with both master and develop branch APIs of ComicTagger, automatically detecting which version is in use.

## Logging
- All actions and errors are logged to `ComicMaintainer.log` (in the container working directory).

## GitHub Actions / CI
- The repository includes a GitHub Actions workflow to automatically build and push the Docker image to Docker Hub on every push or pull request to `master`.

## Requirements
- Docker
- (Optional) Docker Hub account for pushing images

## License
MIT
