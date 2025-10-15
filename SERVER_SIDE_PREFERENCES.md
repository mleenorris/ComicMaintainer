# Server-Side Preferences Storage

## Overview

This document describes the migration from client-side localStorage to server-side SQLite storage for user preferences and active job tracking.

## Problem Statement

Previously, the application relied on browser `localStorage` for:
1. **Theme preference** (light/dark mode)
2. **Pagination settings** (perPage value)
3. **Active job tracking** (activeJobId and activeJobTitle for job resumption)

This approach had several issues:
- State was tied to a specific browser/device
- Users had different settings across different browsers
- No way to share settings across multiple devices
- Privacy concerns with storing data in browser storage

## Solution

All preferences and state are now stored server-side in a SQLite database at `/Config/preferences.db`.

### New Backend Components

#### 1. `preferences_store.py`
SQLite-based storage module similar to `job_store.py`. Provides functions for:
- `get_preference(key, default)` - Get a preference value
- `set_preference(key, value)` - Set a preference value
- `get_all_preferences()` - Get all preferences as a dictionary
- `get_active_job()` - Get the currently active batch job
- `set_active_job(job_id, job_title)` - Set the active batch job
- `clear_active_job()` - Clear the active batch job

#### 2. New API Endpoints in `web_app.py`

**Preferences Management:**
- `GET /api/preferences` - Get all user preferences
- `POST /api/preferences` - Set one or more preferences (JSON body)

**Active Job Tracking:**
- `GET /api/active-job` - Get the currently active batch job
- `POST /api/active-job` - Set the active batch job (JSON body with `job_id` and `job_title`)
- `DELETE /api/active-job` - Clear the active batch job

### Frontend Changes

The `index.html` template now:
1. Fetches preferences from the server on page load
2. Updates preferences on the server when they change
3. Tracks active jobs on the server instead of in localStorage
4. Uses a local `hasActiveJob` flag for the `beforeunload` warning (synchronous)

#### New API Helper Functions

```javascript
async function getPreferences()
async function setPreferences(prefs)
async function getActiveJobFromServer()
async function setActiveJobOnServer(jobId, jobTitle)
async function clearActiveJobOnServer()
```

#### Updated Functions

All functions that previously used `localStorage` now use the API helpers:
- `initTheme()` - Loads theme preference from server
- `toggleTheme()` - Saves theme to server
- `updateThemeFromSettings()` - Saves theme to server
- `changePerPage()` - Saves perPage to server
- `pollJobStatus()` - Tracks active job on server
- `checkAndResumeActiveJob()` - Gets active job from server

## Database Schema

### preferences table
```sql
CREATE TABLE preferences (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at REAL NOT NULL
);
```

Stores key-value pairs for user preferences. Values are JSON-encoded.

### active_job table
```sql
CREATE TABLE active_job (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    job_id TEXT,
    job_title TEXT,
    updated_at REAL NOT NULL
);
```

Single-row table that tracks the currently active batch job. Only one job can be active at a time.

## Benefits

1. **Cross-Device Support**: Preferences are stored on the server, so they work across all devices accessing the same server
2. **Persistence**: Settings survive browser cache clears and incognito/private browsing
3. **Server-Side Control**: Server can set defaults and validate preferences
4. **Privacy**: No client-side storage of potentially sensitive data
5. **Centralized State**: All state is in one place (`/Config` directory)

## Testing

### Manual Testing Steps

#### Test 1: Theme Persistence
1. Open web interface at `http://localhost:5000`
2. Toggle theme from light to dark (or vice versa)
3. Verify theme changes immediately
4. Refresh the page
5. Verify theme is still the same as before refresh
6. Open the same URL in a different browser/device
7. Verify theme is the same (server-side storage)

**Expected Result:** Theme persists across page refreshes and browsers

#### Test 2: Pagination Settings
1. Open web interface
2. Change "Items per page" from default (100) to another value (e.g., 50)
3. Verify file list reloads with new page size
4. Refresh the page
5. Verify pagination dropdown shows the saved value
6. Verify file list still uses the saved page size

**Expected Result:** Pagination setting persists across page refreshes

#### Test 3: Active Job Tracking
1. Start a batch processing job (e.g., "Process All")
2. Verify progress modal appears
3. Refresh the page during job execution
4. Verify warning dialog appears: "A batch processing job is still running..."
5. Proceed with refresh
6. Verify progress modal automatically reopens
7. Verify job continues from where it left off

**Expected Result:** Active job is tracked on server and resumes after page refresh

#### Test 4: Job Completion Clears State
1. Start a batch job
2. Wait for completion
3. Verify success message appears
4. Refresh the page
5. Verify NO warning dialog appears
6. Verify NO progress modal opens

**Expected Result:** Completed jobs are cleared from active job tracking

#### Test 5: Multi-Tab Behavior
1. Open web interface in Tab 1
2. Start a batch job in Tab 1
3. Open same URL in Tab 2
4. Try to refresh Tab 2

**Expected Result:** Tab 2 sees the same active job and can resume polling. Warning appears in Tab 2 because the server knows about the active job.

### API Testing

#### Test Preferences API
```bash
# Get preferences
curl http://localhost:5000/api/preferences

# Set theme
curl -X POST http://localhost:5000/api/preferences \
  -H "Content-Type: application/json" \
  -d '{"theme": "dark"}'

# Set multiple preferences
curl -X POST http://localhost:5000/api/preferences \
  -H "Content-Type: application/json" \
  -d '{"theme": "dark", "perPage": 50}'
```

#### Test Active Job API
```bash
# Get active job (should be null if none)
curl http://localhost:5000/api/active-job

# Set active job
curl -X POST http://localhost:5000/api/active-job \
  -H "Content-Type: application/json" \
  -d '{"job_id": "test-job-123", "job_title": "Test Job"}'

# Get active job (should return the job we just set)
curl http://localhost:5000/api/active-job

# Clear active job
curl -X DELETE http://localhost:5000/api/active-job

# Get active job (should be null again)
curl http://localhost:5000/api/active-job
```

## Migration Notes

### Breaking Changes
- **None for users**: The change is transparent. Users' localStorage data will simply not be used anymore, and they'll start with defaults on first load after the update.

### First Run After Update
1. Theme will be set to system preference (light/dark based on OS setting)
2. Pagination will default to 100 items per page
3. No active job will be tracked

Users will need to set their preferences again after the update, but this is a one-time inconvenience.

## File Locations

- Database: `/Config/preferences.db`
- Database WAL files: `/Config/preferences.db-wal`, `/Config/preferences.db-shm`
- Source code: 
  - Backend: `preferences_store.py`, `web_app.py`
  - Frontend: `templates/index.html`

## Backup and Restore

The preferences database can be backed up by copying the entire `/Config` directory, which already contains other important state like:
- `jobs.db` (batch job state)
- `markers/` (processed files, duplicates)
- `config.json` (filename format, watcher settings)
- `Log/` (application logs)

## Future Enhancements

Possible future improvements:
1. **Per-User Preferences**: Add user authentication and per-user settings
2. **More Preferences**: Store sorting order, filter mode, collapsed folders, etc.
3. **Settings Export/Import**: Allow users to export and import their settings
4. **Settings UI**: Dedicated settings page to view/edit all preferences
5. **Preference History**: Track changes to preferences over time

## Rollback Plan

If issues arise, rollback is simple:
1. Revert the code changes
2. Application will use localStorage again
3. The preferences database will remain but won't be used
4. Users' old localStorage settings will be used if still present

No data loss occurs because the old localStorage data is never deleted by this change.
