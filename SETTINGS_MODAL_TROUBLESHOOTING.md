# Settings Modal Troubleshooting Guide

## Issue: Settings Modal Fails to Open with 404 Error

If the settings modal fails to open when you click on the Settings (⚙️) button, this guide will help you diagnose and resolve the issue.

## Diagnostic Logging

As of the latest version, comprehensive logging has been added to help diagnose settings modal issues. When the modal fails to open, follow these steps to collect diagnostic information:

### How to View Logs

1. Open your browser's developer tools:
   - **Chrome/Edge**: Press `F12` or `Ctrl+Shift+I` (Windows/Linux) or `Cmd+Option+I` (Mac)
   - **Firefox**: Press `F12` or `Ctrl+Shift+K` (Windows/Linux) or `Cmd+Option+K` (Mac)
   - **Safari**: Enable developer menu in Preferences, then press `Cmd+Option+C`

2. Navigate to the **Console** tab

3. Click on the Settings button (⚙️) to trigger the modal

4. Look for log messages with the following prefixes:
   - `[API_URL]` - Shows URL construction details
   - `[AUTH]` - Shows authentication token status
   - `[SETTINGS]` - Shows the settings loading process

### What the Logs Show

The diagnostic logs will display:

1. **URL Construction**: 
   ```
   [API_URL] Building URL - BASE_PATH: '' path: '/api/settings' result: '/api/settings'
   ```
   This confirms the correct URL is being built.

2. **Authentication Status**:
   ```
   [AUTH] Adding authorization header (token present)
   ```
   Or if there's an issue:
   ```
   [AUTH] No JWT token found in localStorage
   ```

3. **Request Details**:
   ```
   [SETTINGS] Fetching settings from URL: /api/settings
   [SETTINGS] Response status: 200 OK: true URL: http://localhost:5000/api/settings
   ```

4. **Settings Data**:
   ```
   [SETTINGS] Settings data loaded: {filename_format: "...", ...}
   [SETTINGS] Log max bytes: 10485760 converted to MB: 10
   [SETTINGS] All settings loaded successfully, opening modal
   ```

5. **Error Information** (if any):
   ```
   [SETTINGS] Failed to load settings. Status: 404 Response: {"error": "..."}
   ```

## Common Issues and Solutions

### Issue 1: 404 Not Found Error

**Symptoms**: 
- Console shows: `[SETTINGS] Response status: 404 OK: false`

**Possible Causes**:
1. The API endpoint is not properly registered
2. Base path configuration is incorrect
3. Reverse proxy configuration issue

**Solutions**:

1. **Check if the API is accessible directly**:
   - Navigate to `http://your-server:port/api/settings` directly in your browser
   - If this returns settings data, the issue is in the frontend
   - If this returns 404, check the server logs

2. **Verify Base Path Configuration**:
   - Check if `BASE_PATH` is set correctly in your deployment
   - For reverse proxy setups, ensure the base path is properly configured

3. **Check Server Logs**:
   - Look for: `GetAllSettings endpoint called`
   - If this log is not present, the request is not reaching the controller

### Issue 2: 401 Unauthorized Error

**Symptoms**:
- Console shows: `[SETTINGS] Response status: 401 OK: false`
- Or: `[AUTH] JWT token expired, redirecting to login`

**Solution**:
- This is expected behavior when your session expires
- Simply log in again to get a new token

### Issue 3: Network Error

**Symptoms**:
- Console shows: `[SETTINGS] Error in openSettings(): NetworkError`

**Possible Causes**:
1. Server is down or not accessible
2. Network connectivity issue
3. CORS policy blocking the request

**Solutions**:
1. Verify the server is running
2. Check your network connection
3. For cross-origin requests, verify CORS is properly configured

### Issue 4: Log Max Bytes Display Issue

**Symptoms**:
- Log max size field shows a very large number instead of megabytes

**Solution**:
- This has been fixed in the latest version
- The log_max_bytes value is now properly converted from bytes to MB
- Update to the latest version to get the fix

## Server-Side Logging

Server-side logs can provide additional context. Check your application logs for:

1. **Endpoint Access**:
   ```
   [INFO] GetAllSettings endpoint called
   ```

2. **Settings Values**:
   ```
   [DEBUG] Returning settings: FilenameFormat=..., IssueNumberPadding=4, ...
   ```

## Reporting Issues

If you continue to experience issues after following this guide, please report the issue with:

1. The complete console log output (copy from Console tab)
2. The server application logs
3. Your deployment configuration (Docker, reverse proxy, etc.)
4. Steps to reproduce the issue

## Fixed Issues

### v2.0+ (Latest)
- ✅ Added comprehensive diagnostic logging
- ✅ Fixed log_max_bytes conversion bug (was not converting from bytes to MB)
- ✅ Improved error messages to show actual response text
- ✅ Added URL construction logging
- ✅ Added authentication token status logging

## See Also

- [DEBUG_LOGGING_GUIDE.md](DEBUG_LOGGING_GUIDE.md) - General debug logging guide
- [REVERSE_PROXY_FIX_SUMMARY.md](REVERSE_PROXY_FIX_SUMMARY.md) - Reverse proxy configuration
- [QUICKSTART.md](QUICKSTART.md) - Getting started guide
