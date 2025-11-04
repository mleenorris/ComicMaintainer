# SWAG Proxy Configuration Quick Start

This guide provides a quick start for setting up ComicMaintainer with SWAG (Secure Web Application Gateway) from linuxserver.io.

## What is SWAG?

SWAG is an all-in-one Docker container that provides:
- ✅ Nginx reverse proxy
- ✅ Automatic Let's Encrypt SSL certificates
- ✅ Fail2ban protection
- ✅ Easy subdomain and subfolder routing

## Quick Setup

### Option 1: Subdomain (Recommended)

Access at: `https://comics.yourdomain.com`

1. **Copy the config file:**
   ```bash
   cp docs/swag-configs/comicmaintainer.subdomain.conf /path/to/swag/config/nginx/proxy-confs/
   ```

2. **Ensure containers are networked:**
   ```bash
   docker network create swag-network
   docker network connect swag-network comicmaintainer
   ```

3. **Restart SWAG:**
   ```bash
   docker restart swag
   ```

### Option 2: Subfolder

Access at: `https://yourdomain.com/comics`

1. **Copy the config file:**
   ```bash
   cp docs/swag-configs/comicmaintainer.subfolder.conf /path/to/swag/config/nginx/proxy-confs/
   ```

2. **Add BASE_PATH to ComicMaintainer:**
   ```yaml
   environment:
     - BASE_PATH=/comics
   ```

3. **Restart both containers:**
   ```bash
   docker restart comicmaintainer swag
   ```

## Complete Documentation

For detailed instructions, troubleshooting, and authentication options:
- **[SWAG Configuration Guide](docs/swag-configs/README.md)** - Complete setup guide
- **[Reverse Proxy Guide](docs/REVERSE_PROXY.md)** - General reverse proxy documentation

## Configuration Files

Ready-to-use configuration files are available:
- `docs/swag-configs/comicmaintainer.subdomain.conf` - Subdomain deployment
- `docs/swag-configs/comicmaintainer.subfolder.conf` - Subfolder deployment

Both configurations include:
- ✅ WebSocket/SSE support for real-time updates
- ✅ Long timeouts for batch operations
- ✅ Optional authentication (HTTP Basic, Authelia, Authentik, LDAP)
- ✅ Proper buffering settings

## Need Help?

- Check the [SWAG Configuration README](docs/swag-configs/README.md) for troubleshooting
- Review [SWAG Documentation](https://docs.linuxserver.io/general/swag)
- Open an issue on [GitHub](https://github.com/mleenorris/ComicMaintainer/issues)
