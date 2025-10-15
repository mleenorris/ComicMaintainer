#!/bin/bash

# Change to app directory
cd /app

# Start the watcher in the background
python /app/watcher.py &
WATCHER_PID=$!

# Get the port from environment variable (default: 5000)
WEB_PORT=${WEB_PORT:-5000}

# Get number of workers from environment variable (default: 2)
# With PostgreSQL backend, multiple workers are now supported
WORKERS=${GUNICORN_WORKERS:-2}

# Start the web app with Gunicorn (production WSGI server)
# PostgreSQL backend allows multiple workers for better performance
# Concurrency is provided by ThreadPoolExecutor in JobManager (default: 4 workers per process)
# Timeout increased to 600 seconds (10 minutes) to handle batch processing of large libraries
gunicorn --workers ${WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 web_app:app &
WEB_PID=$!

# Wait for both processes
wait $WATCHER_PID $WEB_PID
