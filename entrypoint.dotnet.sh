#!/bin/bash
set -e

# Get PUID and PGID from environment variables, default to 99:100
PUID=${PUID:-99}
PGID=${PGID:-100}

# Validate that PUID and PGID are numeric
if ! [[ "$PUID" =~ ^[0-9]+$ ]]; then
    echo "Error: PUID must be a numeric value, got: $PUID"
    exit 1
fi

if ! [[ "$PGID" =~ ^[0-9]+$ ]]; then
    echo "Error: PGID must be a numeric value, got: $PGID"
    exit 1
fi

echo "Starting ComicMaintainer with PUID=$PUID and PGID=$PGID"

# Handle group creation/assignment
GROUP_NAME="comicmaintainer"
EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1 || true)

if [ -n "$EXISTING_GROUP" ]; then
    # A group with this GID already exists, use it
    echo "Using existing group '$EXISTING_GROUP' with GID $PGID"
    GROUP_NAME="$EXISTING_GROUP"
elif getent group comicmaintainer > /dev/null 2>&1; then
    # comicmaintainer group exists but with different GID, modify it
    echo "Modifying existing comicmaintainer group to use GID $PGID"
    groupmod -g "$PGID" comicmaintainer 2>/dev/null || true
else
    # Create new group with specified GID
    echo "Creating new group comicmaintainer with GID $PGID"
    groupadd -g "$PGID" comicmaintainer
fi

# Handle user creation/assignment
USER_NAME="comicmaintainer"
EXISTING_USER=$(getent passwd "$PUID" | cut -d: -f1 || true)

if [ -n "$EXISTING_USER" ]; then
    # A user with this UID already exists, use it
    echo "Using existing user '$EXISTING_USER' with UID $PUID"
    USER_NAME="$EXISTING_USER"
    # Ensure the user is in the correct group
    usermod -g "$GROUP_NAME" "$EXISTING_USER" 2>/dev/null || true
elif id comicmaintainer > /dev/null 2>&1; then
    # comicmaintainer user exists but with different UID, modify it
    echo "Modifying existing comicmaintainer user to use UID $PUID"
    usermod -u "$PUID" -g "$GROUP_NAME" comicmaintainer 2>/dev/null || true
else
    # Create new user with specified UID
    echo "Creating new user comicmaintainer with UID $PUID"
    useradd -r -u "$PUID" -g "$GROUP_NAME" comicmaintainer
fi

# Ensure directories exist and have correct permissions
mkdir -p /watched_dir /duplicates /Config
chown -R "$PUID:$PGID" /app /Config /watched_dir /duplicates 2>/dev/null || true
chmod -R 755 /watched_dir /duplicates /Config 2>/dev/null || true

# Switch to the specified user and run the application
echo "Switching to user $USER_NAME (UID=$PUID, GID=$PGID)"
exec gosu "$USER_NAME" dotnet ComicMaintainer.WebApi.dll
