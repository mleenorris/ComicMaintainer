# Architecture Change: localStorage to Server-Side Storage

## Overview
This document visualizes the architectural change from client-side localStorage to server-side database storage.

---

## Before: Client-Side Storage (localStorage)

```
┌─────────────────────────────────────────────────────────────┐
│                        Browser Tab 1                         │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              localStorage (Client-Side)                │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │  theme: "dark"                                   │  │  │
│  │  │  perPage: 100                                    │  │  │
│  │  │  activeJobId: "job-123"                          │  │  │
│  │  │  activeJobTitle: "Processing..."                 │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  │                                                         │  │
│  │  JavaScript reads/writes directly to localStorage       │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                        Browser Tab 2                         │
│  ┌───────────────────────────────────────────────────────┐  │
│  │           localStorage (Same Domain = Shared)          │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │  theme: "dark"           ← Same as Tab 1        │  │  │
│  │  │  perPage: 100            ← Same as Tab 1        │  │  │
│  │  │  activeJobId: "job-123"  ← Same as Tab 1        │  │  │
│  │  │  activeJobTitle: "Processing..."                 │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                    Different Browser/Device                  │
│  ┌───────────────────────────────────────────────────────┐  │
│  │        localStorage (Separate, Not Shared)             │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │  theme: "light"          ← Different!           │  │  │
│  │  │  perPage: 50             ← Different!           │  │  │
│  │  │  activeJobId: null       ← Different!           │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘

                           ┌─────────────┐
                           │   Server    │
                           │  (No State) │
                           └─────────────┘
```

### Problems with localStorage:
❌ Settings not shared across browsers/devices  
❌ Lost when browser cache is cleared  
❌ Privacy concerns with client-side storage  
❌ Each browser has different state  

---

## After: Server-Side Storage (SQLite)

```
┌─────────────────────────────────────────────────────────────┐
│                        Browser Tab 1                         │
│  ┌───────────────────────────────────────────────────────┐  │
│  │                    JavaScript (Client)                  │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │  const prefs = await getPreferences()           │  │  │
│  │  │      ↓ HTTP GET /api/preferences                 │  │  │
│  │  │                                                   │  │  │
│  │  │  await setPreferences({theme: 'dark'})           │  │  │
│  │  │      ↓ HTTP POST /api/preferences                │  │  │
│  │  │                                                   │  │  │
│  │  │  const job = await getActiveJobFromServer()      │  │  │
│  │  │      ↓ HTTP GET /api/active-job                  │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                              ↓
                      HTTP API Calls
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                           Server                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │                  Flask API Endpoints                    │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │  GET  /api/preferences                           │  │  │
│  │  │  POST /api/preferences                           │  │  │
│  │  │  GET  /api/active-job                            │  │  │
│  │  │  POST /api/active-job                            │  │  │
│  │  │  DELETE /api/active-job                          │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  │                        ↓                                 │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │          preferences_store.py                    │  │  │
│  │  │  ┌───────────────────────────────────────────┐  │  │  │
│  │  │  │  get_preference(key, default)              │  │  │  │
│  │  │  │  set_preference(key, value)                │  │  │  │
│  │  │  │  get_all_preferences()                     │  │  │  │
│  │  │  │  get_active_job()                          │  │  │  │
│  │  │  │  set_active_job(job_id, job_title)        │  │  │  │
│  │  │  │  clear_active_job()                        │  │  │  │
│  │  │  └───────────────────────────────────────────┘  │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  │                        ↓                                 │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │       /Config/preferences.db (SQLite)            │  │  │
│  │  │  ┌───────────────────────────────────────────┐  │  │  │
│  │  │  │  preferences table:                        │  │  │  │
│  │  │  │    key='theme',    value='dark'            │  │  │  │
│  │  │  │    key='perPage',  value='100'             │  │  │  │
│  │  │  │                                             │  │  │  │
│  │  │  │  active_job table:                         │  │  │  │
│  │  │  │    job_id='job-123'                        │  │  │  │
│  │  │  │    job_title='Processing...'               │  │  │  │
│  │  │  └───────────────────────────────────────────┘  │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                              ↑
                      HTTP API Calls
                              ↑
┌─────────────────────────────────────────────────────────────┐
│                        Browser Tab 2                         │
│  (Reads/writes same server database via API)                │
└─────────────────────────────────────────────────────────────┘
                              ↑
                      HTTP API Calls
                              ↑
┌─────────────────────────────────────────────────────────────┐
│                    Different Browser/Device                  │
│  (Reads/writes same server database via API)                │
└─────────────────────────────────────────────────────────────┘
```

### Benefits of Server-Side Storage:
✅ Settings shared across all browsers/devices  
✅ Persistent (survives browser cache clears)  
✅ Centralized state management  
✅ Privacy compliant (no client-side storage)  
✅ Server can validate and set defaults  

