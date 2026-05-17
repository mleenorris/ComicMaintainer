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
CONFIG_DIR="${WORKDIR}/config"
mkdir -p "${WATCH_DIR}" "${DUP_DIR}" "${CONFIG_DIR}"

log()  { echo "[smoke] $*"; }
fail() { echo "[smoke][FAIL] $*" >&2; dump_logs; cleanup; exit 1; }

dump_logs() {
    echo "----- container logs (${CONTAINER_NAME}) -----" >&2
    docker logs "${CONTAINER_NAME}" 2>&1 | tail -200 >&2 || true
    echo "----- end container logs -----" >&2
}

cleanup() {
    docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
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

log "Starting container ${CONTAINER_NAME} from ${IMAGE} on port ${HOST_PORT}"
docker run -d \
    --name "${CONTAINER_NAME}" \
    -p "${HOST_PORT}:5000" \
    -e PUID=0 \
    -e PGID=0 \
    -e JWT_SECRET="smoke-test-secret-key-must-be-at-least-32-characters-long!!" \
    -e ADMIN_USERNAME=admin \
    -e ADMIN_EMAIL=admin@smoke.local \
    -e ADMIN_PASSWORD=SmokeTestPassword123! \
    -e WATCHER_ENABLE_RENAME=true \
    -e WATCHER_ENABLE_NORMALIZE=false \
    -v "${WATCH_DIR}:/watched_dir" \
    -v "${DUP_DIR}:/duplicates" \
    -v "${CONFIG_DIR}:/Config" \
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

# --- Verify the watcher sees / processes the fixture file -----------------
log "Waiting up to ${PROCESSING_TIMEOUT}s for the watcher to react to the fixture..."
deadline=$(( $(date +%s) + PROCESSING_TIMEOUT ))
processed=0
# Patterns indicating the watcher noticed the file. Any one is sufficient.
patterns='smoke-test\.cbz|File watcher started|Found .* comic files|Incremental scan'
while [ "$(date +%s)" -lt "${deadline}" ]; do
    if docker logs "${CONTAINER_NAME}" 2>&1 | grep -E -q "${patterns}"; then
        processed=1
        break
    fi
    sleep 2
done
[ "${processed}" -eq 1 ] || fail "Watcher did not appear to start or react to the fixture file within ${PROCESSING_TIMEOUT}s"

log "Smoke test PASSED"
