# Docker Deployment Guide - .NET Application

This guide covers building and deploying the ComicMaintainer .NET application with Docker.

## Quick Start

### 1. Setup Environment Variables

```bash
# Copy the example environment file
cp .env.example .env

# Generate a secure JWT secret
openssl rand -base64 48 > jwt_secret.txt

# Edit .env and update:
# - JWT_SECRET with the generated value
# - ADMIN_PASSWORD to a strong password
# - Other settings as needed
```

### 2. Build and Run with Docker Compose

```bash
# Build and start the container
docker-compose -f docker-compose.dotnet.yml up -d

# View logs
docker-compose -f docker-compose.dotnet.yml logs -f

# Stop the container
docker-compose -f docker-compose.dotnet.yml down
```

### 3. Access the Application

- Web Interface: http://localhost:5000
- API Documentation: http://localhost:5000/swagger (in development mode)

## Manual Docker Build

### Build Image

```bash
docker build -f Dockerfile.dotnet -t comicmaintainer-dotnet:latest .
```

### Run Container

```bash
docker run -d \
  --name comicmaintainer-dotnet \
  -p 5000:5000 \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -e JWT_SECRET="$(cat jwt_secret.txt)" \
  -e ADMIN_PASSWORD="YourStrongPassword123!" \
  -v $(pwd)/test_comics:/watched_dir \
  -v $(pwd)/duplicates:/duplicates \
  -v $(pwd)/config:/Config \
  comicmaintainer-dotnet:latest
```

**Note**: Setting `PUID` and `PGID` to match your host user ensures proper file ownership.

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_URLS` | http://+:5000 | Server URLs |
| `WATCHED_DIR` | /watched_dir | Directory to watch for comics |
| `DUPLICATE_DIR` | /duplicates | Directory for duplicate files |
| `CONFIG_DIR` | /Config | Configuration and database directory |
| `PUID` | 99 | User ID for file operations (dynamically created) |
| `PGID` | 100 | Group ID for file operations (dynamically created) |
| `MAX_WORKERS` | 4 | Maximum concurrent workers |
| `DB_CACHE_SIZE_MB` | 64 | Database cache size |
| `JWT_SECRET` | *required* | JWT signing key (32+ chars). A blank value counts as unset, and the app then refuses to start unless `JwtSettings__Secret` holds your own secret |
| `ADMIN_USERNAME` | admin | Default admin username |
| `ADMIN_EMAIL` | admin@local | Default admin email |
| `ADMIN_PASSWORD` | *required* | Default admin password |

**Note on PUID/PGID**: The application dynamically creates a user and group with the specified IDs at container startup. This ensures files created by the container have the correct ownership on the host system. Use `id -u` and `id -g` on your host to find your user/group IDs.

### Volume Mounts

- `/watched_dir` - Directory containing comic files to process
- `/duplicates` - Directory where duplicate files are moved
- `/Config` - Configuration files and SQLite database

## Security Best Practices

### 1. Generate Secure Secrets

```bash
# Generate JWT secret (48 bytes = 64 base64 characters)
openssl rand -base64 48

# Generate admin password
openssl rand -base64 24
```

### 2. Use Docker Secrets (Recommended for Production)

```bash
# Create secrets
echo "your-jwt-secret" | docker secret create jwt_secret -
echo "your-admin-password" | docker secret create admin_password -

# Run with secrets
docker service create \
  --name comicmaintainer-dotnet \
  --secret jwt_secret \
  --secret admin_password \
  --env JWT_SECRET_FILE=/run/secrets/jwt_secret \
  --env ADMIN_PASSWORD_FILE=/run/secrets/admin_password \
  -p 5000:5000 \
  comicmaintainer-dotnet:latest
```

### 3. Set Proper Permissions

```bash
# Find your user and group IDs
echo "Your UID: $(id -u)"
echo "Your GID: $(id -g)"

# Set PUID and PGID in .env to match your user
# This ensures files created by the container have correct ownership

# Create directories
mkdir -p test_comics duplicates config
chmod -R 755 test_comics duplicates config

# The container will automatically set ownership based on PUID/PGID
```

### 4. Network Security

```bash
# Use Docker networks for isolation
docker network create comicmaintainer-net

docker run -d \
  --name comicmaintainer-dotnet \
  --network comicmaintainer-net \
  -p 127.0.0.1:5000:5000 \
  comicmaintainer-dotnet:latest
```

## Database Management

### Backup Database

The SQLite database lives in `/Config/comicmaintainer.db`. Do **not** copy it
with `cp` while the app is running — it uses WAL mode, so a plain copy can miss
recent writes or be inconsistent. Use the built-in backups instead:

- The **`database-backup` scheduled job** (enabled by default, daily) writes
  `/Config/backups/comicmaintainer-yyyyMMdd-HHmmss.db` using SQLite's
  `VACUUM INTO` (consistent while the app is live) and keeps the newest 7.
  Tune with `APPSETTINGS__DATABASEBACKUPRETENTION` and
  `APPSETTINGS__DATABASEBACKUPDIRECTORY`; change the schedule under
  **Scheduled Jobs**.
- **On demand** (admin token required):

```bash
# Create a backup now
curl -X POST -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/backups
# List backups
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/backups
# Download one
curl -OJ -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/backups/comicmaintainer-20261020-040000.db/download
```

Backups contain password hashes and stored credentials (SMTP, API keys), so
keep `/Config/backups` private and copy it off-host as part of your normal
backup routine.

### Restore Database

```bash
# Restore an existing backup (or upload one with POST /api/backups/upload-restore, field "file")
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/backups/comicmaintainer-20261020-040000.db/restore

