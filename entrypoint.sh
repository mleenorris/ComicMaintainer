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

# Ensure CACHE_DIR is writable if it's set and exists (e.g., mounted volume)
if [ -n "${CACHE_DIR}" ] && [ -d "${CACHE_DIR}" ]; then
    echo "Setting permissions for CACHE_DIR: ${CACHE_DIR}"
    chown -R ${PUID}:${PGID} ${CACHE_DIR}
fi

# Execute the command as the specified user
exec gosu ${PUID}:${PGID} "$@"
