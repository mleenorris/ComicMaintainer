# Filter Button Fix - Before & After

## The Problem

### Before the Fix ❌

**Step 1:** User selects "Unmarked" filter
```
Header: [📚 All ▼] → User clicks → Selects "⚠️ Unmarked"
Header: [⚠️ Unmarked ▼] ✓ Shows correct filter
File List: Shows only unmarked files ✓ Works correctly
```

**Step 2:** User refreshes the page (F5)
```
Header: [📚 All ▼] ❌ Wrong! Should show "⚠️ Unmarked"
File List: Shows only unmarked files ✓ Works correctly
```

**The Issue:**
- The file list was filtered correctly (backend kept track)
- But the filter button always showed "📚 All" after refresh
- User couldn't tell which filter was active by looking at the UI

---

## The Solution

### After the Fix ✅

**Step 1:** User selects "Unmarked" filter
```
Header: [📚 All ▼] → User clicks → Selects "⚠️ Unmarked"
Header: [⚠️ Unmarked ▼] ✓ Shows correct filter
File List: Shows only unmarked files ✓ Works correctly
✨ Filter saved to preferences database
```

**Step 2:** User refreshes the page (F5)
```
✨ Filter loaded from preferences database
Header: [⚠️ Unmarked ▼] ✓ Still shows "⚠️ Unmarked"!
File List: Shows only unmarked files ✓ Works correctly
Dropdown menu: "⚠️ Unmarked" is highlighted ✓ Active state correct
```

**The Fix:**
- Filter selection is now saved to server-side preferences
- On page load, the saved filter is restored
- Button text and dropdown menu reflect the current filter
- UI and backend are always in sync

---

## Visual Comparison

### Before Fix
```
┌─────────────────────────────────────────┐
│  Filter: [📚 All ▼]  (WRONG)            │  ← Always shows "All"
├─────────────────────────────────────────┤
│  File List                              │
│  ⚠️  file1.cbz (unmarked)              │  ← Shows unmarked files
│  ⚠️  file2.cbz (unmarked)              │     (filter works)
│  ⚠️  file3.cbz (unmarked)              │
└─────────────────────────────────────────┘
```

### After Fix
```
┌─────────────────────────────────────────┐
│  Filter: [⚠️ Unmarked ▼]  (CORRECT)    │  ← Shows actual filter
├─────────────────────────────────────────┤
│  File List                              │
│  ⚠️  file1.cbz (unmarked)              │  ← Shows unmarked files
│  ⚠️  file2.cbz (unmarked)              │     (filter works)
│  ⚠️  file3.cbz (unmarked)              │
└─────────────────────────────────────────┘
```

---

## All Filter Options

The fix applies to all four filter options:

| Filter | Button Text | Icon | Purpose |
|--------|------------|------|---------|
| All | 📚 All | 📚 | Show all files |
| Unmarked | ⚠️ Unmarked | ⚠️ | Show only unprocessed files |
| Marked | ✅ Marked | ✅ | Show only processed files |
| Duplicates | 🔁 Duplicates | 🔁 | Show only duplicate files |

Each filter is now correctly displayed after page refresh.

---

## Implementation

### What Changed

**2 small code additions in `templates/index.html`:**

1. **Save to preferences** (when filter is selected):
   ```javascript
   setPreferences({ filterMode: mode });
   ```

2. **Restore from preferences** (when page loads):
   ```javascript
   if (prefs.filterMode) {
       filterMode = prefs.filterMode;
       document.getElementById('headerFilterLabel').textContent = filterLabels[filterMode];
       // ... update dropdown menu active state
   }
   ```

### Where Data is Stored

- **Location:** `/Config/preferences.db` (SQLite database)
- **Table:** `preferences`
- **Key:** `filterMode`
- **Value:** One of: `'all'`, `'unmarked'`, `'marked'`, `'duplicates'`

### Persistence

The preference persists across:
- ✅ Page refreshes (F5)
- ✅ Browser restarts
- ✅ Container restarts (if `/Config` is mounted as volume)
- ✅ Different browser sessions (server-side storage)

---

## User Experience Improvement

**Before:**
- 🤔 Confusing - UI doesn't match actual filter
- 😕 User has to remember which filter they selected
- 🔄 User might select the same filter again unnecessarily

**After:**
- 😊 Clear - UI always shows the active filter
- 👍 Intuitive - what you see is what you get
- ⚡ Efficient - no confusion or unnecessary clicks
