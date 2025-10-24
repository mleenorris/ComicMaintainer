# HTTPS and Proxy Settings Configuration - Implementation Summary

## Overview

This PR implements comprehensive configuration support for HTTPS/SSL and reverse proxy settings, making all options settable through:
1. Environment variables
2. Settings UI (web interface)
3. Manual config file editing

## Problem Statement

Previously, HTTPS and reverse proxy options were only partially configurable:
- HTTPS settings (SSL_CERTFILE, SSL_KEYFILE, SSL_CA_CERTS) were only available via environment variables
- Reverse proxy settings (ProxyFix x_for, x_proto, x_host, x_prefix) were hardcoded to `1`
- BASE_PATH was only available via environment variable

Users had no way to configure these settings through the UI or manually edit the config file.

## Solution

### 1. Configuration System Updates (`src/config.py`)

Added getter and setter functions for all HTTPS and proxy settings:

**HTTPS/SSL Settings:**
- `get_ssl_certfile()` / `set_ssl_certfile()`
- `get_ssl_keyfile()` / `set_ssl_keyfile()`
- `get_ssl_ca_certs()` / `set_ssl_ca_certs()`

**Reverse Proxy Settings:**
- `get_base_path()` / `set_base_path()`
- `get_proxy_x_for()` / `set_proxy_x_for()`
- `get_proxy_x_proto()` / `set_proxy_x_proto()`
- `get_proxy_x_host()` / `set_proxy_x_host()`
- `get_proxy_x_prefix()` / `set_proxy_x_prefix()`

**Priority Order:**
1. Environment variables (highest)
2. Config file (`/Config/config.json`)
3. Default values (lowest)

### 2. Web Application Updates (`src/web_app.py`)

**ProxyFix Configuration:**
Changed from hardcoded values:
```python
app.wsgi_app = ProxyFix(
    app.wsgi_app,
    x_for=1,
    x_proto=1,
    x_host=1,
    x_prefix=1
)
```

To dynamic configuration:
```python
app.wsgi_app = ProxyFix(
    app.wsgi_app,
    x_for=get_proxy_x_for(),
    x_proto=get_proxy_x_proto(),
    x_host=get_proxy_x_host(),
    x_prefix=get_proxy_x_prefix()
)
```

**BASE_PATH Configuration:**
Changed from:
```python
BASE_PATH = os.environ.get('BASE_PATH', '').rstrip('/')
```

To:
```python
BASE_PATH = get_base_path()
```

**API Endpoints:**
Added 16 new API endpoints (GET and POST for each setting):
- `/api/settings/ssl-certfile`
- `/api/settings/ssl-keyfile`
- `/api/settings/ssl-ca-certs`
- `/api/settings/base-path`
- `/api/settings/proxy-x-for`
- `/api/settings/proxy-x-proto`
- `/api/settings/proxy-x-host`
- `/api/settings/proxy-x-prefix`

All POST endpoints return `restart_required: true` to inform users when restart is needed.

### 3. User Interface Updates (`templates/index.html`)

**Added Settings Sections:**

1. **HTTPS/SSL Configuration Section:**
   - SSL Certificate File Path
   - SSL Key File Path
   - SSL CA Certificates Path (Optional)

2. **Reverse Proxy Configuration Section:**
   - Base Path (Subdirectory)
   - X-Forwarded-For Trust Level
   - X-Forwarded-Proto Trust Level
   - X-Forwarded-Host Trust Level
   - X-Forwarded-Prefix Trust Level

**JavaScript Functions:**
- Updated `openSettings()` to load all new settings
- Updated `saveFilenameFormat()` to save all new settings
- Added validation for base path (must start with `/`)
- Added restart notification when proxy/HTTPS settings change

### 4. Documentation Updates

**Updated Files:**
- `README.md`: Added comprehensive HTTPS and proxy configuration sections
- `docs/REVERSE_PROXY.md`: Added trust level configuration and priority order
- `docs/HTTPS_SETUP.md`: Added Settings UI and config file methods
- **New:** `docs/CONFIGURATION.md`: Complete configuration guide

**Added Examples:**
- `config.json.example`: Example configuration file
- `docker-compose.yml`: Updated with new environment variable examples

