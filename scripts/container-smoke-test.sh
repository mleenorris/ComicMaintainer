#!/usr/bin/env bash
# Container smoke test for ComicMaintainer.
#
# Verifies that the Docker image starts, the API responds, and the file
# watcher picks up a comic archive dropped into the watched directory.
#
# Inputs (env vars):
#   IMAGE          Docker image tag to test (default: comicmaintainer:ci)
#   CONTAINER_NAME Name to give the container  (default: comicmaintainer-smoke)
#   HOST_PORT      Host port to bind          (default: 5050)
#   STARTUP_TIMEOUT  Seconds to wait for /api/version to become healthy (default: 90)
#   PROCESSING_TIMEOUT  Seconds to wait for the watcher to log a detected file (default: 60)
#
# Exit codes:
#   0   all checks passed
#   non-zero  a check failed (container logs are emitted to stderr)
set -euo pipefail

IMAGE="${IMAGE:-comicmaintainer:ci}"
CONTAINER_NAME="${CONTAINER_NAME:-comicmaintainer-smoke}"
HOST_PORT="${HOST_PORT:-5050}"
STARTUP_TIMEOUT="${STARTUP_TIMEOUT:-90}"
PROCESSING_TIMEOUT="${PROCESSING_TIMEOUT:-60}"

WORKDIR="$(mktemp -d)"
WATCH_DIR="${WORKDIR}/watch"
DUP_DIR="${WORKDIR}/duplicates"
# The host folders belong to the runner's user, not to PUID, so the container is started with FIX_LIBRARY_PERMISSIONS=true,
# which hands the library to PUID. The config folder sits inside the duplicates folder on the host, so that permission
# change on /duplicates reaches it too and must leave it alone.
HOST_CONFIG_DIR="${DUP_DIR}/config"
APP_CONFIG_DIR="/custom-config"
mkdir -p "${WATCH_DIR}" "${DUP_DIR}" "${HOST_CONFIG_DIR}"

log()  { echo "[smoke] $*"; }
fail() { echo "[smoke][FAIL] $*" >&2; dump_logs; cleanup; exit 1; }

# Whether the container's logs match the given grep arguments. No pipeline feeds grep: under pipefail, grep -q stopping
# at its first match could end whatever is still writing to it with SIGPIPE, and the check would report no match.
container_logs_match() {
    local logs
    logs="$(docker logs "${CONTAINER_NAME}" 2>&1 || true)"
    grep -q "$@" <<<"${logs}"
}

dump_logs() {
    echo "----- container logs (${CONTAINER_NAME}) -----" >&2
    docker logs "${CONTAINER_NAME}" 2>&1 | tail -200 >&2 || true
    echo "----- end container logs -----" >&2
}

cleanup() {
    docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
    docker run --rm \
        --entrypoint /bin/sh \
        -v "${WATCH_DIR}:/watched_dir" \
        -v "${DUP_DIR}:/duplicates" \
        -v "${HOST_CONFIG_DIR}:${APP_CONFIG_DIR}" \
        "${IMAGE}" \
        -c "chown -R '$(id -u):$(id -g)' /watched_dir /duplicates '${APP_CONFIG_DIR}'" >/dev/null 2>&1 || true
    rm -rf "${WORKDIR}"
}
trap cleanup EXIT

# --- Build a tiny valid .cbz fixture (zip containing a 1x1 PNG) -----------
log "Creating fixture .cbz at ${WATCH_DIR}/smoke-test.cbz"
python3 - <<PY
import base64, io, zipfile, pathlib
# 1x1 transparent PNG
png_b64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="
)
png = base64.b64decode(png_b64)
buf = io.BytesIO()
with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
    zf.writestr("page01.png", png)
pathlib.Path("${WATCH_DIR}/smoke-test.cbz").write_bytes(buf.getvalue())
print("fixture written:", len(buf.getvalue()), "bytes")
PY

# Free the host port if a previous run left something behind
docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true

# Exercise a root-owned, private custom volume with the application running as an unprivileged user.
docker run --rm \
    --entrypoint /bin/sh \
    -v "${HOST_CONFIG_DIR}:${APP_CONFIG_DIR}" \
    "${IMAGE}" \
    -c "chown root:root '${APP_CONFIG_DIR}' && chmod 700 '${APP_CONFIG_DIR}' && touch '${APP_CONFIG_DIR}/.permission-test' && chmod 600 '${APP_CONFIG_DIR}/.permission-test'"

# The admin password is read from a private file in /duplicates that belongs to the runner's user. FIX_LIBRARY_PERMISSIONS
# must give it to PUID, or the app could not read it, and leave its mode private.
printf '%s\n' 'SmokeTestPassword123!' > "${DUP_DIR}/.admin-password"
chmod 600 "${DUP_DIR}/.admin-password"

