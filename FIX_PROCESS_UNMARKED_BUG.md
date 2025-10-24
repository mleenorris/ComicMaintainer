# Fix: Process Unmarked File Existence Validation Bug

## Problem Statement
Analyzed the full start-to-finish path for `process-unmarked` and `process-selected` endpoints and discovered a bug preventing proper processing of unmarked files.

## Bug Identified
The `process-unmarked` endpoints did **NOT** validate that files exist before attempting to process them, unlike `process-selected` which properly validates file existence.

## Root Cause Analysis

### Process-Selected (Correct Behavior)
```python
# Gets relative paths from client
for filepath in file_list:
    full_path = os.path.join(WATCHED_DIR, filepath)
    if os.path.exists(full_path):  # ✅ Validates existence
        full_paths.append(full_path)
```

### Process-Unmarked (Bug)
```python
# Gets paths from database
for filepath in files:
    if not is_file_processed(filepath):  # Only checks processed status
        unmarked_files.append(filepath)  # ❌ NO existence check!
```

## Impact

When files were deleted from the filesystem but remained in the database:
- ✅ `process-selected` would skip non-existent files (correct)
- ❌ `process-unmarked` would try to process them and fail (bug)

This caused:
1. Processing errors for deleted files
2. Jobs appearing stuck or failing mysteriously
3. "File not found" errors in batch processing
4. Inconsistent behavior between the two endpoints

## Solution Implemented

### 1. Created Helper Function
```python
def filter_unmarked_existing_files(files):
    """Filter files to only unmarked files that still exist on filesystem"""
    unmarked_files = []
    
    for filepath in files:
        # Skip files that are already processed
        if is_file_processed(filepath):
            continue
        # Validate file still exists before adding to list
        if not os.path.exists(filepath):
            logging.warning(f"[API] Skipping non-existent file: {filepath}")
            continue
        unmarked_files.append(filepath)
    
    return unmarked_files
```

### 2. Updated All 6 Unmarked Endpoints

**Async Endpoints:**
- `async_process_unmarked_files()` ✅
- `async_rename_unmarked_files()` ✅
- `async_normalize_unmarked_files()` ✅

**Streaming Endpoints:**
- `process_unmarked_files()` ✅
- `rename_unmarked_files()` ✅
- `normalize_unmarked_files()` ✅

All now use `filter_unmarked_existing_files()` for consistent validation.

## Testing

Created `test_process_unmarked_fix.py` to verify:
- ✅ All 6 endpoints validate file existence (directly or via helper)
- ✅ Behavior matches `process-selected`
- ✅ No syntax errors
- ✅ No security vulnerabilities (CodeQL scan passed)

## Benefits

1. **Bug Fixed**: Process-unmarked now handles deleted files correctly
2. **Consistency**: Both endpoints behave the same way
3. **Reliability**: Prevents processing failures
4. **Maintainability**: Single helper function for all validation logic
5. **Code Quality**: Eliminated code duplication across 6 endpoints

## Files Changed

- `src/web_app.py`:
  - Added `filter_unmarked_existing_files()` helper function
  - Updated 6 unmarked endpoints to use validation
  - Reduced code duplication by ~50 lines
  
- `test_process_unmarked_fix.py`:
  - Created comprehensive test suite
  - Validates all endpoints have proper validation
  - Ensures consistency between endpoints

## Verification

```bash
# Run tests
python test_process_unmarked_fix.py

# Check syntax
python -m py_compile src/web_app.py

# Security scan
# (CodeQL analysis passed with 0 alerts)
```

## Summary

This fix ensures that all process-unmarked endpoints validate file existence before processing, preventing errors when files in the database have been deleted from the filesystem. The behavior now matches process-selected, providing a consistent and reliable user experience.
