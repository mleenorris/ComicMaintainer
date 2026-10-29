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

# Ensure directories exist and have correct permissions. /Config holds credentials and keys, so it only gains owner
# access. Only the last find below opens up /watched_dir and /duplicates, as it leaves secret files and the config
# directories out: a recursive chmod would expose them, and that find would not make them private again.
mkdir -p /watched_dir /duplicates /Config
chown -R "$PUID:$PGID" /app /Config /watched_dir /duplicates 2>/dev/null || true
chmod -R u+rwX /Config 2>/dev/null || true

# CONFIG_DIR holds the database, DataProtection keys, logs and settings, so a custom one must be
# writable by PUID too. (/Config is prepared above either way: it is the default temp file location.)
CONFIG_DIR="${CONFIG_DIR:-/Config}"
if [ "$CONFIG_DIR" != "/Config" ]; then
    # Best effort: root may be refused (e.g. NFS root_squash); the app then creates it as PUID.
    mkdir -p "$CONFIG_DIR" || echo "Warning: could not create $CONFIG_DIR as root; leaving it to $USER_NAME"
    chown -R "$PUID:$PGID" "$CONFIG_DIR" 2>/dev/null || true
    chmod -R u+rwX "$CONFIG_DIR" 2>/dev/null || true
fi

# The JWT signing key the app generates when JWT_SECRET is not set (and its temporary files) must stay private to
# its user, wherever it is kept, so it is left out of the permission change below. Keys made readable by earlier
# versions of this script are made private again first.
find /watched_dir /duplicates /Config "$CONFIG_DIR" -type f \( -name 'jwt-secret.key' -o -name 'jwt-secret.key.*.tmp' \) \
    -exec chmod 600 {} + 2>/dev/null || true
# Secret files named by JWT_SECRET_FILE or ADMIN_PASSWORD_FILE keep the permissions they were given. When the
# named path is a symlink, the file it points to is excluded too. find -path takes a glob pattern, so glob
# metacharacters in the paths are escaped to match them literally.
escape_find_glob() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//\*/\\*}
    value=${value//\?/\\?}
    value=${value//\[/\\[}
    printf '%s' "$value"
}
SECRET_FILE_EXCLUSIONS=()
for secret_file in "${JWT_SECRET_FILE:-}" "${ADMIN_PASSWORD_FILE:-}"; do
    if [ -n "$secret_file" ]; then
        SECRET_FILE_EXCLUSIONS+=(! -path "$(escape_find_glob "$(realpath -ms -- "$secret_file")")")
        SECRET_FILE_EXCLUSIONS+=(! -path "$(escape_find_glob "$(realpath -m -- "$secret_file")")")
    fi
done
# The config directories hold the database, DataProtection keys and settings, so they keep their permissions even when
# one is inside /watched_dir or /duplicates. They are matched by inode, which also catches a host folder nested in a
# library folder and mounted on its own. find -samefile fails on a missing file, so only existing ones are listed.
CONFIG_DIR_EXCLUSIONS=()
for config_dir in /Config "$CONFIG_DIR"; do
    if config_dir=$(realpath -e -- "$config_dir" 2>/dev/null); then
        CONFIG_DIR_EXCLUSIONS+=(-samefile "$config_dir" -prune -o)
    fi
done
find /watched_dir /duplicates "${CONFIG_DIR_EXCLUSIONS[@]}" ! -type l \
    ! \( -type f \( -name 'jwt-secret.key' -o -name 'jwt-secret.key.*.tmp' \) \) \
    "${SECRET_FILE_EXCLUSIONS[@]}" -exec chmod 755 {} + 2>/dev/null || true

# Switch to the specified user and run the application
echo "Switching to user $USER_NAME (UID=$PUID, GID=$PGID)"
exec gosu "$USER_NAME" dotnet ComicMaintainer.WebApi.dll
