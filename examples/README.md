# ComicMaintainer Examples

This directory contains example scripts and configurations for ComicMaintainer.

## SSL Certificate Generation

### `generate-ssl-certs.sh`

Generate self-signed SSL certificates for testing HTTPS support.

**Usage:**
```bash
./generate-ssl-certs.sh [output_directory]
```

**Example:**
```bash
# Generate certificates in ./ssl directory (default)
./generate-ssl-certs.sh

# Generate certificates in a custom directory
./generate-ssl-certs.sh /path/to/my/certs
```

**Output:**
- `cert.pem` - SSL certificate
- `key.pem` - Private key

**Note:** Self-signed certificates are suitable for testing only. For production use, obtain certificates from a trusted Certificate Authority (CA) such as Let's Encrypt.

## HTTPS Configuration Examples

### Docker Run with HTTPS

```bash
# Generate certificates first
./examples/generate-ssl-certs.sh ./ssl

# Run container with HTTPS enabled
docker run -d \
  -v $(pwd)/ssl:/ssl:ro \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e HTTPS_ENABLED=true \
  -e SSL_CERT=/ssl/cert.pem \
  -e SSL_KEY=/ssl/key.pem \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 443:5000 \
  iceburn1/comictagger-watcher:latest
```

Access the web interface at `https://localhost:443`

### Docker Compose with HTTPS

Create a `docker-compose.yml` file:

```yaml
version: '3.8'

services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    container_name: comictagger-watcher
    restart: unless-stopped
    
    environment:
      - WATCHED_DIR=/watched_dir
      - HTTPS_ENABLED=true
      - SSL_CERT=/ssl/cert.pem
      - SSL_KEY=/ssl/key.pem
      - PUID=1000
      - PGID=1000
    
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
      - ./ssl:/ssl:ro
    
    ports:
      - "443:5000"
    
    healthcheck:
      test: ["CMD", "sh", "-c", "wget --quiet --tries=1 --spider https://localhost:5000/health || exit 1"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
```

Run with:
```bash
docker-compose up -d
```

## Production Considerations

For production deployments, consider using:

1. **Let's Encrypt** - Free, automated SSL certificates
2. **Reverse Proxy** - Nginx, Traefik, or Caddy for SSL termination
3. **Certificate Management** - Automatic renewal and monitoring
4. **Security Headers** - Add security headers via reverse proxy

See the main [README.md](../README.md) for detailed production setup instructions.