log "Starting container ${CONTAINER_NAME} from ${IMAGE} on port ${HOST_PORT}"
docker run -d \
    --name "${CONTAINER_NAME}" \
    -p "${HOST_PORT}:5000" \
    -e PUID=10001 \
    -e PGID=10001 \
    -e CONFIG_DIR="${APP_CONFIG_DIR}" \
    -e JWT_SECRET="smoke-test-secret-key-must-be-at-least-32-characters-long!!" \
    -e ADMIN_USERNAME=admin \
    -e ADMIN_EMAIL=admin@smoke.local \
    -e ADMIN_PASSWORD_FILE=/duplicates/.admin-password \
    -e FIX_LIBRARY_PERMISSIONS=true \
    -e WATCHER_ENABLE_RENAME=true \
    -e WATCHER_ENABLE_NORMALIZE=false \
    -v "${WATCH_DIR}:/watched_dir" \
    -v "${DUP_DIR}:/duplicates" \
    -v "${HOST_CONFIG_DIR}:${APP_CONFIG_DIR}" \
    "${IMAGE}" >/dev/null

# --- Wait for /api/version ------------------------------------------------
log "Waiting up to ${STARTUP_TIMEOUT}s for /api/version to respond..."
deadline=$(( $(date +%s) + STARTUP_TIMEOUT ))
healthy=0
while [ "$(date +%s)" -lt "${deadline}" ]; do
    if ! docker ps --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
        fail "Container exited before becoming healthy"
    fi
    body="$(curl -fsS "http://127.0.0.1:${HOST_PORT}/api/version" 2>/dev/null || true)"
    if [ -n "${body}" ] && echo "${body}" | grep -q '"platform"'; then
        log "Version endpoint healthy: ${body}"
        healthy=1
        break
    fi
    sleep 2
done
[ "${healthy}" -eq 1 ] || fail "Timed out waiting for /api/version"

# Validate response shape
echo "${body}" | grep -q '".NET"' || fail "/api/version did not return platform=.NET"
echo "${body}" | grep -q '"version"' || fail "/api/version missing version field"

# Verify the app wrote its database to the custom directory and kept private modes private.
docker exec --user 10001 "${CONTAINER_NAME}" test -s "${APP_CONFIG_DIR}/comicmaintainer.db" \
    || fail "Database was not created in custom CONFIG_DIR"
directory_mode="$(docker exec --user 0 "${CONTAINER_NAME}" stat -c '%a' "${APP_CONFIG_DIR}")"
secret_mode="$(docker exec --user 0 "${CONTAINER_NAME}" stat -c '%a' "${APP_CONFIG_DIR}/.permission-test")"
[ "${directory_mode}" = "700" ] || fail "Custom CONFIG_DIR mode changed to ${directory_mode}, expected 700"
[ "${secret_mode}" = "600" ] || fail "Private config file mode changed to ${secret_mode}, expected 600"
admin_password_owner_mode="$(docker exec --user 0 "${CONTAINER_NAME}" stat -c '%u %a' /duplicates/.admin-password)"
[ "${admin_password_owner_mode}" = "10001 600" ] || fail "ADMIN_PASSWORD_FILE is ${admin_password_owner_mode}, expected owner 10001 and mode 600"

# FIX_LIBRARY_PERMISSIONS only adds owner and group access (u+rwX,g+rwX): the 755 library folder becomes 775 and is
# handed to PUID, so the app user can write it and the entrypoint does not warn that it cannot.
library_owner_mode="$(docker exec --user 0 "${CONTAINER_NAME}" stat -c '%u:%g %a' /watched_dir)"
[ "${library_owner_mode}" = "10001:10001 775" ] || fail "/watched_dir is ${library_owner_mode}, expected 10001:10001 775"
if container_logs_match 'cannot write to'; then
    fail "The entrypoint warned that the app user cannot write to a library folder"
fi

# --- Verify the watcher sees / processes the fixture file -----------------
log "Waiting up to ${PROCESSING_TIMEOUT}s for the watcher to react to the fixture..."
deadline=$(( $(date +%s) + PROCESSING_TIMEOUT ))
processed=0
# Patterns indicating the watcher noticed the file. Any one is sufficient.
patterns='smoke-test\.cbz|File watcher started|Found .* comic files|Incremental scan'
while [ "$(date +%s)" -lt "${deadline}" ]; do
    if container_logs_match -E "${patterns}"; then
        processed=1
        break
    fi
    sleep 2
done
[ "${processed}" -eq 1 ] || fail "Watcher did not appear to start or react to the fixture file within ${PROCESSING_TIMEOUT}s"

log "Smoke test PASSED"
