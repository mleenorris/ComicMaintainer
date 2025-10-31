#!/bin/bash
set -e

# Get PUID and PGID from environment variables, default to 99:100
PUID=${PUID:-99}
PGID=${PGID:-100}

echo "Starting ComicMaintainer with PUID=$PUID and PGID=$PGID"

# Create group if it doesn't exist
if ! getent group comicmaintainer > /dev/null 2>&1; then
    groupadd -g "$PGID" comicmaintainer
else
    # Modify existing group to use the specified GID
    groupmod -g "$PGID" comicmaintainer 2>/dev/null || true
fi

# Create user if it doesn't exist
if ! id comicmaintainer > /dev/null 2>&1; then
    useradd -r -u "$PUID" -g comicmaintainer comicmaintainer
else
    # Modify existing user to use the specified UID
    usermod -u "$PUID" comicmaintainer 2>/dev/null || true
fi

# Ensure directories exist and have correct permissions
mkdir -p /watched_dir /duplicates /Config
chown -R "$PUID:$PGID" /app /Config /watched_dir /duplicates 2>/dev/null || true
chmod -R 755 /watched_dir /duplicates /Config 2>/dev/null || true

# Switch to the specified user and run the application
echo "Switching to user comicmaintainer (UID=$PUID, GID=$PGID)"
exec su-exec comicmaintainer dotnet ComicMaintainer.WebApi.dll
