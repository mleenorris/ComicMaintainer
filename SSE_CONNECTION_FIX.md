# SSE Connection Initialization Fix

## Problem Statement
File list shows loading spinner indefinitely. Console doesn't show SSE connection logs, preventing real-time updates from working.

## Root Cause Analysis

The `initEventSource()` function in `src/ComicMaintainer.WebApi/wwwroot/js/main.js` had insufficient error handling that could cause silent failures:

### Issues Identified

1. **Silent Authentication Failures**
   - Authentication status check failures were not logged with sufficient detail
   - Made it difficult to diagnose why SSE wasn't connecting

2. **Swallowed Errors for Authelia Users**
   - When fetching SSE tokens for Authelia users, errors were caught but only logged
   - The function would continue execution and attempt connection without a token
   - This would result in a 401 Unauthorized error from the server with no clear indication of why

3. **Missing Token Validation**
   - The code would attempt to create an EventSource connection even when no authentication token was available
   - The original code path allowed `token` to be `null`/`undefined` and would attempt connection anyway
   
4. **Insufficient Diagnostic Information**
   - No logging to indicate which authentication mode was being used (JWT vs Authelia)
   - No logging to show whether a token was available before connection attempt

## Changes Made

### Enhanced Error Handling

**File**: `src/ComicMaintainer.WebApi/wwwroot/js/main.js`

#### 1. Improved Authentication Status Logging (Lines 289-298)
```javascript
// Check authentication status for both JWT and Authelia before connecting
console.log('SSE: Checking authentication status...');
const isAuthenticated = await checkAuthenticationStatus();
if (!isAuthenticated) {
    console.error('SSE: Authentication check failed - cannot establish SSE connection');
    console.error('SSE: User will be redirected to login page');
    redirectToLogin();
    return;
}
console.log('SSE: Authentication check passed');
```

#### 2. Added Authentication Mode Logging (Lines 304-307)
```javascript
console.log('SSE: Authentication mode -', isAutheliaAuth ? 'Authelia' : 'JWT');
console.log('SSE: Existing token -', token ? 'Present' : 'None');
```

#### 3. Throw Errors for Authelia Token Fetch Failures (Lines 311-324)
```javascript
if (response.ok) {
    const data = await response.json();
    token = data.token;
    console.log('SSE: Received SSE token for Authelia user');
} else {
    console.error('SSE: Failed to get SSE token - status:', response.status);
    console.error('SSE: This may indicate an authentication issue with Authelia');
    // Don't retry immediately if we get auth errors
    if (response.status === 401 || response.status === 403) {
        console.error('SSE: Authentication failed. SSE connection cannot be established.');
        throw new Error(`Authentication failed (${response.status}). Please check Authelia configuration.`);
    }
}
```

#### 4. Validate Token Before Connection (Lines 332-339)
```javascript
// Ensure we have a token before attempting connection
if (!token) {
    console.error('SSE: No authentication token available. Cannot establish SSE connection.');
    console.error('SSE: For JWT auth, check if jwt_token exists in localStorage');
    console.error('SSE: For Authelia auth, check if /api/auth/sse-token endpoint is accessible');
    throw new Error('No authentication token available for SSE connection');
}
```

#### 5. Updated Connection URL (Lines 341-344)
```javascript
// EventSource doesn't support custom headers, so we pass the token as a query parameter
const streamUrl = apiUrl(`/api/events/stream?access_token=${encodeURIComponent(token)}`);

console.log('SSE: Connecting to event stream with authentication token...');
eventSource = new EventSource(streamUrl);
```

## Benefits

### For Users
- Clear console messages indicate exactly what went wrong with SSE connection
- Easier to diagnose authentication issues
- Faster troubleshooting with detailed error messages

### For Developers
- Better visibility into SSE initialization process
- Authentication mode is logged for debugging
- Token availability is checked before connection attempt
- Auth errors are caught and reported properly

## Testing Recommendations

### Manual Testing Steps

1. **Test JWT Authentication Flow**
   ```
   1. Login with JWT credentials
   2. Check console for: "SSE: Authentication mode - JWT"
   3. Check console for: "SSE: Existing token - Present"
   4. Check console for: "SSE: Connecting to event stream with authentication token..."
   5. Verify SSE connection establishes: "SSE: Connected to event stream"
   ```

2. **Test Authelia Authentication Flow**
   ```
   1. Login via Authelia
   2. Check console for: "SSE: Authentication mode - Authelia"
   3. Check console for: "SSE: Fetching token for Authelia-authenticated user"
   4. Check console for: "SSE: Received SSE token for Authelia user"
   5. Verify SSE connection establishes: "SSE: Connected to event stream"
   ```

3. **Test Authentication Failure**
   ```
   1. Clear localStorage or use expired token
   2. Check console for: "SSE: Authentication check failed - cannot establish SSE connection"
   3. Verify user is redirected to login page
   ```

4. **Test Authelia Token Fetch Failure**
   ```
   1. Login via Authelia
   2. Make /api/auth/sse-token return 401
   3. Check console for: "SSE: Failed to get SSE token - status: 401"
   4. Check console for: "SSE: Authentication failed. SSE connection cannot be established."
   5. Verify error is caught and logged in outer try-catch
   ```

## Security Considerations

- **No security vulnerabilities introduced**: Changes only improve error handling and logging
- **No changes to authentication logic**: Only added validation and error reporting
- **Token handling unchanged**: Still uses query parameter for EventSource (required by API)
- **Error messages**: Detailed but don't expose sensitive information

## Files Changed

- `src/ComicMaintainer.WebApi/wwwroot/js/main.js` - Enhanced SSE error handling and logging

## Related Issues

This fix addresses the issue described in the problem statement:
- "file list shows loading and spins. console doesn't ever show sse connection"

The enhanced logging will now show exactly why the SSE connection is failing, making it much easier to diagnose and fix configuration issues.
