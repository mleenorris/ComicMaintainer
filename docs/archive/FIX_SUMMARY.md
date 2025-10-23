# Fix Summary: Process Unmarked Status Update Issue

## Problem
When using "Process Unmarked Files" feature, the progress modal would:
1. Start with the correct title "Processing Unmarked Files..."
2. After the first item was processed, the title would change to generic "Processing Files..."
3. The title would never show completion status correctly

This made it unclear what operation was running and whether it had completed.

## Root Cause
The `updateProgress()` function in `templates/index.html` was hardcoding the title to "Processing Files..." instead of preserving the job-specific title that was set when the job started.

```javascript
// BEFORE (line 4432)
let title = 'Processing Files...';  // Always hardcoded!
if (errorCount > 0) {
    title = `Processing Files - ${successCount} succeeded, ${errorCount} failed`;
}
```

## Solution
Added a `currentJobTitle` variable to track and preserve the job-specific title throughout the job lifecycle:

1. **Declare variable** (line 2637):
   ```javascript
   let currentJobTitle = null;  // Track current job title for progress updates
   ```

2. **Set title when job starts** (line 3622):
   ```javascript
   currentJobTitle = title;  // Track title for progress updates
   ```

3. **Use preserved title in updateProgress** (lines 4436-4441):
   ```javascript
   let baseTitle = currentJobTitle || 'Processing Files...';
   let title = baseTitle;
   if (errorCount > 0) {
       title = `${baseTitle} - ${successCount} succeeded, ${errorCount} failed`;
   }
   ```

4. **Clear title on completion** (lines 2358, 2368, 2373):
   ```javascript
   currentJobTitle = null;  // Clear when job completes/fails/is cancelled
   ```

## Benefits
- ✅ Job title is preserved throughout processing (e.g., "Processing Unmarked Files...")
- ✅ Status updates show correctly after each item
- ✅ Error counts are appended to the original title, not a generic one
- ✅ Completion status is properly displayed
- ✅ Works for all batch operations: process unmarked, rename unmarked, normalize unmarked, etc.

## Testing
- All existing tests pass (progress callbacks, job-specific events)
- Manual testing verified all three scenarios:
  1. Title preservation during normal processing
  2. Error count display with original title
  3. Proper cleanup on completion

## Files Changed
- `templates/index.html` - 12 lines changed (9 additions, 3 deletions)

## Impact
Minimal, surgical change that only affects the progress display logic. No changes to backend processing, database, or API endpoints.
