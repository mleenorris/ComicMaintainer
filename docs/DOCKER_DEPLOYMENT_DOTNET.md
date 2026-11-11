# Docker Deployment Guide

This guide covers running ComicMaintainer with Docker. The published image is `iceburn1/comicmaintainer:dotnet`.

## Quick Start

### With the Published Image

```bash
docker run -d \
  --name comicmaintainer \
  -p 5000:5000 \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  --restart unless-stopped \
  iceburn1/comicmaintainer:dotnet
```

Set `PUID` and `PGID` to the user and group that own your comic folders. Then open `http://localhost:5000` and [create the first administrator](#first-administrator).

### With Docker Compose and the Published Image

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    restart: unless-stopped
    ports:
      - "5000:5000"
    environment:
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
```

```bash
docker compose up -d
docker compose logs -f
```

### Building From Source

The repository's `docker-compose.dotnet.yml` builds the image from `Dockerfile.dotnet`:

```bash
git clone --branch dotnet https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer

# Copy the example environment file, then edit .env: set PUID and PGID, and anything else you need
cp .env.example .env

docker compose -f docker-compose.dotnet.yml up -d --build
docker compose -f docker-compose.dotnet.yml logs -f
```

It mounts `./test_comics`, `./duplicates` and `./config`. Change those to your own folders, or replace `build:` with `image: iceburn1/comicmaintainer:dotnet` to use the published image.

To build the image on its own:

```bash
docker build -f Dockerfile.dotnet -t comicmaintainer:local .
```

## First Administrator

A new install has no accounts. Choose one of these:

**In the browser (the default).** Open the app and fill in the setup page. It asks for a one-time setup code, so that whoever reaches a new instance first cannot claim it. Until the first account exists, the code is printed in the log at every start:

```bash
docker logs comicmaintainer 2>&1 | grep -i "setup code"
```

To choose your own code, set `SETUP_TOKEN`, or `SETUP_TOKEN_FILE` for a Docker secret. First-run setup is only available while no account exists at all, and never while Authelia is enabled.

**From environment variables.** Set `ADMIN_PASSWORD` (or `ADMIN_PASSWORD_FILE`) to a password of your own, and optionally `ADMIN_USERNAME` (default `admin`), to have the account created on first start:

```bash
docker run -d \
  --name comicmaintainer \
  -p 5000:5000 \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -e ADMIN_PASSWORD="<a password of your own>" \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  iceburn1/comicmaintainer:dotnet
```

Passwords need at least 8 characters with an upper-case letter, a lower-case letter and a digit. Example passwords published with this project, such as `Admin123!`, are refused: no account is created, and the log says why.

An account that still has one of those published passwords cannot sign in. Set `ADMIN_USERNAME` to that account's name and `ADMIN_PASSWORD` to a password of your own, then restart, and the password is replaced. Once you can sign in as an administrator, set new passwords for the other affected accounts on the **Users** page instead.

After the first administrator, add the other accounts on the **Users** page. If you delete the account `ADMIN_USERNAME` names while `ADMIN_PASSWORD` is set, it is created again at the next start.

### Sign-In Protection

After five wrong passwords in a row, an account is locked for 15 minutes. Each client address gets 10 sign-in attempts per 5 minutes. Behind a reverse proxy, set [`TRUSTED_PROXIES`](REVERSE_PROXY.md#trusted-proxies-and-rate-limiting), or all clients share that one limit.

## Configuration

### Environment Variables

The most common ones are below. The [environment variable reference](ENVIRONMENT_VARIABLES.md) lists them all. Many settings can also be changed in the web interface; a setting pinned by an environment variable shows as read-only there.

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | `http://+:5000` | Addresses the app listens on. To use another port, change the host side of the port mapping (`-p 8080:5000`) instead; the container health check probes port 5000 |
| `WATCHED_DIR` | `/watched_dir` | Directory holding the library |
| `DUPLICATE_DIR` | `/duplicates` | Directory for duplicate files |
| `CONFIG_DIR` | `/Config` | Directory for the database, keys, logs and settings |
| `PUID` | `99` | User ID the app runs as |
| `PGID` | `100` | Group ID the app runs as |
| `FIX_LIBRARY_PERMISSIONS` | `false` | Set to `true` once to repair library permissions. See [File Permissions](#file-permissions) |
| `BASE_PATH` | *(empty)* | The folder the app is served under, such as `/comics`. Set it only for a subfolder install; see [Serving Under a Subfolder](REVERSE_PROXY.md#serving-under-a-subfolder-base_path) |
| `TRUSTED_PROXIES` | *(empty)* | Addresses of your reverse proxy. See the [Reverse Proxy Guide](REVERSE_PROXY.md#trusted-proxies-and-rate-limiting) |
| `AppSettings__MaxWorkers` | `4` | Archives processed at once, shared by the watcher, jobs and metadata edits (read at startup) |
| `JWT_SECRET` | *generated* | Signing key for sign-in tokens, 32 characters or more. When unset or set to a published placeholder, a random key is generated on first start and kept in `/Config/jwt-secret.key` |
| `JWT_SECRET_FILE` | | File holding `JWT_SECRET`, such as a Docker secret. Set only one of the two |
| `ADMIN_USERNAME` | `admin` | Name of the administrator created from the environment |
| `ADMIN_EMAIL` | `admin@comicmaintainer.local` | Email of that administrator |
| `ADMIN_PASSWORD` | *(unset)* | See [First Administrator](#first-administrator) |
| `ADMIN_PASSWORD_FILE` | | File holding `ADMIN_PASSWORD`. Set only one of the two |
| `SETUP_TOKEN` | *(generated)* | The first-run setup code. When unset, a random code is printed in the log |
| `SETUP_TOKEN_FILE` | | File holding `SETUP_TOKEN`. Set only one of the two |

The entrypoint creates a user and group with the IDs in `PUID` and `PGID` at startup, so files the app creates have that ownership on the host. Use `id -u` and `id -g` on the host to find your IDs.

### Volume Mounts

| Container path | Holds |
|---|---|
| `/watched_dir` | The comic library |
| `/duplicates` | Files flagged as duplicates |
| `/Config` | The SQLite database, sign-in keys, logs, settings and backups |

### File Permissions

The container starts as root only long enough to create the app user and give it `/Config` (and a custom `CONFIG_DIR`). The application files in `/app` stay owned by root, so the app cannot rewrite itself. Anything that used to write under `/app` must use `/Config`.

**Your comic folders are left alone.** The entrypoint does not change their owner or mode. It checks that `PUID` can write to them and logs a warning if it cannot. An empty top folder owned by root, such as the one Docker creates for a missing host folder, is given to `PUID`.

If you see the warning, do one of these:

- Set `PUID` and `PGID` to the owner of the folders.
- Give that user or group write access on the host.
- Set `FIX_LIBRARY_PERMISSIONS=true` once. At start the entrypoint then gives everything in the folders to `PUID:PGID` with owner and group read/write access. It walks the whole library at every start, so remove the variable afterwards.

**Library folders must allow creating files, not just changing them.** To change an archive, the app writes a new copy next to it (`.tmp_<id>.comicinfo-write`) and renames it over the original, so a half-written file can never replace a good one. On a library where files are writable but folders are not, tagging fails.

Each start deletes any `.tmp_<id>.comicinfo-write` files left by an interrupted write. After upgrading from an older version, the first start also cleans up after that version's write method: it removes leftover temporary files, and it restores `<archive>.backup` over an archive that is missing or unreadable. A `.backup` next to an intact archive is only reported in the log, never deleted. When that cleanup has finished, it is recorded in `/Config/archive-write-recovery-v3.done`; delete that file to run the cleanup again.

## Security Best Practices

### 1. Generate Secure Secrets

```bash
# JWT secret (48 bytes = 64 base64 characters)
openssl rand -base64 48

# Admin password
openssl rand -base64 24
```

Without `JWT_SECRET` (or `JWT_SECRET_FILE`), the app generates a random signing key on first start and keeps it in `/Config/jwt-secret.key`, readable only by the app's user, so sign-ins survive restarts. Keep that file private, like the database: anyone who has the key can sign in as any user. Delete it and restart the container to sign everyone out. Example secrets published in earlier versions are ignored, with a warning in the startup log. The startup log says which secret is in use.

### 2. Use Docker Secrets

```bash
openssl rand -base64 48 | docker secret create jwt_secret -
echo "your-admin-password" | docker secret create admin_password -

docker service create \
  --name comicmaintainer \
  --secret jwt_secret \
  --secret admin_password \
  --env JWT_SECRET_FILE=/run/secrets/jwt_secret \
  --env ADMIN_PASSWORD_FILE=/run/secrets/admin_password \
  -p 5000:5000 \
  iceburn1/comicmaintainer:dotnet
```

`JWT_SECRET_FILE` and `ADMIN_PASSWORD_FILE` name files that hold the values of `JWT_SECRET` and `ADMIN_PASSWORD`; the line break at the end of the file is ignored. Set either a variable or its `_FILE` variant, not both: the app refuses to start when both are set, or when the file cannot be read or is empty.

### 3. Do Not Publish the Port to Everyone

For access from your own machine only, bind the port to loopback:

```bash
docker run -d -p 127.0.0.1:5000:5000 ... iceburn1/comicmaintainer:dotnet
```

Behind a reverse proxy on the same Docker network, use `expose:` and no `ports:`. See the [Reverse Proxy Guide](REVERSE_PROXY.md).

## Database Management

### Backup Database

The SQLite database lives in `/Config/comicmaintainer.db`. Do **not** copy it with `cp` while the app is running: it uses WAL mode, so a plain copy can miss recent writes or be inconsistent. Use the built-in backups instead:

- The **`database-backup` scheduled job** (enabled by default, daily) writes `/Config/backups/comicmaintainer-yyyyMMdd-HHmmss.db` using SQLite's `VACUUM INTO`, which is consistent while the app is live, and keeps the newest 7. Tune with `APPSETTINGS__DATABASEBACKUPRETENTION` and `APPSETTINGS__DATABASEBACKUPDIRECTORY`; change the schedule under **Scheduled Jobs**.
- **Before an upgrade changes the database**, the app backs it up automatically as `comicmaintainer-<timestamp>-premigrate.db` and logs the path. The newest 3 of these are kept, separately from the daily ones. See [Upgrading](#upgrading).
- **On demand**, with an administrator's token:

```bash
# Get a token
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}' | sed 's/.*"token":"\([^"]*\)".*/\1/')

# Create a backup now
curl -X POST -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/backups

# List backups
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/backups

# Download one
curl -OJ -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/backups/comicmaintainer-20261020-040000.db/download
```

Backups contain password hashes and stored credentials (SMTP, API keys), so keep `/Config/backups` private and copy it off the host as part of your normal backup routine.

### Restore Database

```bash
# Restore an existing backup (or upload one with POST /api/backups/upload-restore, field "file")
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/backups/comicmaintainer-20261020-040000.db/restore

# Restart the container to apply it
docker restart comicmaintainer
```

The restore endpoint validates the file (SQLite header, `PRAGMA integrity_check`, ComicMaintainer schema), takes a `-prerestore` safety backup of the current database, and stages the file as `/Config/comicmaintainer.db.restore-pending`. On the next start, before migrations, the pending file replaces the database and the old `-wal` and `-shm` files are removed. Cancel a staged restore with `DELETE /api/backups/restore-pending`.

To restore by hand, stop the container, copy the backup over `comicmaintainer.db`, delete `comicmaintainer.db-wal` and `comicmaintainer.db-shm`, and start it again.

### Inspect the Database

Work on a copy, using a backup rather than the live file:

```bash
sqlite3 /path/to/config/backups/comicmaintainer-20261020-040000.db ".tables"
```

## Troubleshooting

### Check the Logs

```bash
docker logs -f comicmaintainer
```

The app also writes logs to `/Config`: `app.log`, `debug.log`, `watcher.log`, `metadata.log` and `auth.log`.

### Check That the App Answers

```bash
# Liveness: is the app up?
curl http://localhost:5000/health

# Readiness: can it reach its database?
curl http://localhost:5000/health/ready

# The version
curl http://localhost:5000/api/version
```

To test signing in:

```bash
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}'
```

### Common Issues

#### Port Already in Use

```bash
# Find what uses port 5000
lsof -i :5000

# Use a different host port
docker run -p 8080:5000 ... iceburn1/comicmaintainer:dotnet
```

#### Permission Denied or "cannot write" Warnings

See [File Permissions](#file-permissions). `PUID` and `PGID` must be able to create files in the library folders.

#### Database Locked

Only one instance may use a `/Config` folder at a time:

```bash
docker ps | grep comicmaintainer
```

#### "No Account Exists Yet" in the Log

This is the normal first-run message; see [First Administrator](#first-administrator). The setup code changes at every start until the first account exists.

#### Startup Stops With "Could not back up the database"

The app stops rather than change the database without a backup. Free disk space, or make the backup folder writable, and start it again. The database is unchanged.

## Production Deployment

### 1. Use HTTPS

Put a reverse proxy in front of the app, or serve HTTPS directly. See the [Reverse Proxy Guide](REVERSE_PROXY.md) and the [HTTPS Setup Guide](HTTPS_SETUP.md).

### 2. Resource Limits

```yaml
services:
  comicmaintainer:
    # ... other config ...
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 2G
        reservations:
          cpus: '1'
          memory: 1G
```

### 3. Health Checks

The image already has a health check that calls `/health`. To reproduce it in your own orchestration:

```yaml
services:
  comicmaintainer:
    # ... other config ...
    healthcheck:
      test: ["CMD", "curl", "--fail", "--silent", "--show-error", "http://localhost:5000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 60s
```

Use `/health/ready` where you want traffic held back until the database is reachable.

### 4. Logging

```yaml
services:
  comicmaintainer:
    # ... other config ...
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"
```

### 5. Container Hardening

`docker-compose.dotnet.yml` ships with:

```yaml
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    cap_add:
      - CHOWN         # give /Config to PUID:PGID
      - FOWNER        # chmod files the root user does not own
      - DAC_OVERRIDE  # traverse host-owned volumes; edit /etc/passwd, /etc/group
      - SETUID        # gosu: switch to PUID
      - SETGID        # gosu: switch to PGID / setgroups
```

`CHOWN` also covers `FIX_LIBRARY_PERMISSIONS` and giving an empty root-owned library folder to `PUID`. The entrypoint runs as root only long enough to create the `PUID`/`PGID` user, prepare `/Config` and `exec gosu` into the app; `no-new-privileges` is compatible with that because gosu only drops privileges. The app listens on port 5000, so `NET_BIND_SERVICE` is not needed. If you remove a capability and the container fails at startup, check the entrypoint output for the failing `useradd`, `chown` or `gosu` step.

A read-only root filesystem is offered as a commented example (`read_only: true` plus `tmpfs: /tmp`). All application state lives in the mounted volumes, but the entrypoint edits `/etc/passwd` and `/etc/group` when the `PUID`/`PGID` user does not already exist in the image, so test it in your environment before enabling it.

The image contains no `unrar` or `7z` programs; the app reads archives in-process.

### 6. Monitoring

- **Prometheus:** set `TELEMETRY__PROMETHEUSENABLED=true` to expose `/metrics`. Set `TELEMETRY__METRICSTOKEN` to require `Authorization: Bearer <token>`, and configure the same value as the scrape job's `authorization.credentials`. Without a token the endpoint is anonymous, so block `/metrics` at your reverse proxy if the app is reachable from untrusted networks.
- **OTLP:** set `OTEL_EXPORTER_OTLP_ENDPOINT` (for example `http://otel-collector:4317`) to push metrics and traces to an OpenTelemetry collector.
- **Swagger:** set `API__ENABLESWAGGER=true` to serve `/swagger` outside Development.

## Upgrading

### Pull New Image

```bash
docker compose pull
docker compose up -d
```

### Pin or Roll Back to a Version

Every release is also published as `iceburn1/comicmaintainer:X.Y.Z`, while `:dotnet` always points at the newest release. The web interface shows the version as `X.Y.Z.0`. To stay on a release, or to go back to one after an upgrade, use its tag instead of `:dotnet`:

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:3.0.0
```

```bash
docker compose up -d
```

The available versions are listed on [Docker Hub](https://hub.docker.com/r/iceburn1/comicmaintainer/tags).

### What Happens to the Database

When an upgrade has database changes to apply, the app first backs up the database to `/Config/backups/comicmaintainer-<timestamp>-premigrate.db` and logs the path.

- If the backup fails, for example because the disk is full, the app stops without changing the database. Fix the cause and start it again.
- If the upgrade is interrupted and retried, the first backup is reused, so a restart loop does not take a new one each time and the backup from before the upgrade is kept.

An older release does not undo database changes and may not work with the newer schema. To go back past a release that changed the database, [restore](#restore-database) the `-premigrate` backup from before that upgrade, then start the older image.

### Rebuild From Source

```bash
git pull origin dotnet
docker compose -f docker-compose.dotnet.yml build --no-cache
docker compose -f docker-compose.dotnet.yml up -d
```

## Support

- Check the logs: `docker logs comicmaintainer`
- Security policy and hardening: [SECURITY.md](../SECURITY.md)
- API reference: [API.md](API.md), or `/swagger` when enabled
- [ComicMaintainer issues](https://github.com/mleenorris/ComicMaintainer/issues)