# Restart the container to apply it
docker restart comicmaintainer-dotnet
```

The restore endpoint validates the file (SQLite header, `PRAGMA
integrity_check`, ComicMaintainer schema), takes a `-prerestore` safety backup
of the current database, and stages the file as
`/Config/comicmaintainer.db.restore-pending`. On the next start — before
migrations — the pending file replaces the database and the old `-wal`/`-shm`
files are removed. Cancel a staged restore with
`DELETE /api/backups/restore-pending`. To restore manually, stop the container,
copy the backup over `comicmaintainer.db`, delete `comicmaintainer.db-wal` and
`comicmaintainer.db-shm`, and start it again.

### View Database

```bash
# Install sqlite3 if not available
docker exec -it comicmaintainer-dotnet apt-get update
docker exec -it comicmaintainer-dotnet apt-get install -y sqlite3

# Query database
docker exec -it comicmaintainer-dotnet \
  sqlite3 /Config/comicmaintainer.db "SELECT * FROM AspNetUsers;"
```

## Troubleshooting

### Check Logs

```bash
# Docker Compose
docker-compose -f docker-compose.dotnet.yml logs -f

# Docker
docker logs -f comicmaintainer-dotnet
```

### Check Container Status

```bash
docker ps -a | grep comicmaintainer
```

### Test Connectivity

```bash
# Health check
curl http://localhost:5000/api/watcher/status

# Test authentication
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"YourPassword"}'
```

### Common Issues

#### Port Already in Use
```bash
# Find process using port 5000
lsof -i :5000

# Use different port
docker run -p 8080:5000 comicmaintainer-dotnet:latest
```

#### Permission Denied
```bash
# Fix volume permissions
sudo chown -R 1000:1000 config watched_dir duplicates
```

#### Database Locked
```bash
# Ensure only one instance is running
docker ps | grep comicmaintainer

# Stop all instances
docker stop $(docker ps -q --filter name=comicmaintainer)
```

## Production Deployment

### 1. Use HTTPS

Set up a reverse proxy (nginx, Traefik, etc.) for HTTPS:

```nginx
server {
    listen 443 ssl http2;
    server_name comics.yourdomain.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    # SignalR WebSocket support
    location /hubs/ {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

### 2. Resource Limits

```yaml
# docker-compose.dotnet.yml
services:
  comicmaintainer-dotnet:
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

```yaml
services:
  comicmaintainer-dotnet:
    # ... other config ...
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5000/api/watcher/status"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
```

### 4. Logging

```yaml
services:
  comicmaintainer-dotnet:
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
      - CHOWN         # chown -R the volumes to PUID:PGID
      - FOWNER        # chmod files the root user does not own
      - DAC_OVERRIDE  # traverse host-owned volumes; edit /etc/passwd, /etc/group
      - SETUID        # gosu: switch to PUID
      - SETGID        # gosu: switch to PGID / setgroups
```

The entrypoint runs as root only long enough to create the PUID/PGID user,
fix volume ownership and `exec gosu` into the app; `no-new-privileges` is
compatible with that because gosu only drops privileges. The app listens on
port 5000, so `NET_BIND_SERVICE` is not needed. If you remove a capability and
the container fails at startup, check the entrypoint output for the failing
`useradd`/`chown`/`gosu` step.

A read-only root filesystem is offered as a commented example
(`read_only: true` plus `tmpfs: /tmp`). All application state lives in the
mounted volumes, but the entrypoint edits `/etc/passwd`/`/etc/group` when the
PUID/PGID user does not already exist in the image, so test it in your
environment before enabling it.

### 6. Monitoring

- **Prometheus:** set `TELEMETRY__PROMETHEUSENABLED=true` to expose `/metrics`.
  Set `TELEMETRY__METRICSTOKEN` to require `Authorization: Bearer <token>`
  (configure the same value as the scrape job's `authorization.credentials`).
  Without a token the endpoint is anonymous — block `/metrics` at your reverse
  proxy if the app is reachable from untrusted networks.
- **OTLP:** set `OTEL_EXPORTER_OTLP_ENDPOINT` (e.g. `http://otel-collector:4317`)
  to push metrics and traces to an OpenTelemetry collector.
- **Swagger:** set `API__ENABLESWAGGER=true` to serve `/swagger` outside Development.

## Upgrading

### Pull New Image

```bash
docker-compose -f docker-compose.dotnet.yml pull
docker-compose -f docker-compose.dotnet.yml up -d
```

### Rebuild from Source

```bash
git pull origin main
docker-compose -f docker-compose.dotnet.yml build --no-cache
docker-compose -f docker-compose.dotnet.yml up -d
```

## Support

For issues and questions:
- Check logs: `docker-compose -f docker-compose.dotnet.yml logs`
- Review security documentation: `SECURITY_FIXES_DOTNET.md`
- Check API documentation: http://localhost:5000/swagger
