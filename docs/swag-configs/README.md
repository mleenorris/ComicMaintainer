# SWAG Proxy Configuration for ComicMaintainer

This directory contains ready-to-use reverse proxy configuration files for [SWAG (Secure Web Application Gateway)](https://github.com/linuxserver/docker-swag) from linuxserver.io.

## What is SWAG?

SWAG is an all-in-one Docker container that includes:
- **Nginx** - High-performance web server and reverse proxy
- **Let's Encrypt** - Automatic SSL/TLS certificate generation and renewal
- **Fail2ban** - Protection against brute-force attacks
- **PHP** - Optional PHP support

SWAG makes it incredibly easy to expose your services securely with automatic HTTPS.

## Prerequisites

1. **SWAG container running** - Follow the [SWAG setup guide](https://docs.linuxserver.io/general/swag)
2. **Domain name** - You need a domain pointing to your server
3. **ComicMaintainer container** - Running and accessible on your Docker network

## Configuration Options

Choose one of the following deployment methods:

### Option 1: Subdomain Deployment (Recommended)

Access ComicMaintainer at: `https://comics.yourdomain.com`

**Configuration file:** `comicmaintainer.subdomain.conf`

### Option 2: Subfolder Deployment

Access ComicMaintainer at: `https://yourdomain.com/comics`

**Configuration file:** `comicmaintainer.subfolder.conf`

**Need help choosing?** See [COMPARISON.md](COMPARISON.md) for a detailed comparison of both methods.

## Installation Instructions

### Step 1: Set Up Docker Network

Ensure both SWAG and ComicMaintainer are on the same Docker network:

```bash
# Create a network if you don't have one
docker network create swag-network

# Your SWAG container should already be on this network
# Add ComicMaintainer to the same network
docker network connect swag-network comicmaintainer
```

### Step 2A: Subdomain Setup

**1. Configure DNS:**
Create a CNAME record pointing to your domain:
```
comics.yourdomain.com → yourdomain.com
```

Or create an A record pointing to your server's IP:
```
comics.yourdomain.com → YOUR.SERVER.IP.ADDRESS
```

**2. Copy the configuration file:**
```bash
# Copy the subdomain config to SWAG
cp comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/

# Or use docker cp if accessing SWAG container directly
docker cp comicmaintainer.subdomain.conf swag:/config/nginx/proxy-confs/
```

**3. Update docker-compose.yml for ComicMaintainer:**
```yaml
version: '3.8'

services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - PUID=1000
      - PGID=1000
      # No BASE_PATH needed for subdomain
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    networks:
      - swag-network
    # No need to expose port 5000 externally
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    external: true
```

**4. Restart SWAG:**
```bash
docker restart swag
```

**5. Access your service:**
Navigate to `https://comics.yourdomain.com`

### Step 2B: Subfolder Setup

**1. Copy the configuration file:**
```bash
# Copy the subfolder config to SWAG
cp comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/

# Or use docker cp if accessing SWAG container directly
docker cp comicmaintainer.subfolder.conf swag:/config/nginx/proxy-confs/
```

**2. Update docker-compose.yml for ComicMaintainer:**

**IMPORTANT:** You must set the `BASE_PATH` environment variable for subfolder deployment:

```yaml
version: '3.8'

services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - BASE_PATH=/comics  # REQUIRED for subfolder deployment
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    networks:
      - swag-network
    # No need to expose port 5000 externally
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    external: true
```

**3. Restart both containers:**
```bash
docker restart comicmaintainer
docker restart swag
```

**4. Access your service:**
Navigate to `https://yourdomain.com/comics`

## Authentication Options

Both configuration files support multiple authentication methods. Uncomment the relevant sections in the config file:

### HTTP Basic Authentication
```nginx
# In the location block, uncomment:
auth_basic "Restricted";
auth_basic_user_file /config/nginx/.htpasswd;
```

Then create the password file:
```bash
docker exec -it swag htpasswd -c /config/nginx/.htpasswd username
```

### Authelia
```nginx
# In the server block, uncomment:
include /config/nginx/authelia-server.conf;

# In the location block, uncomment:
include /config/nginx/authelia-location.conf;
```

### Authentik
```nginx
# In the server block, uncomment:
include /config/nginx/authentik-server.conf;

# In the location block, uncomment:
include /config/nginx/authentik-location.conf;
```

### LDAP
```nginx
# In the server block, uncomment:
include /config/nginx/ldap-server.conf;

# In the location block, uncomment:
include /config/nginx/ldap-location.conf;
```

## Troubleshooting

### Issue: 502 Bad Gateway

**Solution:**
1. Check that ComicMaintainer is running:
   ```bash
   docker ps | grep comicmaintainer
   ```
2. Verify the container name matches the config (`comicmaintainer`):
   ```bash
   docker inspect comicmaintainer | grep Name
   ```
3. Check both containers are on the same network:
   ```bash
   docker network inspect swag-network
   ```
4. Check SWAG logs:
   ```bash
   docker logs swag
   ```

### Issue: 404 Not Found (Subfolder Setup)

**Solution:**
1. Verify `BASE_PATH=/comics` is set in ComicMaintainer environment
2. Check ComicMaintainer logs:
   ```bash
   docker logs comicmaintainer | grep BASE_PATH
   ```
3. Restart ComicMaintainer after adding BASE_PATH:
   ```bash
   docker restart comicmaintainer
   ```

### Issue: Real-time Updates Not Working

**Solution:**
The configuration files already include the necessary settings for Server-Sent Events (SSE):
- `proxy_buffering off;`
- `proxy_cache off;`
- WebSocket upgrade headers

If real-time updates still don't work:
1. Check browser console for connection errors
2. Verify the timeout settings (already set to 600s)
3. Check SWAG error logs:
   ```bash
   docker logs swag 2>&1 | grep error
   ```

### Issue: Certificate Errors

**Solution:**
1. Verify your domain's DNS is correctly configured
2. Check SWAG certificate generation logs:
   ```bash
   docker logs swag | grep -i cert
   ```
3. Wait a few minutes for Let's Encrypt to issue the certificate
4. Ensure ports 80 and 443 are forwarded to your server

## Testing Your Configuration

### Test 1: Check Nginx Syntax
```bash
docker exec swag nginx -t
```

### Test 2: Check API Endpoint
```bash
curl https://comics.yourdomain.com/api/version
# Should return: {"version": "x.x.x"}
```

### Test 3: Check SSL Certificate
```bash
curl -I https://comics.yourdomain.com
# Should return 200 OK with valid SSL
```

### Test 4: Check Real-time Events
Open browser developer tools and check the Network tab for:
- `/api/events/stream` with status 200 and type "eventsource"

## Container Name Customization

If your ComicMaintainer container has a different name, update the config file:

```nginx
set $upstream_app your-container-name;  # Change this line
set $upstream_port 5000;
set $upstream_proto http;
```

## Additional Resources

- [SWAG Documentation](https://docs.linuxserver.io/general/swag)
- [ComicMaintainer Reverse Proxy Guide](../REVERSE_PROXY.md) - General reverse proxy documentation
- [Let's Encrypt Documentation](https://letsencrypt.org/docs/)
- [Nginx Reverse Proxy Guide](https://docs.nginx.com/nginx/admin-guide/web-server/reverse-proxy/)

## Support

If you encounter issues:
1. Check the [ComicMaintainer issues](https://github.com/mleenorris/ComicMaintainer/issues)
2. Check the [SWAG issues](https://github.com/linuxserver/docker-swag/issues)
3. Review the [Troubleshooting](#troubleshooting) section above

## Example Complete Setup

Here's a complete `docker-compose.yml` showing both SWAG and ComicMaintainer:

```yaml
version: '3.8'

services:
  swag:
    image: lscr.io/linuxserver/swag:latest
    container_name: swag
    cap_add:
      - NET_ADMIN
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=America/New_York
      - URL=yourdomain.com
      - SUBDOMAINS=comics  # For subdomain setup
      - VALIDATION=http
      - EMAIL=your-email@example.com
    volumes:
      - /path/to/swag/config:/config
    ports:
      - 443:443
      - 80:80
    networks:
      - swag-network
    restart: unless-stopped

  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - PUID=1000
      - PGID=1000
      # Add BASE_PATH=/comics only for subfolder setup
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    networks:
      - swag-network
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    driver: bridge
```

## Additional Documentation

- **[COMPARISON.md](COMPARISON.md)** - Detailed comparison of subdomain vs subfolder deployment
- **[EXAMPLE_SETUP.md](EXAMPLE_SETUP.md)** - Complete step-by-step setup with docker-compose examples
- **[../REVERSE_PROXY.md](../REVERSE_PROXY.md)** - General reverse proxy documentation

## Notes

- These configurations disable proxy buffering for proper SSE (Server-Sent Events) support
- Timeouts are set to 600 seconds (10 minutes) to support long-running batch operations
- WebSocket support is included for real-time updates
- The configurations follow SWAG's standard format and include optional authentication
- Both IPv4 and IPv6 are supported in the subdomain configuration
