# SWAG Proxy Configuration for ComicMaintainer

This folder holds reverse proxy configuration files for [SWAG (Secure Web Application Gateway)](https://github.com/linuxserver/docker-swag), the linuxserver.io container that combines nginx, Let's Encrypt certificates and fail2ban.

For a guide to other reverse proxies, see the [Reverse Proxy Guide](../REVERSE_PROXY.md).

## Prerequisites

1. A running SWAG container ([setup guide](https://docs.linuxserver.io/general/swag))
2. A domain name that points to your server
3. ComicMaintainer running on the same Docker network as SWAG

## Choose a Deployment

| | Subdomain (recommended) | Subfolder |
|---|---|---|
| **Address** | `https://comics.yourdomain.com` | `https://yourdomain.com/comics` |
| **Config file** | `comicmaintainer.subdomain.conf` | `comicmaintainer.subfolder.conf` |
| **`BASE_PATH`** | Do not set it | Required: `BASE_PATH=/comics` |
| **DNS** | A record for the subdomain | None beyond the main domain |

[COMPARISON.md](COMPARISON.md) compares the two in more detail.

## Installation

### Step 1: Put Both Containers on One Network

```bash
# Create a network if you don't have one
docker network create swag-network

# Add both containers to it
docker network connect swag-network swag
docker network connect swag-network comicmaintainer
```

The config files expect the container to be named `comicmaintainer`. If yours has another name, change the `set $upstream_app` line in the file you copy.

### Step 2A: Subdomain

**1. Configure DNS.** Create a CNAME record pointing to your domain, or an A record pointing to your server's IP:

```
comics.yourdomain.com → yourdomain.com
```

**2. Copy the configuration file:**

```bash
# Run from the docs/swag-configs folder
cp comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/
```

**3. Run ComicMaintainer without `BASE_PATH`:**

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - PUID=1000                      # The user that owns your comic folders
      - PGID=1000
      - TRUSTED_PROXIES=172.18.0.0/16  # swag-network's subnet (see Trusted Proxies below)
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    networks:
      - swag-network
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    external: true
```

**4. Restart SWAG and open the address:**

```bash
docker restart swag
```

Then go to `https://comics.yourdomain.com`.

### Step 2B: Subfolder

**1. Copy the configuration file:**

```bash
# Run from the docs/swag-configs folder
cp comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/
```

**2. Run ComicMaintainer with `BASE_PATH`:**

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - BASE_PATH=/comics              # Must match the folder in the config file
      - PUID=1000                      # The user that owns your comic folders
      - PGID=1000
      - TRUSTED_PROXIES=172.18.0.0/16  # swag-network's subnet (see Trusted Proxies below)
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    networks:
      - swag-network
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    external: true
```

**3. Restart both containers and open the address:**

```bash
docker restart comicmaintainer swag
```

Then go to `https://yourdomain.com/comics`.

`BASE_PATH` must be the real subfolder. It must also stay unset when the app is served at the root of a domain: if it is set there, every URL the app generates points at a folder that does not exist. See [Serving Under a Subfolder](../REVERSE_PROXY.md#serving-under-a-subfolder-base_path).

The file passes requests on with `/comics` still on them, and ComicMaintainer removes the prefix itself. The `proxy_pass` line must therefore end at the port, with no `/` after it.

### First Sign-In

A new install has no accounts. Open the address and create the first administrator on the setup page. It asks for a one-time setup code, which is printed in the log until the first account exists:

```bash
docker logs comicmaintainer 2>&1 | grep -i "setup code"
```

You can also set your own code with `SETUP_TOKEN`, or create the administrator with `ADMIN_USERNAME` and `ADMIN_PASSWORD`. See the [Docker Deployment Guide](../DOCKER_DEPLOYMENT_DOTNET.md#first-administrator).

## Trusted Proxies

Every request reaches ComicMaintainer from SWAG's address. Without `TRUSTED_PROXIES`, all clients share one limit of 10 sign-in attempts per 5 minutes, and all signed-out requests share one limit of 100 a minute, so a single client can stop everyone from signing in.

Set `TRUSTED_PROXIES` to the subnet of `swag-network`, or to SWAG's fixed IP address:

```bash
docker network inspect swag-network --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}'
```

Requests from SWAG are then counted per client, using the address SWAG's `proxy.conf` appends to `X-Forwarded-For`. With Authelia enabled, `AUTHELIA_TRUSTED_PROXIES` already covers this. See [Trusted Proxies and Rate Limiting](../REVERSE_PROXY.md#trusted-proxies-and-rate-limiting).

## Adding a Sign-In in Front of the App

ComicMaintainer has its own sign-in, so this is optional.

### Authelia

Authelia adds two-factor authentication and single sign-on, and ComicMaintainer signs people in from its headers.

In the SWAG config, uncomment:

```nginx
# In the server block (subdomain) or in SWAG's default site (subfolder):
include /config/nginx/authelia-server.conf;

# In the location block:
include /config/nginx/authelia-location.conf;
```

In the ComicMaintainer container, set:

```yaml
environment:
  - AUTHELIA_ENABLED=true
  - AUTHELIA_TRUSTED_PROXIES=172.18.0.0/16  # Required: SWAG's address or its Docker network subnet
  - AUTHELIA_ADMIN_GROUPS=admins            # Groups that get the Admin role
  - AUTHELIA_URL=http://authelia:9091       # Optional: log Authelia outages
```

The container refuses to start without `AUTHELIA_TRUSTED_PROXIES`. If you use the OPDS catalog, Authelia also needs a bypass rule for it. The [Authelia Integration Guide](../AUTHELIA.md) covers both.

### Authentik and LDAP

The config files have commented-out lines for SWAG's Authentik and LDAP includes. These put a sign-in in front of the app, but ComicMaintainer does not read their headers, so people still sign in to ComicMaintainer afterwards. OPDS apps need the same exception as under Authelia, because they cannot follow a sign-in page.

### HTTP Basic

The commented-out `auth_basic` lines are SWAG's standard template and do not work with this app. ComicMaintainer sends its session token in the `Authorization` header, and nginx refuses any request whose `Authorization` header is not Basic credentials, so the app stops working after you sign in. Use Authelia, or ComicMaintainer's own sign-in, instead.

## Caching

Comic pages and covers (`/api/comicreader/page`, `/api/comicreader/cover`) are sent with `Cache-Control: private`, because they are behind authentication. Do not add a `proxy_cache` or a CDN "cache everything" rule for them.

## Troubleshooting

### 502 Bad Gateway

1. Check that ComicMaintainer is running: `docker ps | grep comicmaintainer`
2. Check the container name matches the `set $upstream_app` line
3. Check both containers are on the network: `docker network inspect swag-network`
4. Check the SWAG log: `docker logs swag`

### Every Subfolder Address Shows the App's Start Page, or Files Fail to Load

- Check that `proxy_pass` ends at the port, with no `/` after it
- Check that `BASE_PATH` matches the folder: `docker exec comicmaintainer env | grep BASE_PATH`
- Look for `Serving the app under the base path` in `docker logs comicmaintainer`. `Ignoring BASE_PATH` means the value could not be used and the log line says why
- Restart ComicMaintainer after changing `BASE_PATH`

### Real-Time Updates Not Working

Real-time updates use Server-Sent Events. The config files already set `proxy_buffering off` and `proxy_cache off`, which they need.

1. In the browser's developer tools, check the Network tab for `/api/events/stream`, with status 200 and type `eventsource`
2. Check the SWAG log: `docker logs swag 2>&1 | grep error`

### "429 Too Many Requests" When Signing In

Set `TRUSTED_PROXIES` as described in [Trusted Proxies](#trusted-proxies).

### Certificate Errors

1. Check your domain's DNS
2. Check the certificate generation log: `docker logs swag | grep -i cert`
3. Make sure ports 80 and 443 are forwarded to your server

## Testing Your Configuration

```bash
# nginx syntax
docker exec swag nginx -t

# The app answers through the proxy. Should return: {"version":"..."}
curl https://comics.yourdomain.com/api/version

# For a subfolder install
curl https://yourdomain.com/comics/api/version
```

## Complete Example

A single compose file with SWAG and ComicMaintainer on a subdomain. The fixed subnet is what `TRUSTED_PROXIES` refers to:

```yaml
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
      - SUBDOMAINS=comics
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
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - PUID=1000
      - PGID=1000
      - TRUSTED_PROXIES=172.30.0.0/24
      # Add BASE_PATH=/comics only for a subfolder install
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    networks:
      - swag-network
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

## Further Reading

- [COMPARISON.md](COMPARISON.md): subdomain and subfolder compared
- [EXAMPLE_SETUP.md](EXAMPLE_SETUP.md): a complete walkthrough
- [OPDS.md](../OPDS.md): reading from comic apps
- [SWAG Documentation](https://docs.linuxserver.io/general/swag)
- [ComicMaintainer issues](https://github.com/mleenorris/ComicMaintainer/issues)
