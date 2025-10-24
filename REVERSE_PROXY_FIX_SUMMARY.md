# Reverse Proxy Support - Complete Implementation

## Issue
Ensure that all site traffic can be reverse proxied properly.

## Problem Analysis
The application had reverse proxy middleware (ProxyFix) configured and BASE_PATH support, but all URLs in the HTML template, manifest.json, and service worker used hardcoded absolute paths starting with `/`. This caused complete breakage when deploying to subdirectory paths like `/comics`:
- Assets would try to load from `/static/...` instead of `/comics/static/...`
- API calls would go to `/api/...` instead of `/comics/api/...`
- Service worker and manifest URLs would be incorrect
- PWA installation would fail

## Solution Implemented

### 1. Dynamic Manifest Generation
**File**: `src/web_app.py`

Changed manifest.json from a static file to a dynamically generated JSON response that includes BASE_PATH in all URLs:
```python
@app.route('/manifest.json')
def serve_manifest():
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    manifest = {
        "start_url": f"{base_path}/",
        "icons": [
            {"src": f"{base_path}/static/icons/icon-192x192.png", ...},
            {"src": f"{base_path}/static/icons/icon-512x512.png", ...}
        ],
        ...
    }
    return jsonify(manifest)
```

### 2. Dynamic Service Worker
**File**: `src/web_app.py`

Created a new route that serves the service worker with BASE_PATH injected:
```python
@app.route('/sw.js')
def serve_service_worker():
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    # Inject BASE_PATH and rewrite all hardcoded paths
    sw_with_base = f"const BASE_PATH = '{base_path}';\n\n{sw_content}"
    # Replace all hardcoded '/' paths with BASE_PATH-aware versions
    ...
    return Response(sw_with_base, mimetype='application/javascript')
```

### 3. Template Updates
**File**: `templates/index.html`

#### Added BASE_PATH constant
```javascript
const BASE_PATH = '{{ base_path }}';
```

#### Added URL helper function
```javascript
function apiUrl(path) {
    if (!path.startsWith('/')) {
        path = '/' + path;
    }
    return BASE_PATH + path;
}
```

#### Updated all references (36+ changes)
- All `fetch('/api/...)` → `fetch(apiUrl('/api/...))`
- All `new EventSource('/api/...)` → `new EventSource(apiUrl('/api/...))`
- Service worker registration: `navigator.serviceWorker.register(apiUrl('/sw.js'))`
- Icon links: `href="/static/icons/..."` → `href="{{ base_path }}/static/icons/..."`
- Manifest link: `href="/manifest.json"` → `href="{{ base_path }}/manifest.json"`

### 4. Flask Route Updates
**File**: `src/web_app.py`

Updated index route to pass BASE_PATH to template:
```python
@app.route('/')
def index():
    base_path = app.config.get('APPLICATION_ROOT', '')
    if base_path == '/':
        base_path = ''
    return render_template('index.html', base_path=base_path)
```

### 5. Documentation Updates
**File**: `docs/REVERSE_PROXY.md`

Added section explaining PWA support:
- Dynamic asset paths adjust automatically
- Offline caching works with BASE_PATH
- PWA installation from any reverse proxy URL
- No additional configuration needed

## Testing

### New Test Files Created
1. **test_reverse_proxy_paths.py** - Validates all path handling in templates
2. **test_reverse_proxy_integration.py** - Tests Flask app with BASE_PATH

### Test Results
```
test_reverse_proxy.py:              5/5 tests passing ✓
test_reverse_proxy_paths.py:        3/3 tests passing ✓
test_reverse_proxy_integration.py:  2/2 tests passing ✓
```

### Security Scan
```bash
bandit -r src/
# Result: No new vulnerabilities introduced
```

## Deployment Scenarios

### Root Path Deployment
**Example**: `https://comics.example.com`

```bash
docker run -d \
  -e WATCHED_DIR=/watched_dir \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Result**: All URLs work correctly at domain root (no BASE_PATH needed)

### Subdirectory Deployment
**Example**: `https://example.com/comics`

```bash
docker run -d \
  -e WATCHED_DIR=/watched_dir \
  -e BASE_PATH=/comics \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

**Nginx Configuration**:
```nginx
location /comics/ {
    proxy_pass http://localhost:5000/;
    proxy_set_header X-Forwarded-Prefix /comics;
    # ... other headers
}
```

**Result**: All URLs automatically include `/comics` prefix

## Benefits

### For Users
- ✅ Deploy at domain root or subdirectory without code changes
- ✅ PWA installation works from any URL
- ✅ Offline support with correct asset paths
- ✅ All API calls and resources load correctly
- ✅ No manual URL configuration needed

### For Administrators
- ✅ Single environment variable (BASE_PATH) controls everything
- ✅ Works with all major reverse proxies (Nginx, Traefik, Apache, Caddy)
- ✅ Backward compatible - existing deployments continue working
- ✅ Comprehensive documentation and examples

### Technical
- ✅ Zero hardcoded paths in templates or static files
- ✅ Dynamic generation of manifest and service worker
- ✅ Automatic URL construction via helper functions
- ✅ Proper handling of Flask's APPLICATION_ROOT
- ✅ Fully tested and validated

## Files Changed

### Modified Files (3)
- `src/web_app.py` - Added dynamic routes, BASE_PATH handling
- `templates/index.html` - Converted to use BASE_PATH and helper functions
- `docs/REVERSE_PROXY.md` - Added PWA support documentation

### New Files (3)
- `test_reverse_proxy_paths.py` - Path handling tests
- `test_reverse_proxy_integration.py` - Integration tests
- `REVERSE_PROXY_FIX_SUMMARY.md` - This document

### Total Changes
- **Added**: ~350 lines (mostly tests and documentation)
- **Modified**: ~60 lines in web_app.py and index.html
- **No breaking changes** - fully backward compatible

## Verification

To verify the fix works:

1. **Test with root path**:
   ```bash
   # Start without BASE_PATH
   docker run -p 5000:5000 ...
   # Visit http://localhost:5000
   # Check: All assets load, API works, PWA installable
   ```

2. **Test with subdirectory**:
   ```bash
   # Start with BASE_PATH
   docker run -e BASE_PATH=/comics -p 5000:5000 ...
   # Configure nginx to proxy /comics/ to :5000/
   # Visit http://localhost/comics
   # Check: All assets load from /comics/*, API works, PWA installable
   ```

3. **Run tests**:
   ```bash
   python test_reverse_proxy.py
   python test_reverse_proxy_paths.py
   python test_reverse_proxy_integration.py
   ```

## Conclusion

All site traffic can now be properly reverse proxied with full support for:
- ✅ Root path deployments (e.g., comics.example.com)
- ✅ Subdirectory deployments (e.g., example.com/comics)
- ✅ PWA features (manifest, service worker, offline support)
- ✅ All static assets and API endpoints
- ✅ Real-time updates (Server-Sent Events)
- ✅ Multiple reverse proxy solutions (Nginx, Traefik, Apache, Caddy)

The implementation is complete, tested, documented, and ready for production use.
