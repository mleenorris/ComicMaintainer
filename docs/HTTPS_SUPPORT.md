# HTTPS Support Documentation

ComicMaintainer now supports HTTPS for secure communication with the web interface. This document provides detailed information about HTTPS configuration and usage.

## Overview

HTTPS support is implemented through Gunicorn's built-in SSL/TLS capabilities. The feature is opt-in and fully backward compatible with existing HTTP deployments.

## Features

- ✅ **Built-in SSL/TLS Support**: Direct HTTPS support without reverse proxy
- ✅ **Environment-Driven Configuration**: Enable HTTPS via environment variables
- ✅ **Flexible Certificate Support**: Works with self-signed and CA-signed certificates
- ✅ **Backward Compatible**: Existing deployments continue to work without changes
- ✅ **Production Ready**: Supports Let's Encrypt and other trusted CAs
- ✅ **Kubernetes Ready**: Full support for cert-manager and Ingress TLS

## Quick Start

### 1. Generate Certificates

For testing, use the provided script to generate self-signed certificates:

```bash
./examples/generate-ssl-certs.sh ./ssl
```

For production, obtain certificates from a trusted CA like Let's Encrypt.

### 2. Run with HTTPS

```bash
docker run -d \
  -v $(pwd)/ssl:/ssl:ro \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e HTTPS_ENABLED=true \
  -e SSL_CERT=/ssl/cert.pem \
  -e SSL_KEY=/ssl/key.pem \
  -p 443:5000 \
  iceburn1/comictagger-watcher:latest
```

### 3. Access the Interface

Open your browser and navigate to `https://localhost:443`

## Configuration

### Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `HTTPS_ENABLED` | No | `false` | Enable HTTPS support |
| `SSL_CERT` | Yes* | - | Path to SSL certificate file |
| `SSL_KEY` | Yes* | - | Path to SSL private key file |

*Required when `HTTPS_ENABLED=true`

### Certificate Formats

Supported certificate formats:
- **PEM** (recommended): `.pem`, `.crt`, `.cer`
- **Key formats**: `.pem`, `.key`

The certificate file should contain the full certificate chain (if applicable), and the key file should be in PEM format.

## Deployment Scenarios

### Scenario 1: Self-Signed Certificates (Testing)

**Use Case**: Local development, testing, or internal networks where certificate warnings are acceptable.

**Steps:**
1. Generate self-signed certificate
2. Configure container with HTTPS environment variables
3. Accept security warning in browser

**Example:**
```bash
# Generate certificate
./examples/generate-ssl-certs.sh ./ssl

# Run container
docker run -d \
  -v $(pwd)/ssl:/ssl:ro \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e HTTPS_ENABLED=true \
  -e SSL_CERT=/ssl/cert.pem \
  -e SSL_KEY=/ssl/key.pem \
  -p 443:5000 \
  iceburn1/comictagger-watcher:latest
```

### Scenario 2: Let's Encrypt (Production)

**Use Case**: Production deployments requiring trusted SSL certificates.

**Steps:**
1. Obtain Let's Encrypt certificates using certbot
2. Mount certificate directory in container
3. Configure container to use certificates

**Example:**
```bash
# Obtain certificates with certbot (one-time setup)
sudo certbot certonly --standalone -d comicmaintainer.example.com

# Run container with Let's Encrypt certificates
docker run -d \
  -v /etc/letsencrypt/live/comicmaintainer.example.com:/ssl:ro \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e HTTPS_ENABLED=true \
  -e SSL_CERT=/ssl/fullchain.pem \
  -e SSL_KEY=/ssl/privkey.pem \
  -p 443:5000 \
  iceburn1/comictagger-watcher:latest
```

### Scenario 3: Reverse Proxy (Recommended for Production)

**Use Case**: Production deployments with complex routing, load balancing, or multiple services.

**Benefits:**
- Centralized SSL/TLS management
- Advanced features (load balancing, rate limiting, etc.)
- Easier certificate renewal
- Better separation of concerns

**Example with Nginx:**
```nginx
server {
    listen 443 ssl http2;
    server_name comicmaintainer.example.com;
    
    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;
    
    # SSL configuration (modern, secure settings)
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    
    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # WebSocket and SSE support
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 300s;
        proxy_buffering off;
    }
}
```

**Example with Traefik (Docker Compose):**
```yaml
version: '3.8'

services:
  traefik:
    image: traefik:v2.10
    command:
      - "--providers.docker=true"
      - "--entrypoints.websecure.address=:443"
      - "--certificatesresolvers.letsencrypt.acme.tlschallenge=true"
      - "--certificatesresolvers.letsencrypt.acme.email=admin@example.com"
      - "--certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json"
    ports:
      - "443:443"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ./letsencrypt:/letsencrypt
  
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    environment:
      - WATCHED_DIR=/watched_dir
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.comicmaintainer.rule=Host(`comicmaintainer.example.com`)"
      - "traefik.http.routers.comicmaintainer.entrypoints=websecure"
      - "traefik.http.routers.comicmaintainer.tls.certresolver=letsencrypt"
```

### Scenario 4: Kubernetes with cert-manager

**Use Case**: Kubernetes deployments with automatic certificate management.

**Steps:**
1. Install cert-manager in your cluster
2. Create ClusterIssuer for Let's Encrypt
3. Configure Ingress with TLS

See [docs/kubernetes-deployment.yaml](kubernetes-deployment.yaml) for a complete example.

