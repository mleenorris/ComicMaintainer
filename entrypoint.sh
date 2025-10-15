#!/bin/bash
set -e

# Default PUID and PGID to nobody:users (99:100)
PUID=${PUID:-99}
PGID=${PGID:-100}

echo "Starting with PUID=${PUID} and PGID=${PGID}"

# Create group if it doesn't exist
if ! getent group ${PGID} > /dev/null 2>&1; then
    echo "Creating group with GID ${PGID}"
    groupadd -g ${PGID} appgroup
else
    echo "Group with GID ${PGID} already exists"
fi

# Create user if it doesn't exist
if ! getent passwd ${PUID} > /dev/null 2>&1; then
    echo "Creating user with UID ${PUID}"
    useradd -u ${PUID} -g ${PGID} -M -s /bin/bash appuser
else
    echo "User with UID ${PUID} already exists"
fi

# Get the username and groupname
USERNAME=$(getent passwd ${PUID} | cut -d: -f1)
GROUPNAME=$(getent group ${PGID} | cut -d: -f1)

echo "Running as user: ${USERNAME} (${PUID}:${PGID})"

# Ensure the app directory is writable by the specified user
chown -R ${PUID}:${PGID} /app

# Ensure /Config directory is writable (used for persistent data)
if [ -d "/Config" ]; then
    echo "Setting permissions for /Config"
    chown -R ${PUID}:${PGID} /Config
fi

# Execute the command as the specified user
exec gosu ${PUID}:${PGID} "$@"
