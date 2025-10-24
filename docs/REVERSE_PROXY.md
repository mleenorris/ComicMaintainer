# Reverse Proxy Setup Guide

This guide explains how to deploy ComicMaintainer behind a reverse proxy for secure external access.

> **Note**: ComicMaintainer also supports native HTTPS without a reverse proxy. See the [HTTPS Setup Guide](HTTPS_SETUP.md) for direct HTTPS configuration. This guide focuses on using reverse proxies, which is recommended for production deployments.

## Table of Contents
- [Why Use a Reverse Proxy?](#why-use-a-reverse-proxy)
- [Configuration Options](#configuration-options)
- [Nginx Configuration](#nginx-configuration)
- [Traefik Configuration](#traefik-configuration)
- [Apache Configuration](#apache-configuration)
- [Caddy Configuration](#caddy-configuration)
- [Testing Your Setup](#testing-your-setup)
- [Troubleshooting](#troubleshooting)

## Why Use a Reverse Proxy?

A reverse proxy provides several benefits:
- **HTTPS/SSL Termination**: Secure your connection with TLS certificates
- **Domain/Subdomain Routing**: Access via a friendly domain name (e.g., comics.example.com)
- **Path-based Routing**: Serve from a subdirectory (e.g., example.com/comics)
- **Access Control**: Add authentication layers
- **Rate Limiting**: Protect against abuse
- **Load Balancing**: Distribute traffic across multiple instances

## Configuration Options

ComicMaintainer supports reverse proxy deployment with the following features:

### Automatic Proxy Header Handling
The application automatically respects standard reverse proxy headers:
- `X-Forwarded-For`: Client IP address
- `X-Forwarded-Proto`: Original protocol (http/https)
- `X-Forwarded-Host`: Original hostname
- `X-Forwarded-Prefix`: Path prefix for subdirectory deployments

### Base Path Support
You can deploy ComicMaintainer at a subdirectory path using the `BASE_PATH` environment variable:

```bash
docker run -d \
  -e BASE_PATH=/comics \
  -e WATCHED_DIR=/watched_dir \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Important**: `BASE_PATH` must start with a forward slash (e.g., `/comics`, not `comics`).

### PWA and Offline Support
ComicMaintainer includes Progressive Web App (PWA) features that work seamlessly with reverse proxy deployments:

- **Dynamic Asset Paths**: All static assets (icons, manifest, service worker) automatically adjust to your BASE_PATH
- **Offline Caching**: Service worker caches static assets for offline access
- **Installable**: Users can install the app on their devices from any reverse proxy URL

The application automatically handles:
- Manifest.json generation with correct paths for your deployment
- Service worker registration with BASE_PATH awareness
- All API and static asset URLs relative to your configured path

No additional configuration is needed - PWA features work out of the box with both root path and subdirectory deployments.

## Nginx Configuration

### Root Path Deployment
Deploy at a domain root (e.g., `comics.example.com`):

```nginx
server {
    listen 80;
    server_name comics.example.com;
    
    # Redirect HTTP to HTTPS
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name comics.example.com;
    
    # SSL Configuration
    ssl_certificate /etc/nginx/ssl/comics.example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/comics.example.com.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    
    # Optional: Basic Authentication
    # auth_basic "Comic Manager";
    # auth_basic_user_file /etc/nginx/.htpasswd;
    
    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        
        # Standard proxy headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        
        # WebSocket/SSE support
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        
        # Timeouts for long-running operations
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
        send_timeout 600s;
        
        # Buffering settings for SSE
        proxy_buffering off;
        proxy_cache off;
    }
}
```

### Subdirectory Deployment
Deploy at a subdirectory (e.g., `example.com/comics`):

**Docker Configuration:**
```bash
docker run -d \
  -e BASE_PATH=/comics \
  -e WATCHED_DIR=/watched_dir \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Nginx Configuration:**
```nginx
server {
    listen 443 ssl http2;
    server_name example.com;
    
    # SSL Configuration
    ssl_certificate /etc/nginx/ssl/example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/example.com.key;
    
    location /comics/ {
        proxy_pass http://localhost:5000/;
        proxy_http_version 1.1;
        
        # Standard proxy headers
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Prefix /comics;
        
        # WebSocket/SSE support
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        
        # Timeouts for long-running operations
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
        
        # Buffering settings for SSE
        proxy_buffering off;
        proxy_cache off;
    }
}
```

## Traefik Configuration

Traefik v2+ with Docker labels:

### Root Path Deployment

**docker-compose.yml:**
```yaml
version: '3.8'

services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    container_name: comictagger-watcher
    environment:
      - WATCHED_DIR=/watched_dir
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    labels:
      # Enable Traefik
      - "traefik.enable=true"
      
      # HTTP router
      - "traefik.http.routers.comics.rule=Host(`comics.example.com`)"
      - "traefik.http.routers.comics.entrypoints=web"
      
      # Redirect HTTP to HTTPS
      - "traefik.http.middlewares.comics-https.redirectscheme.scheme=https"
      - "traefik.http.routers.comics.middlewares=comics-https"
      
      # HTTPS router
      - "traefik.http.routers.comics-secure.rule=Host(`comics.example.com`)"
      - "traefik.http.routers.comics-secure.entrypoints=websecure"
      - "traefik.http.routers.comics-secure.tls=true"
      - "traefik.http.routers.comics-secure.tls.certresolver=letsencrypt"
      
      # Service definition
      - "traefik.http.services.comics.loadbalancer.server.port=5000"
      
      # Optional: Basic Authentication
      # Generate with: htpasswd -nb username password
      # - "traefik.http.middlewares.comics-auth.basicauth.users=user:$$apr1$$..."
      # - "traefik.http.routers.comics-secure.middlewares=comics-auth"
    networks:
      - traefik

networks:
  traefik:
    external: true
```

### Subdirectory Deployment

**docker-compose.yml:**
```yaml
version: '3.8'

services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:latest
    container_name: comictagger-watcher
    environment:
      - WATCHED_DIR=/watched_dir
      - BASE_PATH=/comics
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.comics.rule=Host(`example.com`) && PathPrefix(`/comics`)"
      - "traefik.http.routers.comics.entrypoints=websecure"
      - "traefik.http.routers.comics.tls=true"
      - "traefik.http.services.comics.loadbalancer.server.port=5000"
      
      # Strip prefix middleware (if needed)
      - "traefik.http.middlewares.comics-stripprefix.stripprefix.prefixes=/comics"
      # Don't use stripprefix if BASE_PATH is set correctly
    networks:
      - traefik

networks:
  traefik:
    external: true
```

## Apache Configuration

### Root Path Deployment

**apache-comics.conf:**
```apache
<VirtualHost *:80>
    ServerName comics.example.com
    
    # Redirect HTTP to HTTPS
    Redirect permanent / https://comics.example.com/
</VirtualHost>

<VirtualHost *:443>
    ServerName comics.example.com
    
    # SSL Configuration
    SSLEngine on
    SSLCertificateFile /etc/apache2/ssl/comics.example.com.crt
    SSLCertificateKeyFile /etc/apache2/ssl/comics.example.com.key
    
    # Optional: Basic Authentication
    # <Location />
    #     AuthType Basic
    #     AuthName "Comic Manager"
    #     AuthUserFile /etc/apache2/.htpasswd
    #     Require valid-user
    # </Location>
    
    # Enable required modules
    # a2enmod proxy proxy_http proxy_wstunnel headers
    
    # Reverse Proxy Configuration
    ProxyPreserveHost On
    ProxyRequests Off
    
    # Set proxy headers
    RequestHeader set X-Forwarded-Proto "https"
    RequestHeader set X-Forwarded-Host "comics.example.com"
    
    # WebSocket/SSE support
    ProxyPass / http://localhost:5000/ upgrade=websocket
    ProxyPassReverse / http://localhost:5000/
    
    # Timeout settings
    ProxyTimeout 600
    
    # Error logging
    ErrorLog ${APACHE_LOG_DIR}/comics-error.log
    CustomLog ${APACHE_LOG_DIR}/comics-access.log combined
</VirtualHost>
```

### Subdirectory Deployment

**apache-comics.conf:**
```apache
<VirtualHost *:443>
    ServerName example.com
    
    # SSL Configuration
    SSLEngine on
    SSLCertificateFile /etc/apache2/ssl/example.com.crt
    SSLCertificateKeyFile /etc/apache2/ssl/example.com.key
    
    <Location /comics>
        # Set proxy headers
        RequestHeader set X-Forwarded-Proto "https"
        RequestHeader set X-Forwarded-Host "example.com"
        RequestHeader set X-Forwarded-Prefix "/comics"
        
        # Reverse proxy
        ProxyPass http://localhost:5000/
        ProxyPassReverse http://localhost:5000/
        
        # WebSocket/SSE support
        ProxyPass upgrade=websocket
        
        ProxyTimeout 600
    </Location>
</VirtualHost>
```

**Docker command:**
```bash
docker run -d \
  -e BASE_PATH=/comics \
  -e WATCHED_DIR=/watched_dir \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

## Caddy Configuration

Caddy automatically handles HTTPS with Let's Encrypt:

### Root Path Deployment

**Caddyfile:**
```caddy
comics.example.com {
    # Caddy automatically handles HTTPS with Let's Encrypt
    
    # Optional: Basic Authentication
    # basicauth {
    #     user $2a$14$hash...
    # }
    
    reverse_proxy localhost:5000 {
        # Headers are automatically set by Caddy
        
        # WebSocket/SSE support
        header_up Upgrade {http.request.header.Upgrade}
        header_up Connection {http.request.header.Connection}
        
        # Timeouts
        transport http {
            dial_timeout 600s
            response_header_timeout 600s
        }
    }
}
```

### Subdirectory Deployment

**Caddyfile:**
```caddy
example.com {
    handle /comics/* {
        reverse_proxy localhost:5000 {
            # Caddy will automatically set X-Forwarded-Prefix
            
            # WebSocket/SSE support
            header_up Upgrade {http.request.header.Upgrade}
            header_up Connection {http.request.header.Connection}
        }
    }
}
```

**Docker command:**
```bash
docker run -d \
  -e BASE_PATH=/comics \
  -e WATCHED_DIR=/watched_dir \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

## Testing Your Setup

### 1. Check Basic Connectivity
```bash
curl -I https://comics.example.com
# Should return 200 OK
```

### 2. Test API Endpoints
```bash
curl https://comics.example.com/api/version
# Should return: {"version": "x.x.x"}
```

### 3. Test SSE Connection
Open browser developer console on your site and check:
```javascript
// Should show successful SSE connection
// Network tab -> Look for /api/events/stream with status 200
```

### 4. Verify Headers
Check that your reverse proxy is setting headers correctly:
```bash
# From inside the container
docker exec comictagger-watcher cat /Config/Log/ComicMaintainer.log | grep "X-Forwarded"
```

## Troubleshooting

### Issue: 404 Not Found or 502 Bad Gateway
**Solution:**
- Verify ComicMaintainer is running: `docker ps | grep comictagger`
- Check container logs: `docker logs comictagger-watcher`
- Verify port mapping is correct (5000:5000)
- Check reverse proxy is forwarding to the correct port

### Issue: SSE/Real-time Updates Not Working
**Solution:**
- Ensure proxy buffering is disabled (`proxy_buffering off` in Nginx)
- Check WebSocket/SSE headers are being forwarded
- Verify timeouts are long enough (600+ seconds)
- Check browser console for SSE connection errors

### Issue: Assets Not Loading (CSS/JS)
**Solution:**
- If using subdirectory deployment, ensure `BASE_PATH` is set correctly
- Verify `X-Forwarded-Prefix` header is being sent
- Check browser console for 404 errors on static files
- Clear browser cache

### Issue: Redirect Loops or Wrong URLs
**Solution:**
- Ensure `X-Forwarded-Proto` is set correctly (https if using SSL)
- Check `X-Forwarded-Host` matches your domain
- Verify ProxyFix middleware is working (already enabled by default)

### Issue: Authentication Required But Not Showing
**Solution:**
- Check your reverse proxy authentication configuration
- Verify the auth middleware is enabled
- Test basic auth: `curl -u user:pass https://comics.example.com`

### Issue: Slow Performance Behind Proxy
**Solution:**
- Enable HTTP/2 in your proxy configuration
- Check proxy timeout settings aren't too aggressive
- Verify no unnecessary buffering is enabled
- Check network latency between proxy and container

## Security Best Practices

1. **Always Use HTTPS**: Configure SSL/TLS certificates for external access
2. **Enable Authentication**: Add basic auth or OAuth at the proxy level
3. **Restrict Access**: Use firewall rules to limit who can access the proxy
4. **Set Proper Headers**: Ensure security headers (HSTS, CSP) are configured
5. **Monitor Logs**: Regularly check both proxy and application logs
6. **Keep Updated**: Update both the reverse proxy and ComicMaintainer regularly

## Additional Resources

- [Nginx Reverse Proxy Documentation](https://docs.nginx.com/nginx/admin-guide/web-server/reverse-proxy/)
- [Traefik Documentation](https://doc.traefik.io/traefik/)
- [Apache mod_proxy Documentation](https://httpd.apache.org/docs/current/mod/mod_proxy.html)
- [Caddy Reverse Proxy Guide](https://caddyserver.com/docs/quick-starts/reverse-proxy)
- [OWASP Reverse Proxy Best Practices](https://owasp.org/www-community/controls/Reverse_Proxy)
