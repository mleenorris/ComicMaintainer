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
# Reverse proxy support: --forwarded-allow-ips='*' trusts X-Forwarded-* headers from all proxies

# Build gunicorn command with optional SSL support
GUNICORN_CMD="gunicorn --workers ${GUNICORN_WORKERS} --bind 0.0.0.0:${WEB_PORT} --timeout 600 --forwarded-allow-ips='*'"

# Add SSL/TLS support if certificates are provided
if [ -n "$SSL_CERTFILE" ] && [ -n "$SSL_KEYFILE" ]; then
    if [ -f "$SSL_CERTFILE" ] && [ -f "$SSL_KEYFILE" ]; then
        echo "Starting with HTTPS enabled (certificates found)"
        GUNICORN_CMD="$GUNICORN_CMD --certfile $SSL_CERTFILE --keyfile $SSL_KEYFILE"
        
        # Add CA bundle if provided
        if [ -n "$SSL_CA_CERTS" ] && [ -f "$SSL_CA_CERTS" ]; then
            GUNICORN_CMD="$GUNICORN_CMD --ca-certs $SSL_CA_CERTS"
        fi
    else
        echo "Warning: SSL_CERTFILE or SSL_KEYFILE not found, starting without HTTPS"
    fi
else
    echo "Starting with HTTP only (no SSL certificates configured)"
fi

$GUNICORN_CMD web_app:app &
WEB_PID=$!

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
