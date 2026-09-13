# Website Failure Fix

## Issue
The website was failing with the error:
```
jinja2.exceptions.TemplateNotFound: index.html
```

This error occurred when accessing the website at `obelisk.frost-byte.org/logt`.

## Root Cause Analysis

The issue was caused by two problems in the deployment configuration:

### Problem 1: Missing Static Folder in Dockerfile
The `Dockerfile` was copying the `templates` directory but not the `static` directory to the Docker image. This meant that static assets (icons, manifest.json, service worker, etc.) were not available in the deployed container.

**Before:**
```dockerfile
# Copy templates directory
COPY templates /app/templates
```

**After:**
```dockerfile
# Copy templates and static directories
COPY templates /app/templates
COPY static /app/static
```

### Problem 2: Incorrect Path Calculation in web_app.py
The `web_app.py` script calculates the paths to the templates and static folders based on its own location. However, the calculation was designed for development mode (when `web_app.py` is in `src/`) but didn't account for deployment mode (when `web_app.py` is copied directly to `/app/`).

**Before:**
```python
# web_app.py is in src/, templates and static are in parent directory
project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
template_folder = os.path.join(project_root, 'templates')
static_folder = os.path.join(project_root, 'static')
```

When `web_app.py` is in `/app/web_app.py`:
- `os.path.dirname(os.path.abspath(__file__))` → `/app`
- `os.path.dirname(os.path.dirname(...))` → `/` (incorrect!)

This caused Flask to look for templates in `/templates` instead of `/app/templates`.

**After:**
```python
# web_app.py is in src/ during development, but in /app/ when deployed in Docker
script_dir = os.path.dirname(os.path.abspath(__file__))
# Check if we're in the src/ directory or deployed directly in /app/
if os.path.basename(script_dir) == 'src':
    # Development mode: web_app.py is in src/, templates and static are in parent directory
    project_root = os.path.dirname(script_dir)
else:
    # Deployed mode: web_app.py is in /app/, templates and static are in /app/templates and /app/static
    project_root = script_dir

template_folder = os.path.join(project_root, 'templates')
static_folder = os.path.join(project_root, 'static')
```

Now the path calculation works correctly in both scenarios:
- **Development mode** (script in `src/`): `project_root` → `/home/user/project`
- **Deployment mode** (script in `/app/`): `project_root` → `/app`

## Changes Made

1. **Dockerfile**: Added `COPY static /app/static` to copy the static folder to the Docker image

2. **src/web_app.py**: Made three changes to fix path resolution:
   - Added conditional logic to detect whether the script is running in development or deployment mode and calculate `template_folder` and `static_folder` accordingly
   - Fixed `serve_service_worker()` function to use calculated `static_folder` instead of hardcoded `'../static'`
   - Fixed `serve_static()` function to use calculated `static_folder` instead of hardcoded `'../static'`

## Testing

The fix was validated with a custom test script that verified:
1. Path resolution works correctly in development mode (script in `src/`)
2. Path resolution works correctly in deployment mode (script in `/app/`)
3. All required directories exist in the current development environment
4. The `index.html` template file is accessible

## Impact

This fix resolves the website failure issue and ensures that:
- The Flask application can find and render templates correctly
- Static assets (icons, manifest, service worker) are available for Progressive Web App functionality
- The application works correctly in both development and deployment environments

## Deployment

To deploy this fix:
1. Build a new Docker image with these changes
2. Deploy the updated image to the server
3. The website should now load correctly at `obelisk.frost-byte.org/logt`
