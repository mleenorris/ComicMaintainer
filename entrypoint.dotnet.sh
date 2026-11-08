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

# /Config, and a custom CONFIG_DIR, hold the database, DataProtection keys, logs and settings, so the app user must
# own them. They hold credentials and keys, so only owner access is added. (/Config is prepared either way: it is the
# default temp file location.)
mkdir -p /Config
chown -R "$PUID:$PGID" /Config 2>/dev/null || true
chmod -R u+rwX /Config 2>/dev/null || true

CONFIG_DIR="${CONFIG_DIR:-/Config}"
if [ "$CONFIG_DIR" != "/Config" ]; then
    # Best effort: root may be refused (e.g. NFS root_squash); the app then creates it as PUID.
    mkdir -p "$CONFIG_DIR" || echo "Warning: could not create $CONFIG_DIR as root; leaving it to $USER_NAME"
    chown -R "$PUID:$PGID" "$CONFIG_DIR" 2>/dev/null || true
    chmod -R u+rwX "$CONFIG_DIR" 2>/dev/null || true
fi

# The JWT signing key the app generates when JWT_SECRET is not set (and its temporary files) must stay private to
# its user. Keys made readable by earlier versions of this script are made private again.
find /Config "$CONFIG_DIR" -type f \( -name 'jwt-secret.key' -o -name 'jwt-secret.key.*.tmp' \) \
    -exec chmod 600 {} + 2>/dev/null || true

# The comic library and the duplicates folder belong to the user and are left alone. Changing the owner or mode of
# everything in them at every start would strip the group write access other containers (Suwayomi, Komga, Kavita)
# rely on, expose libraries kept private, and walk the whole library, which takes minutes on NFS/SMB, before the
# app starts. Only the top folders are checked; a folder that does not exist yet is created for PUID.
LIBRARY_DIRS=("${WATCHED_DIR:-/watched_dir}")
if [ "${DUPLICATE_DIR:-/duplicates}" != "${LIBRARY_DIRS[0]}" ]; then
    LIBRARY_DIRS+=("${DUPLICATE_DIR:-/duplicates}")
fi

# FIX_LIBRARY_PERMISSIONS=true opts in to repairing the library once: everything in it is given to PUID:PGID with
# owner and group read/write access added (u+rwX,g+rwX: other bits are kept, and files gain no execute bit).
case "${FIX_LIBRARY_PERMISSIONS:-false}" in
    [Tt][Rr][Uu][Ee] | 1 | [Yy][Ee][Ss]) FIX_LIBRARY_PERMISSIONS=true ;;
    [Ff][Aa][Ll][Ss][Ee] | 0 | [Nn][Oo] | "") FIX_LIBRARY_PERMISSIONS=false ;;
    *)
        echo "Warning: FIX_LIBRARY_PERMISSIONS must be true or false, got: $FIX_LIBRARY_PERMISSIONS; the library is left alone."
        FIX_LIBRARY_PERMISSIONS=false
        ;;
esac

# Secret files named by JWT_SECRET_FILE or ADMIN_PASSWORD_FILE keep the permissions they were given, even inside the
# library. When the named path is a symlink, the file it points to is excluded too. find -path takes a glob pattern,
# so glob metacharacters in the paths are escaped to match them literally.
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
# The config directories keep their own permissions even when one is inside a library folder. They are matched by
# inode, which also catches a host folder nested in a library folder and mounted on its own. find -samefile fails on
# a missing file, so only existing ones are listed.
CONFIG_DIR_EXCLUSIONS=()
for config_dir in /Config "$CONFIG_DIR"; do
    if config_dir=$(realpath -e -- "$config_dir" 2>/dev/null); then
        CONFIG_DIR_EXCLUSIONS+=(-samefile "$config_dir" -prune -o)
    fi
done

# Whether the app user can list, read and create files in the directory $1.
app_user_can_write() {
    gosu "$USER_NAME" sh -c '[ -r "$1" ] && [ -w "$1" ] && [ -x "$1" ]' sh "$1"
}

for library_dir in "${LIBRARY_DIRS[@]}"; do
    if [ ! -d "$library_dir" ]; then
        if mkdir -p -- "$library_dir" 2>/dev/null; then
            chown "$PUID:$PGID" -- "$library_dir" 2>/dev/null || true
        else
            echo "Warning: $library_dir does not exist and could not be created."
            continue
        fi
    fi

    if [ "$FIX_LIBRARY_PERMISSIONS" = true ]; then
        echo "FIX_LIBRARY_PERMISSIONS=true: giving everything in $library_dir to UID $PUID and GID $PGID with owner and group read/write access. This walks the whole folder at every start; remove the variable once it has run."
        find "$library_dir" "${CONFIG_DIR_EXCLUSIONS[@]}" ! -type l \
            ! \( -type f \( -name 'jwt-secret.key' -o -name 'jwt-secret.key.*.tmp' \) \) \
            "${SECRET_FILE_EXCLUSIONS[@]}" -exec chown "$PUID:$PGID" {} + -exec chmod u+rwX,g+rwX {} + 2>/dev/null || true
    fi

    if ! app_user_can_write "$library_dir"; then
        echo "Warning: $USER_NAME (UID=$PUID, GID=$PGID) cannot write to $library_dir, so comics there cannot be tagged, renamed or moved."
        echo "         Give UID $PUID or GID $PGID write access to the host folder, set PUID/PGID to its owner, or set FIX_LIBRARY_PERMISSIONS=true once."
    fi
done

# Switch to the specified user and run the application. /app is left owned by root, so the app user cannot rewrite
# its own binaries.
echo "Switching to user $USER_NAME (UID=$PUID, GID=$PGID)"
exec gosu "$USER_NAME" dotnet ComicMaintainer.WebApi.dll
