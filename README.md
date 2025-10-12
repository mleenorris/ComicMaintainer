# Python Watcher Service

This service watches a specified directory for file changes and runs a script on each changed file. It is designed to run in a Docker container and includes ComicTagger as a dependency.

## Features
- Watches a directory for file changes (create, modify, delete)
- Runs a Python script on each changed file
- Containerized with Docker
- ComicTagger installed for comic file processing

## Usage
1. Build the Docker image:
   ```powershell
   docker build -t python-watcher-service .
   ```
2. Run the container, mounting the directory to watch:
   ```powershell
   docker run -v <host_dir_to_watch>:/watched_dir python-watcher-service
   ```
3. The service will process changed files using the script in `process_file.py`.

## Notes
- Replace the placeholder in `process_file.py` with the converted logic from your PowerShell script.
- ComicTagger is installed and available in the container.
