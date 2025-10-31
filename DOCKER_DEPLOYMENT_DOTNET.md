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
| `JWT_SECRET` | *required* | JWT signing key (32+ chars) |
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

```bash
# The SQLite database is stored in /Config/comicmaintainer.db
docker exec comicmaintainer-dotnet \
  cp /Config/comicmaintainer.db /Config/backup-$(date +%Y%m%d).db

# Or from host (if volume mounted)
cp config/comicmaintainer.db config/backup-$(date +%Y%m%d).db
```

### Restore Database

```bash
docker exec comicmaintainer-dotnet \
  cp /Config/backup-20231031.db /Config/comicmaintainer.db

# Restart container to apply
docker restart comicmaintainer-dotnet
```

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
