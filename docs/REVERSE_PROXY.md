# Reverse Proxy Setup Guide

This guide explains how to run ComicMaintainer behind a reverse proxy for secure access from outside your network. The proxy provides HTTPS and a friendly address; ComicMaintainer itself listens on plain HTTP on port 5000.

To serve HTTPS from the app itself instead, see the [HTTPS Setup Guide](HTTPS_SETUP.md). Examples use the published image, `iceburn1/comicmaintainer:dotnet`.

## Contents

- [What the Proxy Has to Do](#what-the-proxy-has-to-do)
- [Trusted Proxies and Rate Limiting](#trusted-proxies-and-rate-limiting)
- [Serving Under a Subfolder (BASE_PATH)](#serving-under-a-subfolder-base_path)
- [SWAG](#swag)
- [nginx](#nginx)
- [Traefik](#traefik)
- [Apache](#apache)
- [Caddy](#caddy)
- [Testing Your Setup](#testing-your-setup)
- [Troubleshooting](#troubleshooting)
- [Security Checklist](#security-checklist)

## What the Proxy Has to Do

| Requirement | Why |
|---|---|
| Send `X-Forwarded-Proto` | When it is `https`, the app adds `Strict-Transport-Security: max-age=31536000; includeSubDomains` and `upgrade-insecure-requests` to its responses. `includeSubDomains` applies to every subdomain of the host name, so check that they all serve HTTPS before you expose the app. |
| Append the client address to `X-Forwarded-For` | The app counts rate limits per client from it. See [Trusted Proxies](#trusted-proxies-and-rate-limiting). |
| Not buffer `/api/events/stream` | Real-time updates are Server-Sent Events. A heartbeat is sent every 30 seconds, and the browser reconnects if the stream stays silent. |
| Allow long requests | Most requests must finish within 30 seconds, but condensing a book or queueing a bulk send can take up to 10 minutes, and comic downloads have no limit. |
| Allow large uploads | Restoring an uploaded database backup can be up to 2 GB; a series image can be up to 20 MB. |
| Let `/manifest.json` through | The browser fetches it, with a cookie, to install the web app. Auth gateways must let that request pass. |
| Let `/api/version` through | A new version of the service worker needs it during install. If it fails, the installed version stays in control and the next update check tries again. |
| Not cache comic pages or covers | `/api/comicreader/page` and `/api/comicreader/cover` are sent with `Cache-Control: private`, because they are behind authentication. Do not add a `proxy_cache` or a CDN "cache everything" rule for them. |

The app does not read `X-Forwarded-Host` or `X-Forwarded-Prefix`, so you do not need to send them. It generates links relative to the address the client used.

## Trusted Proxies and Rate Limiting

ComicMaintainer applies two rate limits:

- Each client that is not signed in may make 100 requests a minute. Signed-in users get 1000 each.
- Sign-in, registration, first-run setup, password changes, API key creation and OPDS sign-in share a limit of 10 attempts per 5 minutes per client address. Requests over the limit get `429 Too Many Requests` with a `Retry-After` header.

Behind a reverse proxy every request comes from the proxy's address. Unless the proxy is trusted, all clients share those limits, so one client, such as a password-guessing script, can stop everyone from signing in. The first time a request relayed by an untrusted proxy is rate limited, the log says so.

Set `TRUSTED_PROXIES` to the address your reverse proxy connects to ComicMaintainer from:

```yaml
environment:
  - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of the proxy's Docker network (or the proxy's fixed IP)
```

- Entries are comma-separated IPv4 or IPv6 addresses or CIDR ranges (for example `172.18.0.0/16, 10.0.0.5, fd00::/8`). Hostnames are not supported, and the app refuses to start on an invalid entry. Choose them as described in [Choosing `AUTHELIA_TRUSTED_PROXIES`](AUTHELIA.md#choosing-authelia_trusted_proxies). The value can also be set in `appsettings.json` as `ReverseProxySettings:TrustedProxies`; the environment variable takes precedence.
- One forwarded hop is used by default: the right-most `X-Forwarded-For` entry, which the proxy directly connected to the app appended. If requests pass through several proxies (for example a CDN in front of SWAG), set `TRUSTED_PROXY_HOPS` to the number of right-most entries to follow, or use `ReverseProxySettings:ForwardedHopLimit`. It must be a positive integer. Entries beyond the first are followed only while the current address is trusted.
- With Authelia enabled, the addresses in `AUTHELIA_TRUSTED_PROXIES` are trusted for this as well, so an Authelia deployment usually needs no extra setting.
- The proxy must **append** the client address to `X-Forwarded-For`. nginx does this with `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`, which SWAG's `proxy.conf` and the nginx examples below include. Traefik, Caddy and Apache add it by default. The header is read from the right, and is ignored on requests that do not come from a trusted proxy.
- Keep the hop limit at one unless every additional proxy is trusted and reliably appends its real peer address. For example, Docker's userland proxy can relay traffic through the bridge gateway (`172.18.0.1`), producing a header like `<client-supplied>, 172.18.0.1`. With the one-hop limit the gateway is the partition, not the client-controlled entry to its left. Those clients share a rate limit, but raising the hop limit would let them choose separate partitions with that entry.
- Keep the list narrow: a host in it can choose which address its requests are counted against. The list only affects rate limiting and never makes a request trusted for authentication.

At startup, the log lists the trusted proxies:
`Rate limiting counts anonymous requests from 172.18.0.0/16 per X-Forwarded-For client address, up to 1 hop(s)`.

## Serving Under a Subfolder (BASE_PATH)

Set `BASE_PATH` to serve the app at an address such as `https://example.com/comics`:

```bash
docker run -d \
  -e BASE_PATH=/comics \
  -e PUID=1000 -e PGID=1000 \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comicmaintainer:dotnet
```

- **Format:** leading and trailing slashes are normalized, so `comics`, `/comics` and `/comics/` are the same value. Each segment may contain letters, digits, `.`, `_`, `~` and `-`, and cannot be `.` or `..`. Several segments, such as `/apps/comics`, work too.
- **An invalid value** is logged as an error, starting with `Ignoring BASE_PATH`, and the app is served at the root instead. A valid value logs `Serving the app under the base path /comics`.
- **Forward or strip the prefix:** the app handles both. A proxy can pass `/comics/js/app.js` through unchanged or strip it to `/js/app.js`. The examples below forward it.
- **Only set it for a real subfolder.** If `BASE_PATH` is set while the app is served at the root of a domain, the app generates all its URLs under a folder that does not exist, and the pages break. Remove it in that case.

`BASE_PATH` also applies to the installable web app (the manifest and service worker) and to the links in the [OPDS catalog](OPDS.md). OPDS links are path-absolute, so they work whether your proxy forwards or strips the prefix.

Health checks answer at `/health` and `/health/ready` with or without the prefix.

## SWAG

[SWAG](https://github.com/linuxserver/docker-swag) is a Docker container with nginx and automatic Let's Encrypt certificates. Ready-to-use config files are in [swag-configs](swag-configs/):

- `comicmaintainer.subdomain.conf`, for an address such as `comics.yourdomain.com`
- `comicmaintainer.subfolder.conf`, for an address such as `yourdomain.com/comics`, with `BASE_PATH=/comics`

See the [SWAG quick start](SWAG_PROXY_GUIDE.md) and the [full SWAG guide](swag-configs/README.md).

## nginx

### At the Root of a Domain

```nginx
server {
    listen 80;
    server_name comics.example.com;
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name comics.example.com;

    ssl_certificate /etc/nginx/ssl/comics.example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/comics.example.com.key;

    # Restoring an uploaded database backup can be up to 2 GB.
    client_max_body_size 2g;

    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Long requests and the real-time event stream
        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
        send_timeout 600s;
        proxy_buffering off;
        proxy_cache off;
    }
}
```

Set `TRUSTED_PROXIES` to the address nginx connects from. For nginx on the Docker host, see [the host option](AUTHELIA.md#choosing-authelia_trusted_proxies).

### Under a Subfolder

Run the container with `BASE_PATH=/comics`, as shown [above](#serving-under-a-subfolder-base_path).

```nginx
server {
    listen 443 ssl http2;
    server_name example.com;

    ssl_certificate /etc/nginx/ssl/example.com.crt;
    ssl_certificate_key /etc/nginx/ssl/example.com.key;

    client_max_body_size 2g;

    location = /comics {
        return 301 $scheme://$host/comics/;
    }

    location /comics/ {
        # No path after the port: the request goes through unchanged, with /comics on it
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        proxy_connect_timeout 600s;
        proxy_send_timeout 600s;
        proxy_read_timeout 600s;
        proxy_buffering off;
        proxy_cache off;
    }
}
```

Check your configuration with `nginx -t` and the steps under [Testing Your Setup](#testing-your-setup).

## Traefik

Traefik v2 or later, with Docker labels.

### At the Root of a Domain

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - PUID=1000
      - PGID=1000
      - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of the traefik network
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    labels:
      - "traefik.enable=true"

      # HTTP router: redirect to HTTPS
      - "traefik.http.routers.comics.rule=Host(`comics.example.com`)"
      - "traefik.http.routers.comics.entrypoints=web"
      - "traefik.http.middlewares.comics-https.redirectscheme.scheme=https"
      - "traefik.http.routers.comics.middlewares=comics-https"

      # HTTPS router
      - "traefik.http.routers.comics-secure.rule=Host(`comics.example.com`)"
      - "traefik.http.routers.comics-secure.entrypoints=websecure"
      - "traefik.http.routers.comics-secure.tls=true"
      - "traefik.http.routers.comics-secure.tls.certresolver=letsencrypt"

      # Service definition
      - "traefik.http.services.comics.loadbalancer.server.port=5000"
    networks:
      - traefik

networks:
  traefik:
    external: true
```

### Under a Subfolder

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    environment:
      - BASE_PATH=/comics
      - PUID=1000
      - PGID=1000
      - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of the traefik network
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.comics.rule=Host(`example.com`) && PathPrefix(`/comics`)"
      - "traefik.http.routers.comics.entrypoints=websecure"
      - "traefik.http.routers.comics.tls=true"
      - "traefik.http.services.comics.loadbalancer.server.port=5000"
    networks:
      - traefik

networks:
  traefik:
    external: true
```

No `stripprefix` middleware is needed. Adding one does no harm.

## Apache

Enable the modules first: `a2enmod proxy proxy_http headers ssl`.

### At the Root of a Domain

```apache
<VirtualHost *:80>
    ServerName comics.example.com
    Redirect permanent / https://comics.example.com/
</VirtualHost>

<VirtualHost *:443>
    ServerName comics.example.com

    SSLEngine on
    SSLCertificateFile /etc/apache2/ssl/comics.example.com.crt
    SSLCertificateKeyFile /etc/apache2/ssl/comics.example.com.key

    ProxyPreserveHost On
    ProxyRequests Off
    RequestHeader set X-Forwarded-Proto "https"

    ProxyPass / http://localhost:5000/
    ProxyPassReverse / http://localhost:5000/
    ProxyTimeout 600

    ErrorLog ${APACHE_LOG_DIR}/comics-error.log
    CustomLog ${APACHE_LOG_DIR}/comics-access.log combined
</VirtualHost>
```

### Under a Subfolder

Run the container with `BASE_PATH=/comics`.

```apache
<VirtualHost *:443>
    ServerName example.com

    SSLEngine on
    SSLCertificateFile /etc/apache2/ssl/example.com.crt
    SSLCertificateKeyFile /etc/apache2/ssl/example.com.key

    ProxyPreserveHost On
    ProxyRequests Off
    ProxyTimeout 600

    <Location /comics>
        RequestHeader set X-Forwarded-Proto "https"
        ProxyPass http://localhost:5000/comics
        ProxyPassReverse http://localhost:5000/comics
    </Location>
</VirtualHost>
```

## Caddy

Caddy obtains and renews certificates by itself, sets `X-Forwarded-For` and `X-Forwarded-Proto`, and streams Server-Sent Events without further settings.

### At the Root of a Domain

```caddy
comics.example.com {
    reverse_proxy localhost:5000
}
```

### Under a Subfolder

Run the container with `BASE_PATH=/comics`.

```caddy
example.com {
    redir /comics /comics/
    handle /comics/* {
        reverse_proxy localhost:5000
    }
}
```

## Testing Your Setup

```bash
# The page loads
curl -I https://comics.example.com

# The app answers. Should return: {"version":"..."}
curl https://comics.example.com/api/version
```

For a subfolder install, add the folder: `https://example.com/comics/api/version`.

In the browser's developer tools, the Network tab should show `/api/events/stream` with status 200 and type `eventsource`, staying open.

To confirm the proxy sends `X-Forwarded-Proto`, check that a page's response headers include `Strict-Transport-Security`.

## Troubleshooting

### "Not Secure" in the Browser

1. Check that the proxy's certificate is valid and has not expired.
2. Check that the proxy sends `X-Forwarded-Proto` (see [What the Proxy Has to Do](#what-the-proxy-has-to-do)).
3. Check that plain HTTP redirects to HTTPS.
4. Clear the browser's cache and restart it.

### 404 Not Found or 502 Bad Gateway

- Check that the container is running: `docker ps | grep comicmaintainer`
- Check the log: `docker logs comicmaintainer`
- Check that the proxy forwards to port 5000 of the container

### Real-Time Updates Not Working

- Check that buffering is off for the proxy location (`proxy_buffering off` in nginx)
- Check that read timeouts are longer than 30 seconds, the interval of the stream's heartbeat
- Check the browser's console for connection errors

### Pages or Files Fail to Load Under a Subfolder

- Check that `BASE_PATH` is set and matches the folder. Look for `Serving the app under the base path` or `Ignoring BASE_PATH` in the log.
- For nginx with variables in `proxy_pass`, check that nothing follows the port. A path there replaces the whole request path.
- Clear the browser's cache and unregister the old service worker if you changed the address.

### "429 Too Many Requests" When Signing In

Signing in fails for every user at once, and works again after a few minutes:

- Set `TRUSTED_PROXIES` to your proxy's address (see [Trusted Proxies and Rate Limiting](#trusted-proxies-and-rate-limiting)).
- Check that the startup log line `Rate limiting counts anonymous requests from ...` lists the address your proxy connects from.
- Check that the proxy appends the client address to `X-Forwarded-For`.

### Installing the Web App Fails, or an Update Never Arrives

Check that `/manifest.json`, `/sw.js` and `/api/version` are reachable through the proxy and any auth gateway.

## Security Checklist

1. Use HTTPS for all access from outside your network.
2. Set `TRUSTED_PROXIES` as narrowly as you can.
3. Do not publish the container's port on untrusted networks: use `expose:` or bind it to `127.0.0.1`.
4. Keep the proxy and the ComicMaintainer image updated.

To add single sign-on or two-factor authentication, see the [Authelia Integration Guide](AUTHELIA.md).

## Additional Resources

- [nginx reverse proxy documentation](https://docs.nginx.com/nginx/admin-guide/web-server/reverse-proxy/)
- [Traefik documentation](https://doc.traefik.io/traefik/)
- [Apache mod_proxy documentation](https://httpd.apache.org/docs/current/mod/mod_proxy.html)
- [Caddy reverse proxy guide](https://caddyserver.com/docs/quick-starts/reverse-proxy)
