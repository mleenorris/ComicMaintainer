# SWAG Proxy Quick Start

[SWAG](https://docs.linuxserver.io/general/swag) is a linuxserver.io container that combines nginx, Let's Encrypt certificates and fail2ban. This page is the short version; the [SWAG Configuration Guide](swag-configs/README.md) has the full steps and troubleshooting.

Both containers must be on the same Docker network, and the config files expect the app's container to be named `comicmaintainer`:

```bash
docker network create swag-network
docker network connect swag-network swag
docker network connect swag-network comicmaintainer
```

## Option 1: Subdomain (Recommended)

Access at `https://comics.yourdomain.com`.

```bash
cp docs/swag-configs/comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/
docker restart swag
```

Do not set `BASE_PATH`.

## Option 2: Subfolder

Access at `https://yourdomain.com/comics`.

```bash
cp docs/swag-configs/comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/
```

Add `BASE_PATH` to the ComicMaintainer container, then restart both:

```yaml
environment:
  - BASE_PATH=/comics
```

```bash
docker restart comicmaintainer swag
```

`BASE_PATH` must match the folder, and must be unset when the app is not in a subfolder. See [Serving Under a Subfolder](REVERSE_PROXY.md#serving-under-a-subfolder-base_path).

## Settings to Add in Either Case

```yaml
environment:
  - TRUSTED_PROXIES=172.18.0.0/16  # Subnet of swag-network
```

Without `TRUSTED_PROXIES`, every client shares one limit of 10 sign-in attempts per 5 minutes, because every request comes from SWAG's address. Find the subnet with `docker network inspect swag-network`. With Authelia enabled, `AUTHELIA_TRUSTED_PROXIES` already covers this.

## Things That Must Keep Working Through the Proxy

- **Real-time updates** use Server-Sent Events at `/api/events/stream`. The config files turn proxy buffering off for this.
- **Comic pages and covers** are sent with `Cache-Control: private`. Do not cache `/api/comicreader/page` or `/api/comicreader/cover` in a shared proxy or CDN.
- **`/manifest.json`** must be reachable, because the browser fetches it to install the web app. It sends a cookie with that request.
- **OPDS links** start with `BASE_PATH`, so comic apps follow them correctly in a subfolder install. See the [OPDS guide](OPDS.md), including the Authelia bypass rule.

## Files

- `docs/swag-configs/comicmaintainer.subdomain.conf`
- `docs/swag-configs/comicmaintainer.subfolder.conf`

## More Help

- [SWAG Configuration Guide](swag-configs/README.md)
- [Reverse Proxy Guide](REVERSE_PROXY.md)
- [Authelia Integration Guide](AUTHELIA.md)
- [ComicMaintainer issues](https://github.com/mleenorris/ComicMaintainer/issues)
