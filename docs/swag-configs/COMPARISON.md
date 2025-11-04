# SWAG Configuration Comparison: Subdomain vs Subfolder

This document compares the two deployment methods for ComicMaintainer with SWAG.

## Quick Comparison

| Feature | Subdomain | Subfolder |
|---------|-----------|-----------|
| **URL** | `https://comics.yourdomain.com` | `https://yourdomain.com/comics` |
| **DNS Required** | Yes (CNAME or A record) | No (uses main domain) |
| **BASE_PATH Setting** | Not required | **Required** (`BASE_PATH=/comics`) |
| **Config File** | `comicmaintainer.subdomain.conf` | `comicmaintainer.subfolder.conf` |
| **SSL Certificate** | Automatic (Let's Encrypt) | Uses main domain cert |
| **Complexity** | Simple | Slightly more complex |
| **Recommended For** | Dedicated comic management | Multiple services on one domain |

## Subdomain Deployment

### When to Use
- ✅ You want a dedicated subdomain for ComicMaintainer
- ✅ You're comfortable managing DNS records
- ✅ You want the simplest configuration
- ✅ You plan to use multiple subdomains for different services

### Configuration

**DNS Setup:**
```
Type: CNAME
Name: comics
Value: yourdomain.com
```

**ComicMaintainer Environment:**
```yaml
environment:
  - WATCHED_DIR=/watched_dir
  - PUID=1000
  - PGID=1000
  # No BASE_PATH needed
```

**SWAG Config Location:**
```
/path/to/swag/config/nginx/proxy-confs/comicmaintainer.subdomain.conf
```

**Access URL:**
```
https://comics.yourdomain.com
```

### Key Config Lines

```nginx
server {
    listen 443 ssl http2;
    server_name comics.*;  # Matches any subdomain starting with 'comics'
    
    location / {
        proxy_pass http://comicmaintainer:5000;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_buffering off;
        proxy_read_timeout 600s;
    }
}
```

## Subfolder Deployment

### When to Use
- ✅ You want to avoid managing DNS records
- ✅ You want multiple services under one domain
- ✅ You're already using the domain for other services
- ✅ You prefer path-based routing

### Configuration

**DNS Setup:**
```
No additional DNS required - uses main domain
```

**ComicMaintainer Environment:**
```yaml
environment:
  - WATCHED_DIR=/watched_dir
  - BASE_PATH=/comics  # REQUIRED!
  - PUID=1000
  - PGID=1000
```

**SWAG Config Location:**
```
/path/to/swag/config/nginx/proxy-confs/comicmaintainer.subfolder.conf
```

**Access URL:**
```
https://yourdomain.com/comics
```

### Key Config Lines

```nginx
location /comics {
    return 301 $scheme://$host/comics/;  # Redirect to trailing slash
}

location ^~ /comics/ {
    proxy_pass http://comicmaintainer:5000/;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Prefix /comics;  # Critical for subfolder
    proxy_buffering off;
    proxy_read_timeout 600s;
}
```

## Common Configuration Elements

Both configurations include the same essential settings:

### WebSocket/SSE Support
```nginx
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
proxy_http_version 1.1;
```

### Timeouts for Batch Operations
```nginx
proxy_connect_timeout 600s;
proxy_send_timeout 600s;
proxy_read_timeout 600s;
send_timeout 600s;
```

### Server-Sent Events Settings
```nginx
proxy_buffering off;
proxy_cache off;
```

### Container Reference
```nginx
set $upstream_app comicmaintainer;
set $upstream_port 5000;
set $upstream_proto http;
```

## Authentication

Both configurations support the same authentication methods:

### HTTP Basic Auth
```nginx
auth_basic "Restricted";
auth_basic_user_file /config/nginx/.htpasswd;
```

### Authelia
```nginx
include /config/nginx/authelia-server.conf;  # In server block
include /config/nginx/authelia-location.conf;  # In location block
```

### Authentik
```nginx
include /config/nginx/authentik-server.conf;  # In server block
include /config/nginx/authentik-location.conf;  # In location block
```

### LDAP
```nginx
include /config/nginx/ldap-server.conf;  # In server block
include /config/nginx/ldap-location.conf;  # In location block
```

## Migration Between Methods

### From Subdomain to Subfolder

1. **Update ComicMaintainer:**
   ```yaml
   environment:
     - BASE_PATH=/comics  # Add this
   ```

2. **Copy new config:**
   ```bash
   cp comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/
   ```

3. **Remove old config:**
   ```bash
   rm /path/to/swag/config/nginx/proxy-confs/comicmaintainer.subdomain.conf
   ```

4. **Restart containers:**
   ```bash
   docker restart comicmaintainer swag
   ```

### From Subfolder to Subdomain

1. **Update ComicMaintainer:**
   ```yaml
   environment:
     # Remove BASE_PATH
   ```

2. **Configure DNS:**
   - Add CNAME: `comics.yourdomain.com → yourdomain.com`

3. **Copy new config:**
   ```bash
   cp comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/
   ```

4. **Remove old config:**
   ```bash
   rm /path/to/swag/config/nginx/proxy-confs/comicmaintainer.subfolder.conf
   ```

5. **Restart containers:**
   ```bash
   docker restart comicmaintainer swag
   ```

## Troubleshooting

### Subdomain Issues

**Problem: 502 Bad Gateway**
```bash
# Check DNS resolution
nslookup comics.yourdomain.com

# Check container name
docker ps | grep comicmaintainer

# Check network
docker network inspect swag-network
```

**Problem: Certificate errors**
```bash
# Check SWAG logs
docker logs swag | grep -i cert

# Verify subdomain in SWAG env
docker exec swag env | grep SUBDOMAINS
```

### Subfolder Issues

**Problem: 404 Not Found**
```bash
# Verify BASE_PATH is set
docker exec comicmaintainer env | grep BASE_PATH

# Should show: BASE_PATH=/comics
```

**Problem: Assets not loading**
```bash
# Check browser console for 404s
# Verify X-Forwarded-Prefix in config
docker exec swag cat /config/nginx/proxy-confs/comicmaintainer.subfolder.conf | grep X-Forwarded-Prefix
```

**Problem: Redirect loops**
```bash
# Ensure both location blocks exist:
# 1. location /comics (redirect to trailing slash)
# 2. location ^~ /comics/ (actual proxy)
```

## Best Practices

### Subdomain
- ✅ Use when you want clean, dedicated URLs
- ✅ Easier for users to remember
- ✅ Better for sharing links
- ✅ Simpler configuration

### Subfolder
- ✅ Use when consolidating services
- ✅ Saves on DNS records
- ✅ Good for internal tools
- ✅ Easier to manage multiple services

## Examples in Production

### Subdomain Example
```
Main site: https://yourdomain.com (website)
Comics:    https://comics.yourdomain.com (ComicMaintainer)
Books:     https://books.yourdomain.com (Calibre)
Media:     https://media.yourdomain.com (Plex)
```

### Subfolder Example
```
Main site: https://yourdomain.com (website)
Comics:    https://yourdomain.com/comics (ComicMaintainer)
Books:     https://yourdomain.com/books (Calibre)
Media:     https://yourdomain.com/media (Plex)
```

## Summary

**Choose Subdomain if:**
- You want the simplest setup
- You're comfortable with DNS
- You want dedicated, memorable URLs

**Choose Subfolder if:**
- You want to minimize DNS management
- You're hosting multiple services
- You prefer path-based organization

Both methods are fully supported and work equally well!