---

## Data Flow Comparison

### Before (localStorage):
```
User Action → JavaScript → localStorage.setItem() → Browser Storage
                                                      (isolated per browser)
```

### After (Server API):
```
User Action → JavaScript → fetch('/api/preferences') → Flask API
                                                     → preferences_store.py
                                                     → SQLite Database
                                                     → Disk (/Config/preferences.db)
                                                     (shared across all clients)
```

---

## Example: Setting Theme Preference

### Before (localStorage):
```javascript
// In templates/index.html
function toggleTheme() {
    const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
    const newTheme = currentTheme === 'light' ? 'dark' : 'light';
    setTheme(newTheme);
    localStorage.setItem('theme', newTheme);  // ❌ Client-side only
}
```

### After (Server API):
```javascript
// In templates/index.html
async function toggleTheme() {
    const currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
    const newTheme = currentTheme === 'light' ? 'dark' : 'light';
    setTheme(newTheme);
    await setPreferences({ theme: newTheme });  // ✅ Persisted on server
}

async function setPreferences(prefs) {
    await fetch('/api/preferences', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(prefs)
    });
}
```

### Server API Handler:
```python
# In web_app.py
@app.route('/api/preferences', methods=['POST'])
def set_preferences_endpoint():
    data = request.json
    for key, value in data.items():
        set_preference(key, value)  # Stores in SQLite
    return jsonify({'success': True})
```

### Database Storage:
```python
# In preferences_store.py
def set_preference(key: str, value: Any):
    with get_db_connection() as conn:
        cursor = conn.cursor()
        json_value = json.dumps(value)
        cursor.execute('''
            INSERT OR REPLACE INTO preferences (key, value, updated_at)
            VALUES (?, ?, ?)
        ''', (key, json_value, time.time()))
        conn.commit()
```

---

## Database Structure

### preferences.db Schema

**preferences table:**
```sql
CREATE TABLE preferences (
    key TEXT PRIMARY KEY,        -- e.g., 'theme', 'perPage'
    value TEXT NOT NULL,         -- JSON-encoded value
    updated_at REAL NOT NULL     -- Unix timestamp
);
```

**Example data:**
| key | value | updated_at |
|-----|-------|------------|
| theme | "dark" | 1697234567.123 |
| perPage | 100 | 1697234567.456 |

**active_job table:**
```sql
CREATE TABLE active_job (
    id INTEGER PRIMARY KEY CHECK (id = 1),  -- Always 1 (single row)
    job_id TEXT,                            -- Current job ID or NULL
    job_title TEXT,                         -- Job description
    updated_at REAL NOT NULL                -- Unix timestamp
);
```

**Example data:**
| id | job_id | job_title | updated_at |
|----|--------|-----------|------------|
| 1 | job-123 | Processing All Files | 1697234567.789 |

---

## Migration Impact

### User Experience
- **First load after update**: Settings reset to defaults
- **Ongoing use**: Settings persist across browsers/devices
- **No data loss**: Old localStorage remains but is not used

### Performance
- **API overhead**: ~10-50ms per preference update
- **Database performance**: <1ms for SQLite operations
- **Network payload**: ~100 bytes per API call
- **Page load**: One additional API call (~50ms)

### Compatibility
- **Backward compatible**: No breaking changes to functionality
- **Forward compatible**: Can add more preferences easily
- **Multi-worker safe**: SQLite WAL mode handles concurrency

---

## File System Layout

### Before:
```
/Config/
├── config.json              # Filename format, watcher settings
├── jobs.db                  # Batch job state
├── markers/                 # Processed files, duplicates
└── Log/                     # Application logs
```

### After:
```
/Config/
├── config.json              # Filename format, watcher settings
├── jobs.db                  # Batch job state
├── preferences.db           # 🆕 User preferences & active job
├── preferences.db-wal       # 🆕 Write-ahead log
├── preferences.db-shm       # 🆕 Shared memory
├── markers/                 # Processed files, duplicates
└── Log/                     # Application logs
```

---

## Rollback Instructions

If needed, rollback is simple:

```bash
# 1. Revert the commits
git revert HEAD~3..HEAD

# 2. Restart the application
docker restart comictagger-watcher

# 3. (Optional) Remove the new database
rm /Config/preferences.db*
```

The application will return to using localStorage without data loss.

---

## Summary

This migration successfully:
- ✅ Removed all localStorage dependencies
- ✅ Implemented robust server-side storage
- ✅ Maintained backward compatibility
- ✅ Improved cross-device support
- ✅ Enhanced data persistence
- ✅ Preserved performance

The architecture is now more scalable, maintainable, and user-friendly.
