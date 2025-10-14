#!/bin/bash

# Start the watcher in the background
python /watcher.py &
WATCHER_PID=$!

# Get the port from environment variable (default: 5000)
WEB_PORT=${WEB_PORT:-5000}

# Start the web app with Gunicorn (production WSGI server)
# Using 4 workers, binding to all interfaces on the specified port
gunicorn --workers 4 --bind 0.0.0.0:${WEB_PORT} --timeout 120 web_app:app &
WEB_PID=$!

# Wait for both processes
wait $WATCHER_PID $WEB_PID
