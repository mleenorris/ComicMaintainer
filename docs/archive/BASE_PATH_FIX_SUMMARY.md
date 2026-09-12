# BASE_PATH Compatibility Fix

## Issue
When deployed behind a reverse proxy with a `BASE_PATH` configured (e.g., `/comics`), the web interface failed to load files and showed "failed to load files, failed to fetch" errors.

## Root Cause
Multiple `fetch()` calls in `templates/index.html` constructed API URLs directly (e.g., `` fetch(`/api/files?...`) ``) without using the `apiUrl()` helper function that prepends the `BASE_PATH`.

When accessing the application at `example.com/comics/`:
- **Before fix**: `fetch('/api/files')` → Request goes to `example.com/api/files` ❌ (404 Not Found)
- **After fix**: `fetch(apiUrl('/api/files'))` → Request goes to `example.com/comics/api/files` ✅

## Solution
Updated all direct API fetch calls to use the `apiUrl()` wrapper function:

```javascript
// Before
const response = await fetch(`/api/files?page=${page}`);

// After
const response = await fetch(apiUrl(`/api/files?page=${page}`));
```

## Fixed Endpoints (13 total)
1. `/api/files` - Main file list loading
2. `/api/file/.../tags` (GET) - Tag viewing
3. `/api/file/.../tags` (POST) - Tag editing
4. `/api/jobs/{jobId}` - Job status checking
5. `/api/jobs/{jobId}/cancel` - Job cancellation
6. `/api/jobs/{activeJobId}` - Active job resumption
7. `/api/process-file/...` - Single file processing
8. `/api/rename-file/...` - File renaming
9. `/api/normalize-file/...` - Metadata normalization
10. `/api/delete-file/...` (first instance) - File deletion
11. `/api/processing-history` - History viewing
12. `/api/logs` - Log viewing
13. `/api/delete-file/...` (second instance) - Batch file deletion

## Testing
- Created `test_base_path_fetch_calls.py` to verify all fetch calls use `apiUrl()`
- The test ensures no regression by checking:
  - `apiUrl()` helper function exists
  - No direct fetch calls to `/api/...` paths
  - Correct count of wrapped fetch calls (48 total, 13 fixed)
- All existing reverse proxy tests pass (14/14)

## Impact
This fix ensures the web interface works correctly when deployed at subdirectories behind reverse proxies, such as:
- Nginx: `location /comics { proxy_pass http://backend; }`
- Traefik: `PathPrefix("/comics")`
- Apache: `ProxyPass /comics http://backend`
- Caddy: `handle /comics/*`

## Related Configuration
The `BASE_PATH` is configured via environment variable:
```bash
docker run -e BASE_PATH=/comics ...
```

The `apiUrl()` helper function automatically prepends this path:
```javascript
function apiUrl(path) {
    if (!path.startsWith('/')) {
        path = '/' + path;
    }
    return BASE_PATH + path;
}
```

## Files Changed
- `templates/index.html` - Updated 13 fetch calls to use `apiUrl()`
- `test_base_path_fetch_calls.py` - Added regression test (new file)

## Security
- No security vulnerabilities introduced
- CodeQL scan: 0 alerts
- All existing tests pass
