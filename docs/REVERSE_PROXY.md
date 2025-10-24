# Reverse Proxy Deployment Guide

This guide explains how to deploy ComicMaintainer behind a reverse proxy (nginx, Apache, Traefik, etc.).

## Table of Contents

- [Overview](#overview)
- [Why Use a Reverse Proxy?](#why-use-a-reverse-proxy)
- [Requirements](#requirements)
- [Nginx Configuration](#nginx-configuration)
- [Apache Configuration](#apache-configuration)
- [Traefik Configuration](#traefik-configuration)
- [Path Prefix Deployments](#path-prefix-deployments)
- [Troubleshooting](#troubleshooting)

## Overview

ComicMaintainer is designed to work seamlessly behind a reverse proxy. The application includes built-in support for:

- **X-Forwarded-* Headers**: Properly handles proxy headers for correct URL generation
- **Path Prefixes**: Supports deployment at subpaths (e.g., `/comics/`)
- **Server-Sent Events**: SSE (real-time updates) work correctly through proxies
- **Long-Running Operations**: Handles batch processing operations that take several minutes

## Why Use a Reverse Proxy?

A reverse proxy provides several benefits:

1. **SSL/TLS Termination**: Handle HTTPS at the proxy level
2. **Multiple Applications**: Host multiple services on the same server/domain
3. **Load Balancing**: Distribute traffic across multiple backend instances
4. **Caching**: Cache static assets for better performance
5. **Security**: Add authentication, rate limiting, and other security features
6. **Centralized Logging**: Unified access logs for all services

## Requirements

### Proxy Headers

The application requires the following headers to be forwarded by the reverse proxy:

- `X-Forwarded-For`: Original client IP address
- `X-Forwarded-Proto`: Original protocol (http/https)
- `X-Forwarded-Host`: Original host header
- `X-Forwarded-Prefix`: URL path prefix (for subpath deployments)

### Server-Sent Events (SSE)

The application uses Server-Sent Events for real-time updates. The proxy must:

- **Disable buffering** for SSE endpoints (`/api/events/stream`)
- Allow **long-lived connections** (up to 5 minutes for SSE)
- Support **HTTP/1.1** with keep-alive

### Timeouts

Batch processing operations can take several minutes. Recommended timeouts:

- **Connection timeout**: 600s (10 minutes)
- **Send timeout**: 600s (10 minutes)
- **Read timeout**: 600s (10 minutes)

## Nginx Configuration

### Basic Configuration (Root Path)

Deploy at the root of a domain (e.g., `http://comicmaintainer.example.com/`):

```nginx
server {
    listen 80;
    server_name comicmaintainer.example.com;

    client_max_body_size 100M;

    location / {
        proxy_pass http://127.0.0.1:5000;
        
        # Required proxy headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Port $server_port;
        
        # SSE support
        proxy_http_version 1.1;
        proxy_buffering off;
        proxy_cache off;
        
        # Long-running operations
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
    }
}
```

### With Path Prefix

Deploy at a subpath (e.g., `http://example.com/comics/`):

```nginx
server {
    listen 80;
    server_name example.com;

    location /comics/ {
        proxy_pass http://127.0.0.1:5000/;
        
        # Required proxy headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Prefix /comics;  # Important!
        
        # SSE support
        proxy_http_version 1.1;
        proxy_buffering off;
        proxy_cache off;
        
        # Long-running operations
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
    }
}
```

### With HTTPS (Let's Encrypt)

```nginx
server {
    listen 443 ssl http2;
    server_name comicmaintainer.example.com;

    ssl_certificate /etc/letsencrypt/live/comicmaintainer.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/comicmaintainer.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    
    location / {
        proxy_pass http://127.0.0.1:5000;
        
        # Required proxy headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        
        # SSE support
        proxy_http_version 1.1;
        proxy_buffering off;
        proxy_cache off;
        
        # Long-running operations
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
    }
}

server {
    listen 80;
    server_name comicmaintainer.example.com;
    return 301 https://$server_name$request_uri;
}
```

For a complete nginx example, see [nginx-reverse-proxy-example.conf](nginx-reverse-proxy-example.conf).

## Apache Configuration

### Basic Configuration (Root Path)

```apache
<VirtualHost *:80>
    ServerName comicmaintainer.example.com
    
    ProxyPreserveHost On
    ProxyPass / http://127.0.0.1:5000/
    ProxyPassReverse / http://127.0.0.1:5000/
    
    # Required proxy headers
    RequestHeader set X-Forwarded-Proto "http"
    RequestHeader set X-Forwarded-Host "%{HTTP_HOST}e"
    
    # Long-running operations
    ProxyTimeout 600
    
    # SSE support - disable buffering
    <Location /api/events/stream>
        ProxyPass http://127.0.0.1:5000/api/events/stream disablereuse=on
        SetEnv proxy-nokeepalive 1
        SetEnv proxy-initial-not-pooled 1
    </Location>
</VirtualHost>
```

### With Path Prefix

```apache
<VirtualHost *:80>
    ServerName example.com
    
    ProxyPreserveHost On
    ProxyPass /comics/ http://127.0.0.1:5000/
    ProxyPassReverse /comics/ http://127.0.0.1:5000/
    
    # Required proxy headers
    RequestHeader set X-Forwarded-Proto "http"
    RequestHeader set X-Forwarded-Host "%{HTTP_HOST}e"
    RequestHeader set X-Forwarded-Prefix "/comics"
    
    # Long-running operations
    ProxyTimeout 600
    
    # SSE support
    <Location /comics/api/events/stream>
        ProxyPass http://127.0.0.1:5000/api/events/stream disablereuse=on
    </Location>
</VirtualHost>
```

### Required Apache Modules

```bash
sudo a2enmod proxy
sudo a2enmod proxy_http
sudo a2enmod headers
sudo systemctl restart apache2
```

## Traefik Configuration

### Docker Compose with Traefik

```yaml
version: '3.8'

services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    environment:
      - WATCHED_DIR=/watched_dir
      - WEB_PORT=5000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    labels:
      # Enable Traefik
      - "traefik.enable=true"
      
      # Router configuration
      - "traefik.http.routers.comicmaintainer.rule=Host(`comicmaintainer.example.com`)"
      - "traefik.http.routers.comicmaintainer.entrypoints=web"
      
      # Service configuration
      - "traefik.http.services.comicmaintainer.loadbalancer.server.port=5000"
      
      # Long timeout for batch operations
      - "traefik.http.services.comicmaintainer.loadbalancer.responseforwardingtimeouts=600s"
    networks:
      - traefik

  traefik:
    image: traefik:v2.10
    command:
      - "--api.insecure=true"
      - "--providers.docker=true"
      - "--entrypoints.web.address=:80"
    ports:
      - "80:80"
      - "8080:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    networks:
      - traefik

networks:
  traefik:
    driver: bridge
```

### With Path Prefix in Traefik

```yaml
labels:
  - "traefik.enable=true"
  - "traefik.http.routers.comicmaintainer.rule=Host(`example.com`) && PathPrefix(`/comics`)"
  - "traefik.http.routers.comicmaintainer.entrypoints=web"
  - "traefik.http.middlewares.comicmaintainer-prefix.stripprefix.prefixes=/comics"
  - "traefik.http.middlewares.comicmaintainer-headers.headers.customrequestheaders.X-Forwarded-Prefix=/comics"
  - "traefik.http.routers.comicmaintainer.middlewares=comicmaintainer-prefix,comicmaintainer-headers"
```

## Path Prefix Deployments

When deploying with a path prefix (e.g., `/comics/`), ensure:

1. **Set X-Forwarded-Prefix header** to the path prefix
2. **Strip the prefix** before forwarding to the backend
3. **All links remain relative** (the app already uses relative URLs)

Example URLs with prefix `/comics`:
- Web interface: `http://example.com/comics/`
- API endpoint: `http://example.com/comics/api/files`
- Health check: `http://example.com/comics/health`

## Troubleshooting

### Issue: Real-time updates don't work

**Symptoms**: Batch processing status doesn't update in real-time

**Solution**: Ensure SSE endpoint is not buffered:

**Nginx:**
```nginx
location /api/events/stream {
    proxy_pass http://127.0.0.1:5000/api/events/stream;
    proxy_buffering off;
    proxy_cache off;
}
```

**Apache:**
```apache
<Location /api/events/stream>
    ProxyPass http://127.0.0.1:5000/api/events/stream disablereuse=on
</Location>
```

### Issue: Batch processing times out

**Symptoms**: "504 Gateway Timeout" during batch operations

**Solution**: Increase proxy timeouts to at least 600 seconds (10 minutes)

**Nginx:**
```nginx
proxy_connect_timeout 600s;
proxy_send_timeout 600s;
proxy_read_timeout 600s;
```

**Apache:**
```apache
ProxyTimeout 600
```

### Issue: Links/redirects go to wrong URL

**Symptoms**: Clicking links navigates to wrong domain or path

**Solution**: Ensure all proxy headers are set correctly, especially:
- `X-Forwarded-Host`: Should be the public hostname
- `X-Forwarded-Proto`: Should be `https` if using HTTPS
- `X-Forwarded-Prefix`: Should be the path prefix (if using subpath)

### Issue: Cannot access through proxy

**Symptoms**: Connection refused or 502 Bad Gateway

**Solution**: 
1. Verify ComicMaintainer is running: `curl http://localhost:5000/health`
2. Check proxy can reach backend: `curl -H "Host: example.com" http://127.0.0.1:5000/health`
3. Review proxy error logs for details

### Issue: CORS errors in browser

**Symptoms**: Browser console shows CORS errors

**Solution**: This shouldn't happen with proper reverse proxy setup. Ensure:
1. Proxy headers are set correctly
2. Frontend requests use relative URLs (already configured)
3. No additional CORS middleware is interfering

## Testing Your Setup

After configuring the reverse proxy, test the following:

1. **Health Check**: `curl https://comicmaintainer.example.com/health`
2. **Web Interface**: Open in browser and verify page loads
3. **API Access**: Test API endpoint `curl https://comicmaintainer.example.com/api/version`
4. **Real-time Updates**: Start a batch processing job and verify progress updates work
5. **Long Operations**: Process a large library and ensure it completes without timeout

## Security Considerations

When deploying behind a reverse proxy:

1. **Bind to localhost**: Configure ComicMaintainer to bind to `127.0.0.1:5000` instead of `0.0.0.0:5000` to prevent direct access
2. **Use HTTPS**: Always use HTTPS for production deployments
3. **Authentication**: Consider adding authentication at the proxy level (basic auth, OAuth, etc.)
4. **Rate Limiting**: Implement rate limiting to prevent abuse
5. **Firewall Rules**: Configure firewall to allow only proxy traffic to reach the backend

## Additional Resources

- [Nginx Proxy Configuration](https://docs.nginx.com/nginx/admin-guide/web-server/reverse-proxy/)
- [Apache Reverse Proxy Guide](https://httpd.apache.org/docs/2.4/howto/reverse_proxy.html)
- [Traefik Docker Provider](https://doc.traefik.io/traefik/providers/docker/)
- [Flask ProxyFix Documentation](https://werkzeug.palletsprojects.com/en/2.3.x/middleware/proxy_fix/)

## Need Help?

If you encounter issues:

1. Check the [Troubleshooting](#troubleshooting) section above
2. Review proxy error logs
3. Enable debug mode: `DEBUG_MODE=true` in ComicMaintainer
4. Open an issue on GitHub with:
   - Proxy configuration
   - Error messages from both proxy and ComicMaintainer logs
   - Browser console errors (if web interface issue)
