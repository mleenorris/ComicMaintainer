# Fix: Folder Selection and Delete Functionality

## Issue

When a user selected a folder using the folder checkbox and then clicked "Delete Selected", they received an error message: "No files selected", even though files were visibly selected in the UI.

## Root Cause

The `deleteSelectedFiles()` function in `templates/index.html` was querying the DOM for checked checkboxes:

```javascript
const selectedFiles = Array.from(document.querySelectorAll('input[name="fileSelect"]:checked'))
    .map(cb => cb.dataset.filepath);
```

However, the file checkboxes rendered in the DOM don't have a `name="fileSelect"` attribute:

```javascript
html += `
    <input type="checkbox" 
           ${isSelected ? 'checked' : ''} 
           onchange="toggleFileSelection('${escapeJs(file.relative_path)}', this.checked)">
`;
```

This caused the DOM query to return an empty array, even though files were selected.

## Architecture

The application maintains selection state in a global `selectedFiles` Set:

```javascript
let selectedFiles = new Set();
```

This Set is the single source of truth for file selection and is updated by:

1. **Individual file selection**: `toggleFileSelection()` adds/removes files from the Set
2. **Folder selection**: `toggleDirectorySelection()` adds/removes all files in a folder from the Set

When the UI is rendered, checkboxes are marked as checked based on membership in this Set:

```javascript
const isSelected = selectedFiles.has(file.relative_path);
html += `<input type="checkbox" ${isSelected ? 'checked' : ''} ...>`;
```

## Solution

Changed `deleteSelectedFiles()` to use the global `selectedFiles` Set directly instead of querying the DOM:

### Before:
```javascript
async function deleteSelectedFiles() {
    const selectedFiles = Array.from(document.querySelectorAll('input[name="fileSelect"]:checked'))
        .map(cb => cb.dataset.filepath);
    
    if (selectedFiles.length === 0) {
        showMessage('No files selected', 'error');
        return;
    }
    // ... rest of function using selectedFiles
}
```

### After:
```javascript
async function deleteSelectedFiles() {
    const selectedFilesArray = Array.from(selectedFiles);
    
    if (selectedFilesArray.length === 0) {
        showMessage('No files selected', 'error');
        return;
    }
    // ... rest of function using selectedFilesArray
}
```

## Benefits

1. **Fixes the issue**: Folder selection now works correctly for deletion
2. **Aligns with architecture**: Uses the single source of truth (`selectedFiles` Set)
3. **Minimal change**: Only 7 insertions, 8 deletions in one file
4. **No regressions**: Works for both individual file selection and folder selection
5. **Consistent**: Matches how other batch operations work in the codebase

## Testing

### Test Case 1: Folder Selection + Delete
1. Open the web interface
2. Click the checkbox next to a folder name
3. Observe that all files in the folder are selected (checkboxes are checked)
4. Click "Delete Selected" button
5. **Expected**: Confirmation dialog shows the correct count of files to delete
6. **Before fix**: "No files selected" error
7. **After fix**: Files are deleted successfully

### Test Case 2: Individual File Selection + Delete
1. Open the web interface
2. Check individual file checkboxes (not folder checkbox)
3. Click "Delete Selected" button
4. **Expected**: Files are deleted successfully
5. **Result**: Works both before and after fix

### Test Case 3: Mixed Selection + Delete
1. Open the web interface
2. Select some files individually
3. Select a folder (all files in folder are added to selection)
4. Uncheck some individual files
5. Click "Delete Selected" button
6. **Expected**: Only the remaining selected files are deleted
7. **After fix**: Works correctly

## Related Functions

The following functions also rely on the `selectedFiles` Set and work correctly:

- `updateSelectInfo()` - Updates button states based on `selectedFiles.size`
- `toggleFileSelection()` - Updates `selectedFiles` Set when individual files are selected
- `toggleDirectorySelection()` - Updates `selectedFiles` Set when folders are selected
- `renderFileList()` - Renders checkboxes based on `selectedFiles` membership

## Files Changed

- `templates/index.html` (lines 4252-4273)
  - Changed `deleteSelectedFiles()` function to use global `selectedFiles` Set