### 5. Testing

**New Test File:** `test_config_options.py`
- Tests all new config functions
- Tests config file persistence
- Tests environment variable priority
- Tests web_app imports and API endpoints
- Tests HTML settings fields
- Tests documentation updates

**Updated Test File:** `test_reverse_proxy.py`
- Updated to check for dynamic proxy configuration

**Test Results:**
- All 8 new tests pass ✓
- All existing HTTPS tests pass ✓
- All existing reverse proxy tests pass ✓

## Configuration Examples

### Method 1: Environment Variables
```bash
docker run -d \
  -e SSL_CERTFILE=/Config/ssl/cert.crt \
  -e SSL_KEYFILE=/Config/ssl/cert.key \
  -e BASE_PATH=/comics \
  -e PROXY_X_FOR=2 \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

### Method 2: Settings UI
1. Navigate to ⚙️ Settings
2. Scroll to "HTTPS/SSL Configuration" or "Reverse Proxy Configuration"
3. Enter desired values
4. Click Save
5. Restart application

### Method 3: Config File
Edit `/Config/config.json`:
```json
{
  "ssl_certfile": "/Config/ssl/cert.crt",
  "ssl_keyfile": "/Config/ssl/cert.key",
  "base_path": "/comics",
  "proxy_x_for": 2,
  "proxy_x_proto": 1,
  "proxy_x_host": 1,
  "proxy_x_prefix": 1
}
```

## Files Changed

### Core Application
- `src/config.py`: Added 8 new getter/setter function pairs (16 functions total)
- `src/web_app.py`: Updated ProxyFix/BASE_PATH configuration, added 8 API endpoints (16 routes total)
- `templates/index.html`: Added 8 input fields, updated JavaScript functions

### Documentation
- `README.md`: Updated HTTPS and Reverse Proxy sections
- `docs/HTTPS_SETUP.md`: Added configuration methods
- `docs/REVERSE_PROXY.md`: Added trust configuration and priority order
- `docs/CONFIGURATION.md`: **New** - Complete configuration guide

### Examples & Tests
- `config.json.example`: **New** - Example configuration file
- `docker-compose.yml`: Updated with new environment variables
- `test_config_options.py`: **New** - Comprehensive test suite
- `test_reverse_proxy.py`: Updated for dynamic configuration

## Migration Notes

### Backward Compatibility
✅ **Fully backward compatible**
- Existing environment variables work exactly as before
- Default values match previous hardcoded values
- No changes required for existing deployments

### For Users with Environment Variables
No action required. Environment variables continue to work and take highest priority.

### For Users Without Configuration
No action required. Defaults match previous behavior:
- `proxy_x_for=1`
- `proxy_x_proto=1`
- `proxy_x_host=1`
- `proxy_x_prefix=1`
- `base_path=""` (empty)
- `ssl_certfile=""` (empty)
- etc.

## Benefits

1. **Flexibility**: Three configuration methods to suit different deployment scenarios
2. **User-Friendly**: Settings UI makes configuration accessible to non-technical users
3. **Persistence**: Config file ensures settings survive container restarts
4. **Priority System**: Environment variables can override config file for deployment flexibility
5. **Documentation**: Comprehensive guides for all configuration methods
6. **Validation**: Input validation prevents misconfiguration
7. **Transparency**: Restart notifications inform users when changes require restart

## Security Considerations

- SSL certificate paths are validated but not checked for existence (to support Docker volume mounts)
- Config file permissions should be restricted (handled by container user/group)
- GitHub tokens are not displayed in UI (security by design)
- Base path validation prevents malformed paths

## Next Steps

Users can now:
1. Configure HTTPS directly through the Settings UI
2. Adjust proxy trust levels for multi-layer proxy deployments
3. Change base path without restarting containers (takes effect on next restart)
4. Manually edit `/Config/config.json` for advanced configuration
5. Use environment variables for container orchestration (Kubernetes, Docker Compose)

## Testing Recommendations

Before deploying to production:
1. Test Settings UI with your configuration
2. Verify restart notification appears for HTTPS/proxy changes
3. Confirm environment variables override config file when needed
4. Check that config file persists across container restarts
5. Validate proxy trust levels with your reverse proxy setup
