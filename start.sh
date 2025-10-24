#!/bin/bash

# Change to app directory
cd /app

# Validate environment variables before starting services
echo "Validating environment configuration..."
python /app/env_validator.py
if [ $? -ne 0 ]; then
    echo "Environment validation failed. Exiting."
    exit 1
fi

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

# Check if HTTPS is enabled and certificates are provided
if [ "${HTTPS_ENABLED}" = "true" ] && [ -n "${SSL_CERT}" ] && [ -n "${SSL_KEY}" ]; then
    echo "Starting Gunicorn with HTTPS enabled"
    if [ ! -f "${SSL_CERT}" ]; then
        echo "Error: SSL certificate file not found: ${SSL_CERT}"
        exit 1
    fi
    if [ ! -f "${SSL_KEY}" ]; then
        echo "Error: SSL key file not found: ${SSL_KEY}"
        exit 1
    fi
    gunicorn --workers ${GUNICORN_WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 --certfile=${SSL_CERT} --keyfile=${SSL_KEY} web_app:app &
    WEB_PID=$!
else
    echo "Starting Gunicorn with HTTP (HTTPS not enabled)"
    gunicorn --workers ${GUNICORN_WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 web_app:app &
    WEB_PID=$!
fi

# Function to check if a process is running
is_running() {
    kill -0 "$1" 2>/dev/null
}

# Monitor both processes and keep container alive as long as web server is running
# The web server is the primary service, so keep container alive as long as it's running
WATCHER_WARNED=0
while is_running $WEB_PID; do
    # Check if watcher died and log it once, but don't exit
    if ! is_running $WATCHER_PID && [ $WATCHER_WARNED -eq 0 ]; then
        echo "Warning: Watcher process (PID $WATCHER_PID) has exited, but web server is still running"
        # Don't restart watcher automatically to avoid restart loops
        WATCHER_WARNED=1
    fi
    sleep 5
done

# If we get here, the web server has exited
echo "Web server has exited, shutting down container"
exit 1
