# Filter Button Display Fix

## Problem
The filter button in the header (📚 All / ⚠️ Unmarked / ✅ Marked / 🔁 Duplicates) did not show the currently selected filter after page refresh. It always displayed "📚 All" even when a different filter was active.

## Root Cause
The `filterMode` JavaScript variable was not persisted between page loads:
1. When a filter was selected, it updated the button text and `filterMode` variable
2. However, `filterMode` was not saved to preferences
3. On page reload, `filterMode` reset to 'all' and the button showed "📚 All"
4. The actual filter applied to the file list was correct (from URL parameters), but the UI didn't reflect it

## Solution
Added persistence of the filter mode using the existing preferences system:

### Changes Made

**File: `templates/index.html`**

1. **Save filter mode when changed** (line 2820)
   ```javascript
   // In setHeaderFilter() function
   setPreferences({ filterMode: mode });
   ```

2. **Restore filter mode on page load** (lines 2611-2633)
   ```javascript
   // In DOMContentLoaded event handler
   if (prefs.filterMode) {
       filterMode = prefs.filterMode;
       
       // Update the filter button to show the saved filter
       const filterLabels = {
           'all': '📚 All',
           'unmarked': '⚠️ Unmarked',
           'marked': '✅ Marked',
           'duplicates': '🔁 Duplicates'
       };
       
       document.getElementById('headerFilterLabel').textContent = filterLabels[filterMode];
       
       // Update active class on dropdown items
       document.querySelectorAll('#headerFilterMenu .header-dropdown-item').forEach(item => {
           if (item.dataset.filter === filterMode) {
               item.classList.add('active');
           } else {
               item.classList.remove('active');
           }
       });
   }
   ```

## How It Works

1. **When user selects a filter:**
   - Filter mode is updated in the `filterMode` variable
   - Button text is updated to show the selected filter
   - Filter is saved to server-side preferences via `setPreferences()`
   - Files are reloaded with the new filter

2. **When page loads:**
   - Preferences are loaded from server
   - If `filterMode` exists in preferences, it's restored
   - Button text is updated to match the saved filter
   - Active menu item is highlighted correctly
   - Files are loaded with the saved filter

## Technical Details

- **Backend Support:** The existing `/api/preferences` endpoint already supports arbitrary key-value preferences
- **Persistence:** Preferences are stored in SQLite database at `/Config/preferences.db`
- **Thread-Safe:** The preferences system uses thread-local connections and WAL mode
- **Minimal Changes:** Only 27 lines added, no breaking changes
- **Consistent with Existing Code:** Uses the same pattern as `perPage` and `theme` preferences

## Testing

To verify the fix:

1. Open the web interface at `http://localhost:5000`
2. Select a filter other than "All" (e.g., "Unmarked")
3. Observe the filter button shows "⚠️ Unmarked"
4. Refresh the page (F5 or Ctrl+R)
5. Verify the filter button still shows "⚠️ Unmarked" after refresh
6. Verify the file list is filtered correctly
7. Check the dropdown menu shows the correct active item

## Benefits

- ✅ Filter selection persists across page refreshes
- ✅ UI accurately reflects the current filter state
- ✅ Consistent user experience
- ✅ No additional database setup required
- ✅ Works seamlessly with existing preferences system
