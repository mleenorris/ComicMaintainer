# Configuration Guide

ComicMaintainer supports three methods of configuration, with the following priority order:

1. **Environment Variables** (highest priority)
2. **Settings UI** (persists to config file)
3. **Manual Config File Editing** (lowest priority)

All configuration is stored in `/Config/config.json` and persists across container restarts.

## Configuration Methods

### Method 1: Environment Variables (Recommended for Docker)

Set environment variables when starting the container:

```bash
docker run -d \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/Config/ssl/cert.crt \
  -e SSL_KEYFILE=/Config/ssl/cert.key \
  -e BASE_PATH=/comics \
  -e PROXY_X_FOR=2 \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

Environment variables always take priority over config file settings.

### Method 2: Settings UI (User-Friendly)

1. Access the web interface at `http://localhost:5000`
2. Click the three-dot menu (⋮) in the top-right corner
3. Select "⚙️ Settings"
4. Configure your desired settings
5. Click "Save"
6. Restart the container if prompted for changes to take effect

The Settings UI saves to `/Config/config.json` automatically.

### Method 3: Manual Config File (Advanced)

Directly edit `/Config/config.json`:

```json
{
  "filename_format": "{series} - Chapter {issue}",
  "watcher_enabled": true,
  "log_max_bytes": 5242880,
  "issue_number_padding": 4,
  "github_token": "",
  "github_repository": "mleenorris/ComicMaintainer",
  "github_issue_assignee": "copilot",
  "ssl_certfile": "/Config/ssl/cert.crt",
  "ssl_keyfile": "/Config/ssl/cert.key",
  "ssl_ca_certs": "",
  "base_path": "/comics",
  "proxy_x_for": 2,
  "proxy_x_proto": 1,
  "proxy_x_host": 1,
  "proxy_x_prefix": 1
}
```

After editing, restart the container for changes to take effect.

## Available Settings

### Application Settings

- **`filename_format`**: Template for renaming files (default: `{series} - Chapter {issue}`)
- **`watcher_enabled`**: Enable/disable automatic file watching (default: `true`)
- **`log_max_bytes`**: Maximum log file size in bytes before rotation (default: `5242880` = 5MB)
- **`issue_number_padding`**: Number of digits for padded issue numbers (default: `4`)

### GitHub Integration

- **`github_token`**: GitHub Personal Access Token for auto-issue creation (default: empty)
- **`github_repository`**: Repository in `owner/repo` format (default: `mleenorris/ComicMaintainer`)
- **`github_issue_assignee`**: Username to assign issues to (default: `copilot`)

### HTTPS/SSL Configuration

- **`ssl_certfile`**: Path to SSL certificate file (default: empty)
- **`ssl_keyfile`**: Path to SSL private key file (default: empty)
- **`ssl_ca_certs`**: Path to CA certificate bundle (optional, default: empty)

**Note**: Changes to HTTPS settings require application restart.

### Reverse Proxy Configuration

- **`base_path`**: Path prefix for subdirectory deployments (default: empty)
  - Example: `/comics` serves at `example.com/comics`
  - Must start with a forward slash `/`

- **`proxy_x_for`**: Trust level for X-Forwarded-For header (default: `1`)
  - Number of reverse proxy servers to trust
  - Set to `0` to disable, increase for multiple proxy layers

- **`proxy_x_proto`**: Trust level for X-Forwarded-Proto header (default: `1`)
  - Controls HTTPS detection from reverse proxy

- **`proxy_x_host`**: Trust level for X-Forwarded-Host header (default: `1`)
  - Controls hostname detection from reverse proxy

- **`proxy_x_prefix`**: Trust level for X-Forwarded-Prefix header (default: `1`)
  - Used with `base_path` for subdirectory deployments

**Note**: Changes to reverse proxy settings require application restart.

## Environment Variables Reference

All settings can be configured via environment variables. The variable name is the uppercase version of the config key:

| Config Key | Environment Variable | Example |
|------------|---------------------|---------|
| `ssl_certfile` | `SSL_CERTFILE` | `/Config/ssl/cert.crt` |
| `ssl_keyfile` | `SSL_KEYFILE` | `/Config/ssl/cert.key` |
| `ssl_ca_certs` | `SSL_CA_CERTS` | `/Config/ssl/ca-bundle.crt` |
| `base_path` | `BASE_PATH` | `/comics` |
| `proxy_x_for` | `PROXY_X_FOR` | `2` |
| `proxy_x_proto` | `PROXY_X_PROTO` | `1` |
| `proxy_x_host` | `PROXY_X_HOST` | `1` |
| `proxy_x_prefix` | `PROXY_X_PREFIX` | `1` |
| `log_max_bytes` | `LOG_MAX_BYTES` | `10485760` |
| `github_token` | `GITHUB_TOKEN` | `ghp_...` |
| `github_repository` | `GITHUB_REPOSITORY` | `owner/repo` |
| `github_issue_assignee` | `GITHUB_ISSUE_ASSIGNEE` | `username` |

## Priority Order

When a setting is configured in multiple places, the priority is:

1. **Environment Variable** (highest)
2. **Config File** (via Settings UI or manual edit)
3. **Default Value** (lowest)

Example: If you set `PROXY_X_FOR=3` as an environment variable and `"proxy_x_for": 1` in the config file, the application will use `3`.

## Configuration Validation

The application validates configuration values on startup:

- `base_path` must start with `/` (e.g., `/comics`, not `comics`)
- `proxy_x_*` values must be non-negative integers
- `log_max_bytes` must be a positive number
- `issue_number_padding` must be 0 or greater

Invalid values are logged and fall back to defaults.

## Restart Requirements

The following settings require an application restart to take effect:

- HTTPS/SSL settings (`ssl_certfile`, `ssl_keyfile`, `ssl_ca_certs`)
- Reverse proxy settings (`base_path`, `proxy_x_*`)
- Log rotation settings (`log_max_bytes`)

Other settings (like `filename_format` and `watcher_enabled`) take effect immediately.

## See Also

- [HTTPS Setup Guide](HTTPS_SETUP.md) - Detailed HTTPS configuration
- [Reverse Proxy Guide](REVERSE_PROXY.md) - Reverse proxy examples (Nginx, Traefik, etc.)
- [README](../README.md) - General usage and setup
