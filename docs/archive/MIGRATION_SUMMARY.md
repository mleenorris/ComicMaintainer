# localStorage to Server-Side Storage Migration Summary

## Issue
Don't rely on local browser storage for anything (#[Issue Number])

## Solution Overview
Replaced all `localStorage` usage with server-side SQLite database storage.

## Architecture Change

### Before
```
Browser (localStorage)
├── theme: "dark"
├── perPage: 100
├── activeJobId: "job-123"
└── activeJobTitle: "Processing..."
```

### After
```
Server (/Config/preferences.db)
├── preferences table
│   ├── theme: "dark"
│   └── perPage: 100
└── active_job table
    ├── job_id: "job-123"
    └── job_title: "Processing..."
    
Browser (JavaScript)
└── API calls to /api/preferences and /api/active-job
```

## Code Changes Summary

### 1. New Backend Module: `preferences_store.py`
```python
# Functions provided:
get_preference(key, default=None)
set_preference(key, value)
get_all_preferences()
get_active_job()
set_active_job(job_id, job_title)
clear_active_job()
```

### 2. New API Endpoints: `web_app.py`
```python
# Preferences
GET  /api/preferences      # Get all preferences
POST /api/preferences      # Set preferences

# Active Job Tracking
GET    /api/active-job     # Get active job
POST   /api/active-job     # Set active job
DELETE /api/active-job     # Clear active job
```

### 3. Frontend Updates: `templates/index.html`

#### Old Code (localStorage)
```javascript
// Get preference
const theme = localStorage.getItem('theme');

// Set preference
localStorage.setItem('theme', 'dark');

// Remove preference
localStorage.removeItem('activeJobId');
```

#### New Code (Server API)
```javascript
// Get preference
const prefs = await getPreferences();
const theme = prefs.theme;

// Set preference
await setPreferences({ theme: 'dark' });

// Clear active job
await clearActiveJobOnServer();
```

## Database Schema

### preferences table
| Column | Type | Description |
|--------|------|-------------|
| key | TEXT PRIMARY KEY | Preference key (e.g., "theme", "perPage") |
| value | TEXT NOT NULL | JSON-encoded value |
| updated_at | REAL NOT NULL | Unix timestamp of last update |

### active_job table
| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER PRIMARY KEY | Always 1 (single row) |
| job_id | TEXT | Current active job ID (NULL if none) |
| job_title | TEXT | Job description/title |
| updated_at | REAL NOT NULL | Unix timestamp of last update |

## Migration Path

### For Existing Users
1. **No data loss**: Old localStorage data remains in browser but is not used
2. **Clean start**: Users will see defaults on first load after update:
   - Theme: System preference (light/dark based on OS)
   - Pagination: 100 items per page
   - Active job: None
3. **One-time setup**: Users need to set preferences again after update

### For New Users
- Fresh installation works exactly as before
- All preferences start with sensible defaults

## Testing Checklist

### Unit Tests ✅
- [x] Preference storage (set/get/update)
- [x] Active job tracking (set/get/clear)
- [x] Database persistence
- [x] Default values
- [x] Python syntax validation
- [x] JavaScript syntax validation

### Integration Tests (Manual)
- [ ] Theme persistence across page refreshes
- [ ] Theme consistency across browsers/devices
- [ ] Pagination settings persistence
- [ ] Active job tracking during batch processing
- [ ] Job resumption after page refresh
- [ ] Multi-tab behavior (shared server state)
- [ ] Beforeunload warning during active job

### API Tests (cURL)
```bash
# Test preferences API
curl http://localhost:5000/api/preferences
curl -X POST http://localhost:5000/api/preferences \
  -H "Content-Type: application/json" \
  -d '{"theme": "dark", "perPage": 50}'

# Test active job API
curl http://localhost:5000/api/active-job
curl -X POST http://localhost:5000/api/active-job \
  -H "Content-Type: application/json" \
  -d '{"job_id": "test-123", "job_title": "Test Job"}'
curl -X DELETE http://localhost:5000/api/active-job
```

## Rollback Plan

If issues arise:
1. Revert commits: `git revert HEAD~2..HEAD`
2. Application returns to using localStorage
3. No data corruption (preferences.db is separate)
4. Old localStorage data still present in browsers

## Files Changed

```
.gitignore                  |   3 +
SERVER_SIDE_PREFERENCES.md  | 244 +++++++++++++++++++++++++
TESTING_PAGE_REFRESH.md     |  21 +--
preferences_store.py        | 236 ++++++++++++++++++++++++
templates/index.html        | 179 +++++++++++++++---
web_app.py                  |  76 ++++++++
```

**Total**: 702 insertions(+), 57 deletions(-)

## Related Documentation

- **`SERVER_SIDE_PREFERENCES.md`**: Complete migration guide with testing instructions
- **`TESTING_PAGE_REFRESH.md`**: Updated to reflect server-side storage
- **`.gitignore`**: Excludes preferences database files

## Performance Impact

- **Minimal overhead**: SQLite operations are fast (<1ms for preference access)
- **Concurrent access**: WAL mode enables multiple readers
- **Network**: Small JSON payloads (~100 bytes per API call)
- **Startup**: Preferences loaded once on page load
- **Updates**: Async API calls don't block UI

## Security Considerations

- **No sensitive data**: Only UI preferences stored
- **Server-side validation**: Can add validation rules
- **Thread-safe**: Database operations use proper locking
- **Isolated storage**: Each server instance has its own database

## Future Enhancements

Possible improvements:
1. Per-user preferences (requires authentication)
2. More preference types (sort order, filter mode, collapsed folders)
3. Settings import/export
4. Preference history/audit trail
5. Admin UI for preference management

## Conclusion

This migration successfully removes all browser localStorage dependencies, replacing them with robust server-side storage that provides better cross-device support, persistence, and privacy.

**Status**: ✅ Implementation Complete - Ready for Testing
