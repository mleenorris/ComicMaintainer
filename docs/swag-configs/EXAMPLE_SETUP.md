# Complete SWAG + ComicMaintainer Setup Example

This document provides a complete, working example of a SWAG + ComicMaintainer setup.

## Architecture Overview

```
Internet
    ↓
Your Router (Port Forwarding: 80 → Server:80, 443 → Server:443)
    ↓
SWAG Container (Nginx + Let's Encrypt)
    ↓
ComicMaintainer Container
    ↓
Your Comics Directory
```

## Complete docker-compose.yml Example

This is a complete, production-ready setup:

```yaml
version: '3.8'

services:
  # SWAG - Secure Web Application Gateway
  swag:
    image: lscr.io/linuxserver/swag:latest
    container_name: swag
    cap_add:
      - NET_ADMIN
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=America/New_York
      - URL=yourdomain.com           # Your domain
      - SUBDOMAINS=comics            # Subdomain for ComicMaintainer
      - VALIDATION=http              # or 'dns' with DNS API
      - EMAIL=your-email@example.com # For Let's Encrypt
    volumes:
      - /path/to/swag/config:/config
    ports:
      - 443:443
      - 80:80
    networks:
      - swag-network
    restart: unless-stopped

  # ComicMaintainer
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - DUPLICATE_DIR=/duplicates
      - CONFIG_DIR=/Config
      - PUID=1000
      - PGID=1000
      - MAX_WORKERS=4
      - DB_CACHE_SIZE_MB=64
      # No BASE_PATH needed for subdomain setup
      # Add BASE_PATH=/comics for subfolder setup
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    networks:
      - swag-network
    # Don't expose port 5000 to host - only accessible via SWAG
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    driver: bridge
```

## Directory Structure

Your file system should look like this:

```
/home/user/
├── docker-compose.yml
├── swag/
│   └── config/
│       └── nginx/
│           └── proxy-confs/
│               └── comicmaintainer.subdomain.conf  # Your config file
├── comics/
│   ├── Series 1/
│   │   ├── issue1.cbz
│   │   └── issue2.cbz
│   └── Series 2/
│       └── issue1.cbr
├── duplicates/           # Optional: for duplicate files
└── comicmaintainer-config/  # Persistent data
    ├── store/
    │   └── comicmaintainer.db
    ├── Log/
    │   └── ComicMaintainer.log
    └── config.json
```

## Step-by-Step Setup

### Step 1: Prepare Directories

```bash
# Create all necessary directories
mkdir -p ~/swag/config
mkdir -p ~/comics
mkdir -p ~/duplicates
mkdir -p ~/comicmaintainer-config

# Set proper permissions (match PUID/PGID in docker-compose.yml)
sudo chown -R 1000:1000 ~/comics ~/duplicates ~/comicmaintainer-config
```

### Step 2: Configure DNS

**Option A: CNAME Record (Recommended)**
```
Type: CNAME
Name: comics
Value: yourdomain.com
TTL: 3600
```

**Option B: A Record**
```
Type: A
Name: comics
Value: YOUR.SERVER.IP.ADDRESS
TTL: 3600
```

### Step 3: Start SWAG First

```bash
# Start SWAG to generate initial config and certificates
docker-compose up -d swag

# Wait for certificate generation (check logs)
docker-compose logs -f swag

# You should see: "Server ready"
```

### Step 4: Copy ComicMaintainer Config

```bash
# Clone the repository to get config files
git clone https://github.com/mleenorris/ComicMaintainer.git /tmp/comicmaintainer

# Copy the SWAG config
cp /tmp/comicmaintainer/docs/swag-configs/comicmaintainer.subdomain.conf \
   ~/swag/config/nginx/proxy-confs/

# Restart SWAG to load the new config
docker restart swag
```

### Step 5: Start ComicMaintainer

```bash
# Start ComicMaintainer
docker-compose up -d comicmaintainer

# Verify it's running
docker-compose ps
```