## Security Best Practices

### Certificate Management

1. **Use Trusted CAs**: For production, always use certificates from trusted CAs (Let's Encrypt, DigiCert, etc.)
2. **Strong Keys**: Use at least 2048-bit RSA keys or 256-bit ECC keys
3. **Certificate Renewal**: Automate certificate renewal (Let's Encrypt certificates expire every 90 days)
4. **Secure Storage**: Protect private keys with appropriate file permissions (600 or 400)
5. **Certificate Monitoring**: Monitor certificate expiration dates

### TLS Configuration

1. **Modern Protocols**: Use TLS 1.2 or TLS 1.3 (disable older protocols)
2. **Strong Ciphers**: Use strong cipher suites (no RC4, MD5, or weak ciphers)
3. **HSTS**: Enable HTTP Strict Transport Security (via reverse proxy)
4. **Certificate Transparency**: Enable CT logging

### Additional Security

1. **Firewall Rules**: Restrict access to HTTPS port (443)
2. **Rate Limiting**: Implement rate limiting (via reverse proxy)
3. **Security Headers**: Add security headers (CSP, X-Frame-Options, etc.)
4. **Regular Updates**: Keep Docker image and dependencies updated

## Troubleshooting

### Certificate Errors

**Issue**: "Certificate verification failed" or "NET::ERR_CERT_AUTHORITY_INVALID"

**Solution**:
- For self-signed certificates: This is expected; accept the security warning or add certificate to trusted store
- For CA-signed certificates: Verify certificate chain is complete and valid

### Connection Refused

**Issue**: Cannot connect to HTTPS port

**Solution**:
- Verify HTTPS is enabled (`HTTPS_ENABLED=true`)
- Verify port mapping is correct (e.g., `-p 443:5000`)
- Check firewall rules
- Verify certificate files exist and are readable

### Container Fails to Start

**Issue**: Container exits immediately when HTTPS is enabled

**Solution**:
- Check container logs: `docker logs <container_id>`
- Verify certificate file paths are correct
- Verify certificate files are mounted correctly (read-only is recommended)
- Check file permissions on certificate files

### Health Check Failures

**Issue**: Health checks fail with HTTPS enabled

**Solution**:
- Verify health check is using HTTPS scheme
- For self-signed certificates, health checks may need to skip certificate verification
- Check if wget supports HTTPS (included in base image)

## Performance Considerations

### SSL/TLS Overhead

- **CPU Usage**: SSL/TLS encryption adds ~5-10% CPU overhead
- **Latency**: Minimal latency increase (~1-2ms per request)
- **Connection Reuse**: Use HTTP/2 or keep-alive to minimize handshake overhead

### Optimization Tips

1. **Enable HTTP/2**: Supported by Gunicorn with HTTPS
2. **Session Resumption**: Enabled by default in modern TLS
3. **OCSP Stapling**: Configure in reverse proxy for better performance
4. **CDN**: Consider using CDN for static assets

## Testing

### Local Testing

```bash
# Generate test certificate
./examples/generate-ssl-certs.sh ./ssl

# Run container with HTTPS
docker run -d \
  -v $(pwd)/ssl:/ssl:ro \
  -v /tmp/comics:/watched_dir \
  -v /tmp/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e HTTPS_ENABLED=true \
  -e SSL_CERT=/ssl/cert.pem \
  -e SSL_KEY=/ssl/key.pem \
  -p 8443:5000 \
  iceburn1/comictagger-watcher:latest

# Test connection
curl -k https://localhost:8443/health
```

### Automated Testing

Run the HTTPS configuration test suite:

```bash
python test_https_config.py
```

## Migration Guide

### Migrating from HTTP to HTTPS

1. **Generate or Obtain Certificates**
   ```bash
   ./examples/generate-ssl-certs.sh ./ssl
   ```

2. **Update Environment Variables**
   ```bash
   # Add to your docker run command or docker-compose.yml
   -e HTTPS_ENABLED=true
   -e SSL_CERT=/ssl/cert.pem
   -e SSL_KEY=/ssl/key.pem
   ```

3. **Mount Certificate Directory**
   ```bash
   -v $(pwd)/ssl:/ssl:ro
   ```

4. **Update Port Mapping**
   ```bash
   # Change from:
   -p 5000:5000
   
   # To:
   -p 443:5000
   ```

5. **Restart Container**
   ```bash
   docker-compose down
   docker-compose up -d
   ```

6. **Verify HTTPS is Working**
   ```bash
   curl -k https://localhost:443/health
   ```

### Rolling Back to HTTP

Simply remove or set `HTTPS_ENABLED=false` and restart the container.

## References

- [Gunicorn SSL Documentation](https://docs.gunicorn.org/en/stable/settings.html#ssl)
- [Let's Encrypt Documentation](https://letsencrypt.org/docs/)
- [Mozilla SSL Configuration Generator](https://ssl-config.mozilla.org/)
- [SSL Labs Server Test](https://www.ssllabs.com/ssltest/)
- [OWASP TLS Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Transport_Layer_Protection_Cheat_Sheet.html)

## Support

For issues or questions about HTTPS support:

1. Check this documentation
2. Review the [README.md](../README.md)
3. Check existing GitHub issues
4. Create a new GitHub issue with:
   - Docker version
   - Container logs
   - HTTPS configuration (certificate paths, environment variables)
   - Error messages
