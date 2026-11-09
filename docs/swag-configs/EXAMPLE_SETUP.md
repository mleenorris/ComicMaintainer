# Complete SWAG + ComicMaintainer Setup Example

A step-by-step setup of SWAG and ComicMaintainer on a subdomain, from one compose file.

```
Internet
    ↓
Your Router (Port Forwarding: 80 → Server:80, 443 → Server:443)
    ↓
SWAG Container (nginx + Let's Encrypt)
    ↓
ComicMaintainer Container
    ↓
Your Comics Directory
```

## docker-compose.yml

```yaml
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
      - VALIDATION=http              # or 'dns' with a DNS API
      - EMAIL=your-email@example.com # For Let's Encrypt
    volumes:
      - ~/swag/config:/config
    ports:
      - 443:443
      - 80:80
    networks:
      - swag-network
    restart: unless-stopped

  # ComicMaintainer
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - PUID=1000                    # The user that owns ~/comics
      - PGID=1000
      - TRUSTED_PROXIES=172.30.0.0/24  # The network below: gives each client its own sign-in limit
      # Add BASE_PATH=/comics for a subfolder setup, with comicmaintainer.subfolder.conf
    volumes:
      - ~/comics:/watched_dir
      - ~/duplicates:/duplicates
      - ~/comicmaintainer-config:/Config
    networks:
      - swag-network
    # Port 5000 is not published to the host: it is reachable only through SWAG
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    name: swag-network
    driver: bridge
    ipam:
      config:
        - subnet: 172.30.0.0/24
```

## Files on the Host

```
~/
├── docker-compose.yml
├── swag/
│   └── config/
│       └── nginx/
│           └── proxy-confs/
│               └── comicmaintainer.subdomain.conf
├── comics/                     # Your library
│   ├── Series 1/
│   │   ├── issue1.cbz
│   │   └── issue2.cbz
│   └── Series 2/
│       └── issue1.cbr
├── duplicates/                 # Files flagged as duplicates are moved here
└── comicmaintainer-config/     # Created and filled by the app
    ├── comicmaintainer.db      # The database
    ├── backups/                # Automatic database backups
    ├── user-settings.json      # Settings saved in the web interface
    └── app.log, auth.log, ...  # Logs
```

## Setup

### Step 1: Create the Folders

```bash
mkdir -p ~/swag/config ~/comics ~/duplicates ~/comicmaintainer-config
```

`PUID` and `PGID` above must be the user and group that can write to `~/comics` and `~/duplicates`. Find yours with `id -u` and `id -g`. ComicMaintainer checks this at start and logs a warning if it cannot write there.

### Step 2: Configure DNS

Create a CNAME record, or an A record pointing to your server's IP:

```
Type: CNAME
Name: comics
Value: yourdomain.com
```

### Step 3: Start SWAG

```bash
docker compose up -d swag

# Wait for certificate generation. You should see: "Server ready"
docker compose logs -f swag
```

### Step 4: Copy the ComicMaintainer Config

```bash
git clone --branch dotnet --depth 1 https://github.com/mleenorris/ComicMaintainer.git /tmp/comicmaintainer

cp /tmp/comicmaintainer/docs/swag-configs/comicmaintainer.subdomain.conf \
   ~/swag/config/nginx/proxy-confs/

docker restart swag
```

### Step 5: Start ComicMaintainer

```bash
docker compose up -d comicmaintainer
docker compose ps
```

### Step 6: Create the First Administrator

Open `https://comics.yourdomain.com`. A new install has no accounts, so the setup page opens. It asks for a one-time setup code, which is in the log:

```bash
docker logs comicmaintainer 2>&1 | grep -i "setup code"
```

## Verification Checklist

- [ ] Both containers are running: `docker compose ps`
- [ ] Both are on the network: `docker network inspect swag-network`
- [ ] DNS resolves: `nslookup comics.yourdomain.com`
- [ ] The app answers through the proxy: `curl https://comics.yourdomain.com/api/version`
- [ ] The web interface loads and shows your comics after you sign in
- [ ] Real-time updates work: in the browser's developer tools, the Network tab shows `/api/events/stream` with status 200

## Troubleshooting

### SWAG Won't Generate Certificates

```bash
# DNS should return your server's IP
nslookup comics.yourdomain.com

# Look for errors
docker logs swag 2>&1 | grep -i "error\|fail"
```

Also check from another network that port 80 reaches your server.

### 502 Bad Gateway

```bash
# From inside SWAG. Should return: {"version":"..."}
docker exec swag curl -s http://comicmaintainer:5000/api/version

docker logs comicmaintainer
```

### Real-Time Updates Not Working

```bash
# Should show: proxy_buffering off;
docker exec swag grep buffering /config/nginx/proxy-confs/comicmaintainer.subdomain.conf
```

## Adding a Sign-In in Front of the App

ComicMaintainer has its own sign-in. To add two-factor authentication and single sign-on, see the [Authelia Integration Guide](../AUTHELIA.md).

Do not enable the `auth_basic` lines in the config file: they stop the app from working, as the [SWAG README](README.md#http-basic) explains.

## Updating ComicMaintainer

```bash
docker compose pull comicmaintainer
docker compose up -d comicmaintainer
docker compose logs -f comicmaintainer
```

The database is backed up automatically before an update changes it.

## Backups

Back up these folders:

- `~/comicmaintainer-config/backups/`: consistent copies of the database, one a day by default
- `~/comicmaintainer-config/`, while the container is stopped: settings, keys and logs
- `~/swag/config/`: SWAG configuration and certificates
- `~/comics/`: your comic files

Do not copy `comicmaintainer.db` while the app is running. See [Backup Database](../DOCKER_DEPLOYMENT_DOTNET.md#backup-database).

## Performance

For large libraries on fast storage you can raise the number of files processed at once from the default of 4:

```yaml
environment:
  - AppSettings__MaxWorkers=8
```

See the [Performance Tuning Guide](../PERFORMANCE_TUNING.md).

## Support

- [ComicMaintainer Issues](https://github.com/mleenorris/ComicMaintainer/issues)
- [SWAG Documentation](https://docs.linuxserver.io/general/swag)
