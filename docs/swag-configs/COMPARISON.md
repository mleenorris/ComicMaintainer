# SWAG Configuration Comparison: Subdomain vs Subfolder

This document compares the two ways of deploying ComicMaintainer with SWAG.

| Feature | Subdomain | Subfolder |
|---------|-----------|-----------|
| **URL** | `https://comics.yourdomain.com` | `https://yourdomain.com/comics` |
| **DNS** | CNAME or A record for the subdomain | Nothing beyond the main domain |
| **`BASE_PATH`** | Must stay unset | **Required** (`BASE_PATH=/comics`) |
| **Config file** | `comicmaintainer.subdomain.conf` | `comicmaintainer.subfolder.conf` |
| **Certificate** | The subdomain must be in SWAG's `SUBDOMAINS` | Uses the main domain's certificate |

## Subdomain

Choose this when you can add a DNS record. It is the simpler setup: the app is at the root of its own address and has no prefix to deal with.

```yaml
environment:
  - PUID=1000
  - PGID=1000
  - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of swag-network
  # No BASE_PATH
```

The key lines of the config file:

```nginx
server {
    listen 443 ssl http2;
    server_name comics.*;

    location / {
        set $upstream_app comicmaintainer;
        set $upstream_port 5000;
        set $upstream_proto http;
        proxy_pass $upstream_proto://$upstream_app:$upstream_port;
        proxy_buffering off;
        proxy_read_timeout 600s;
    }
}
```

## Subfolder

Choose this when you want several services under one domain and would rather not add DNS records.

```yaml
environment:
  - BASE_PATH=/comics              # Required
  - PUID=1000
  - PGID=1000
  - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of swag-network
```

The key lines of the config file:

```nginx
location /comics {
    return 301 $scheme://$host/comics/;  # Redirect to the trailing slash
}

location ^~ /comics/ {
    set $upstream_app comicmaintainer;
    set $upstream_port 5000;
    set $upstream_proto http;
    proxy_pass $upstream_proto://$upstream_app:$upstream_port;  # No path after the port
    proxy_buffering off;
    proxy_read_timeout 600s;
}
```

The `proxy_pass` line must end at the port. When `proxy_pass` contains variables, nginx uses a path written after the port, even a single `/`, as the whole request path, so every request would reach the app as `/`. Without a path the request goes through unchanged, with `/comics` on it, and ComicMaintainer removes the prefix because of `BASE_PATH`.

## What Both Config Files Share

- `proxy_buffering off` and `proxy_cache off`, for the Server-Sent Events that carry real-time updates
- 600-second proxy timeouts
- The same upstream: container `comicmaintainer`, port 5000
- Commented-out includes for Authelia, Authentik and LDAP. The [SWAG README](README.md#adding-a-sign-in-in-front-of-the-app) explains what each does and why HTTP Basic does not work with this app

## Switching Between Them

### From Subdomain to Subfolder

1. Add `BASE_PATH=/comics` to the ComicMaintainer container.
2. Replace the config file:
   ```bash
   cp comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/
   rm /path/to/swag/config/nginx/proxy-confs/comicmaintainer.subdomain.conf
   ```
3. Restart: `docker restart comicmaintainer swag`

### From Subfolder to Subdomain

1. Remove `BASE_PATH` from the ComicMaintainer container.
2. Add the DNS record, and add the subdomain to SWAG's `SUBDOMAINS` if you list them there.
3. Replace the config file:
   ```bash
   cp comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/
   rm /path/to/swag/config/nginx/proxy-confs/comicmaintainer.subfolder.conf
   ```
4. Restart: `docker restart comicmaintainer swag`

An installed web app (PWA) is tied to its address, so reinstall it after switching. Saved offline comics do not move to the new address either.

## Troubleshooting

### Subdomain

| Problem | What to check |
|---|---|
| 502 Bad Gateway | `nslookup comics.yourdomain.com`, `docker ps \| grep comicmaintainer`, `docker network inspect swag-network` |
| Certificate errors | `docker logs swag \| grep -i cert`, `docker exec swag env \| grep SUBDOMAINS` |

### Subfolder

| Problem | What to check |
|---|---|
| Every address shows the start page | `proxy_pass` must end at the port, with no `/` after it |
| Files fail to load, or 404 | `docker exec comicmaintainer env \| grep BASE_PATH` should show `BASE_PATH=/comics` |
| `/comics` does not redirect | Both location blocks must exist: `location /comics` for the redirect and `location ^~ /comics/` for the proxy |