### Step 6: Test Access

```bash
# Test from command line
curl -I https://comics.yourdomain.com

# Should return:
# HTTP/2 200
# server: nginx
# ...
```

## Verification Checklist

- [ ] SWAG container is running: `docker ps | grep swag`
- [ ] ComicMaintainer container is running: `docker ps | grep comicmaintainer`
- [ ] Both containers are on the same network: `docker network inspect swag-network`
- [ ] DNS is resolving: `nslookup comics.yourdomain.com`
- [ ] SSL certificate is valid: `curl -I https://comics.yourdomain.com`
- [ ] Web interface loads: Open `https://comics.yourdomain.com` in browser
- [ ] Can see comic files in the interface
- [ ] Real-time updates work (check browser console for SSE connection)

## Troubleshooting

### SWAG won't generate certificates

**Check DNS propagation:**
```bash
nslookup comics.yourdomain.com
# Should return your server's IP
```

**Check SWAG logs:**
```bash
docker logs swag 2>&1 | grep -i "error\|fail"
```

**Verify ports are forwarded:**
```bash
# From another network
curl http://yourdomain.com
# Should return Nginx response
```

### 502 Bad Gateway

**Check container connectivity:**
```bash
# Enter SWAG container
docker exec -it swag bash

# Try to reach ComicMaintainer
curl http://comicmaintainer:5000/api/version

# Should return: {"version": "x.x.x"}
```

**Check ComicMaintainer logs:**
```bash
docker logs comicmaintainer
```

### Real-time updates not working

**Check browser console:**
- Should see successful EventSource connection to `/api/events/stream`

**Verify config settings:**
```bash
docker exec swag cat /config/nginx/proxy-confs/comicmaintainer.subdomain.conf | grep buffering
# Should show: proxy_buffering off;
```

## Adding Authentication

To add HTTP Basic Authentication:

**1. Create password file:**
```bash
docker exec -it swag htpasswd -c /config/nginx/.htpasswd admin
# Enter password when prompted
```

**2. Edit config file:**
```bash
docker exec -it swag nano /config/nginx/proxy-confs/comicmaintainer.subdomain.conf

# Uncomment these lines in the location block:
auth_basic "Restricted";
auth_basic_user_file /config/nginx/.htpasswd;
```

**3. Restart SWAG:**
```bash
docker restart swag
```

## Updating ComicMaintainer

```bash
# Pull latest image
docker-compose pull comicmaintainer

# Recreate container
docker-compose up -d comicmaintainer

# Check logs
docker-compose logs -f comicmaintainer
```

## Backup Strategy

Important directories to backup:
- `~/comicmaintainer-config/` - Database, logs, settings
- `~/swag/config/` - SWAG configuration and certificates
- `~/comics/` - Your comic files (obviously!)

**Example backup script:**
```bash
#!/bin/bash
DATE=$(date +%Y%m%d)
tar -czf backup-comicmaintainer-${DATE}.tar.gz \
    ~/comicmaintainer-config/ \
    ~/swag/config/
```

## Performance Tuning

For large libraries (>5000 files):

**1. Increase database cache:**
```yaml
environment:
  - DB_CACHE_SIZE_MB=128  # Increase from default 64
```

**2. Increase workers:**
```yaml
environment:
  - MAX_WORKERS=8  # Increase from default 4
```

**3. Enable HTTP/2 (already enabled in SWAG by default)**

## Security Recommendations

1. ✅ Use strong passwords for authentication
2. ✅ Keep SWAG and ComicMaintainer updated
3. ✅ Enable fail2ban (included in SWAG)
4. ✅ Use firewall to restrict SSH access
5. ✅ Regular backups of configuration and data
6. ✅ Monitor logs for suspicious activity

## Support

- [ComicMaintainer Issues](https://github.com/mleenorris/ComicMaintainer/issues)
- [SWAG Documentation](https://docs.linuxserver.io/general/swag)
- [SWAG Discord](https://discord.gg/linuxserver)
