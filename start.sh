#!/bin/bash

# Change to app directory
cd /app

# Start the watcher in the background
python /app/watcher.py &
WATCHER_PID=$!

# Get the port from environment variable (default: 5000)
WEB_PORT=${WEB_PORT:-5000}

# Get number of workers from environment variable (default: 2)
# Job state is now stored in SQLite, supporting multiple workers
GUNICORN_WORKERS=${GUNICORN_WORKERS:-2}

# Start the web app with Gunicorn (production WSGI server)
# Job state stored in SQLite database for cross-process sharing
# Concurrency is provided by both multiple workers and ThreadPoolExecutor (default: 4 threads per worker, configurable via MAX_WORKERS)
# Timeout increased to 600 seconds (10 minutes) to handle batch processing of large libraries
gunicorn --workers ${GUNICORN_WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 web_app:app &
WEB_PID=$!

# Wait for both processes
wait $WATCHER_PID $WEB_PID
