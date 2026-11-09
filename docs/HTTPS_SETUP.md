# HTTPS Setup Guide

Use HTTPS for any access from outside your own network. Sign-in details, session tokens and, for [comic apps](OPDS.md), passwords travel with every request.

There are two ways to do it:

1. **Reverse proxy (recommended).** A proxy such as SWAG, nginx, Traefik or Caddy handles HTTPS and renews its certificate by itself. See the [Reverse Proxy Setup Guide](REVERSE_PROXY.md).
2. **Direct HTTPS.** ComicMaintainer's own web server serves HTTPS from a certificate you provide. Use this when you have a single service and no proxy.

| | Reverse proxy | Direct HTTPS |
|---|---|---|
| **Certificate renewal** | Automatic with Let's Encrypt | Your job; restart the container afterwards |
| **Automatic security headers** (HSTS, `upgrade-insecure-requests`) | Yes, from `X-Forwarded-Proto` | No |
| **Several services on one address** | Yes | No |
| **Setup** | One more container or service | Three environment variables and a certificate |

## Direct HTTPS

The image listens on plain HTTP on port 5000. To add HTTPS, set these variables in the container:

| Variable | Value |
|---|---|
| `ASPNETCORE_URLS` | `http://+:5000;https://+:5001` |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | The certificate file in PEM format, such as `/certs/fullchain.pem` |
| `ASPNETCORE_Kestrel__Certificates__Default__KeyPath` | The private key file in PEM format, such as `/certs/privkey.pem` |

Keep port 5000 in `ASPNETCORE_URLS`, because the container's health check uses it, but publish only the HTTPS port.

Both files must be readable by the container's `PUID`. If the app cannot read them or they do not match, it exits at startup and the reason is in `docker logs`.

### Step 1: Get a Certificate

**Self-signed**, for testing or a private network. Browsers will warn about it, and comic apps may refuse it:

```bash
mkdir -p /path/to/certs
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout /path/to/certs/privkey.pem \
  -out /path/to/certs/fullchain.pem \
  -subj "/CN=comics.local" \
  -addext "subjectAltName=DNS:comics.local,IP:192.168.1.10"

# Replace 1000:1000 with your PUID:PGID
sudo chown 1000:1000 /path/to/certs/privkey.pem /path/to/certs/fullchain.pem
chmod 600 /path/to/certs/privkey.pem
```

Change `comics.local` and the IP address to what you will type in the browser.

**Let's Encrypt** with certbot, for a public domain:

```bash
sudo certbot certonly --standalone -d comics.example.com
```

Certbot stores the files in `/etc/letsencrypt/live/comics.example.com/`, and its private key is readable only by root. Copy them somewhere the container can read:

```bash
sudo mkdir -p /path/to/certs
sudo cp -L /etc/letsencrypt/live/comics.example.com/fullchain.pem /path/to/certs/
sudo cp -L /etc/letsencrypt/live/comics.example.com/privkey.pem /path/to/certs/
# Replace 1000:1000 with your PUID:PGID
sudo chown 1000:1000 /path/to/certs/*.pem
sudo chmod 600 /path/to/certs/privkey.pem
```

**A certificate from another provider** works the same way: put the certificate, followed by any intermediate certificates, in one PEM file, and the key in another.

### Step 2: Run the Container

```bash
docker run -d \
  --name comicmaintainer \
  -e PUID=1000 \
  -e PGID=1000 \
  -e ASPNETCORE_URLS="http://+:5000;https://+:5001" \
  -e ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/fullchain.pem \
  -e ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/certs/privkey.pem \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  -v /path/to/certs:/certs:ro \
  -p 443:5001 \
  iceburn1/comicmaintainer:dotnet
```

Or with Docker Compose:

```yaml
services:
  comicmaintainer:
    image: iceburn1/comicmaintainer:dotnet
    container_name: comicmaintainer
    restart: unless-stopped
    environment:
      - PUID=1000
      - PGID=1000
      - ASPNETCORE_URLS=http://+:5000;https://+:5001
      - ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/fullchain.pem
      - ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/certs/privkey.pem
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/duplicates:/duplicates
      - /path/to/config:/Config
      - /path/to/certs:/certs:ro
    ports:
      - "443:5001"
```

Publishing on host port 443 is the standard HTTPS port, so the address needs no port number. Use another host port, such as `-p 5443:5001`, if something else already uses 443.

### Step 3: Open the App

Go to `https://comics.example.com`. With a self-signed certificate your browser shows a warning that you have to accept.

From the command line:

```bash
# Should return: {"version":"..."}
curl https://comics.example.com/api/version

# With a self-signed certificate, add -k
curl -k https://localhost/api/version
```

### Renewing the Certificate

The app reads the certificate at startup. After renewing it, copy the new files over the old ones and restart the container. With certbot, a deploy hook does this:

```bash
sudo certbot renew --deploy-hook 'cp -L "$RENEWED_LINEAGE/fullchain.pem" "$RENEWED_LINEAGE/privkey.pem" /path/to/certs/ && chown 1000:1000 /path/to/certs/*.pem && docker restart comicmaintainer'
```

## Security Notes

- Self-signed certificates are for testing and private networks only: nothing proves to a client that it is talking to your server.
- Keep the private key readable only by the container's user (`chmod 600`), mount the certificate folder read-only (`:ro`), and never commit keys to version control.
- With direct HTTPS the app does not send `Strict-Transport-Security`, because it only adds that header when a proxy reports HTTPS with `X-Forwarded-Proto`.

## Troubleshooting

### The Container Exits at Startup

Read the reason in the log: `docker logs comicmaintainer`. It is usually a certificate or key file that does not exist at the path you gave, or that the container's `PUID` cannot read. Check the paths and that the folder is mounted.

To confirm that the certificate and the key match:

```bash
openssl x509 -noout -modulus -in fullchain.pem | openssl md5
openssl rsa -noout -modulus -in privkey.pem | openssl md5
# The two values must be the same
```

### The Browser Warns About the Certificate

- A self-signed certificate always gets this warning.
- For any other certificate, check that the domain matches, that the chain is complete and that it has not expired: `openssl s_client -connect comics.example.com:443 -servername comics.example.com`

### The Certificate Has Expired

```bash
openssl x509 -in /path/to/certs/fullchain.pem -noout -dates
```

Renew it, copy the new files and restart the container.

### Connection Refused

- Check the port mapping: the app listens for HTTPS on the port in `ASPNETCORE_URLS`, so publish that one.
- Check that nothing else uses the host port: `sudo ss -tlnp | grep 443`

## Additional Resources

- [Reverse Proxy Setup Guide](REVERSE_PROXY.md)
- [Certbot documentation](https://certbot.eff.org/)
- [Let's Encrypt documentation](https://letsencrypt.org/docs/)
- [Mozilla SSL configuration generator](https://ssl-config.mozilla.org/)
