#!/bin/bash

# Change to app directory
cd /app

# Start the watcher in the background
python /app/watcher.py &
WATCHER_PID=$!

# Get the port from environment variable (default: 5000)
WEB_PORT=${WEB_PORT:-5000}

# Start the web app with Gunicorn (production WSGI server)
# Using 1 worker to ensure job state consistency (jobs stored in-memory)
# Concurrency is provided by ThreadPoolExecutor in JobManager (default: 4 workers, configurable via MAX_WORKERS)
# Timeout increased to 600 seconds (10 minutes) to handle batch processing of large libraries
gunicorn --workers 1 --bind 0.0.0.0:${WEB_PORT} --timeout 600 web_app:app &
WEB_PID=$!

# Wait for both processes
wait $WATCHER_PID $WEB_PID
