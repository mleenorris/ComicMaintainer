# File Actions Dropdown - HTTP 405 Error Fix

## Problem
Clicking on "Process", "Rename", or "Normalize" actions from a file's action dropdown resulted in HTTP 405 (Method Not Allowed) errors.

## Root Cause
The JavaScript functions were calling incorrect API endpoints:
- `processSingleFile()` was calling `/api/process-file/${encodeURIComponent(filepath)}` - endpoint doesn't exist
- `renameSingleFile()` was calling `/api/rename-file/${encodeURIComponent(filepath)}` - endpoint doesn't exist
- `normalizeSingleFile()` was calling `/api/normalize-file/${encodeURIComponent(filepath)}` - endpoint doesn't exist

The functions were using standard URL encoding (`encodeURIComponent`) instead of the base64 URL-safe encoding required by the RESTful endpoints, and using incorrect URL patterns.

## Solution

### Changed Files
1. **src/ComicMaintainer.WebApi/wwwroot/js/main.js** - Updated three functions:
   - `processSingleFile()` - Now uses `/api/files/{encodedPath}/process` with base64 URL-safe encoding
   - `renameSingleFile()` - Now uses `/api/files/{encodedPath}/rename` with base64 URL-safe encoding
   - `normalizeSingleFile()` - Now uses `/api/jobs/normalize-selected` endpoint for batch processing single files

2. **src/ComicMaintainer.WebApi/Controllers/JobsController.cs** - Added new endpoint:
   - `POST /api/jobs/normalize-selected` - Accepts array of file paths and starts a normalize job

### Key Changes
1. All functions now use `encodeFilePathForUrl()` helper for proper base64 URL-safe encoding
2. All functions now include authentication headers via `getAuthHeaders()`
3. All functions now handle authentication errors via `handleAuthError()`
4. Normalize operation now uses the batch job endpoint (consistent with other batch operations)

## Verification
- All 341 existing tests pass
- Build succeeds without errors
- Code follows existing patterns used by `deleteSingleFile()` and `viewTags()` functions

## File Actions Status
All file action dropdown items are now functional:
- ✅ Show File Info - Already working
- ✅ View/Edit Tags - Already working
- ✅ Process - **Fixed**
- ✅ Rename - **Fixed**
- ✅ Normalize - **Fixed**
- ✅ Delete - Already working
