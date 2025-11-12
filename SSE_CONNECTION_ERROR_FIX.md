# SSE Connection Error Fix - Improved Retry Logic

## Issue Description

The application was experiencing persistent Server-Sent Events (SSE) connection errors that caused the EventSource to fail and retry every 5 seconds indefinitely. The browser console showed:

```
main.js:266 SSE: Connection error, will retry in 5s
EventSource {readyState: 2, ...}  // readyState 2 = CLOSED
```

This created an infinite retry loop that:
- Generated excessive console warnings
- Created unnecessary network traffic
- Didn't handle authentication failures properly (for both JWT and Authelia)
- Provided no feedback to users about the issue
- Made debugging difficult

## Root Causes

1. **No Authentication Check**: The code didn't check if authentication was still valid before attempting connection
2. **JWT-Only Validation**: Only checked JWT token expiration, ignoring Authelia session expiration
3. **Infinite Retries**: No maximum retry limit - would retry forever even if the issue was permanent
4. **Fixed Retry Delay**: Always retried after 5 seconds, regardless of failure count
5. **Poor Error Detection**: Didn't check EventSource readyState to understand connection status
6. **Limited Logging**: Minimal information about retry attempts and delays

## Solution Implemented

### 1. Authentication Status Validation (JWT and Authelia)

**New Helper Function:**
```javascript
async function checkAuthenticationStatus() {
    try {
        // First check if using Authelia authentication
        const isAutheliaAuth = localStorage.getItem('authelia_authenticated') === 'true';
        
        if (isAutheliaAuth) {
            // For Authelia, check server auth status to verify session is still valid
            const response = await fetch(apiUrl('/api/auth/status'), {
                credentials: 'include' // Important for Authelia cookies
            });
            
            if (response.ok) {
                const authStatus = await response.json();
                if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                    return true; // Authelia session is still valid
                }
            }
            // If we get here, Authelia session is invalid
            console.warn('[AUTH] Authelia session expired or invalid');
            return false;
        } else {
            // For JWT authentication, check if token exists and is not expired
            const token = localStorage.getItem('jwt_token');
            if (!token) {
                console.warn('[AUTH] No JWT token found');
                return false;
            }
            
            if (isTokenExpired(token)) {
                console.warn('[AUTH] JWT token expired');
                return false;
            }
            
            return true; // JWT token is valid
        }
    } catch (error) {
        console.error('[AUTH] Error checking authentication status:', error);
        return false;
    }
}
```

**Before Connection:**
```javascript
// Check authentication status for both JWT and Authelia before connecting
const isAuthenticated = await checkAuthenticationStatus();
if (!isAuthenticated) {
    console.warn('SSE: Authentication invalid, redirecting to login');
    redirectToLogin();
    return;
}
```

**During Error Handling:**
```javascript
// Check authentication status for both JWT and Authelia
const isAuthenticated = await checkAuthenticationStatus();
if (!isAuthenticated) {
    console.warn('SSE: Authentication expired during connection');
    redirectToLogin();
    return;
}
```

**Benefits:**
- Works with both JWT and Authelia authentication
- Prevents connection attempts with expired/invalid authentication
- Detects Authelia session expiration by checking server auth status
- Redirects users to login immediately when authentication fails
- Avoids infinite retry loops with invalid credentials

**How It Works:**

For **JWT Authentication**:
1. Checks if JWT token exists in localStorage
2. Validates token hasn't expired using `isTokenExpired()`
3. Returns true if valid, false if expired or missing

For **Authelia Authentication**:
1. Detects Authelia mode by checking `authelia_authenticated` flag in localStorage
2. Makes API call to `/api/auth/status` with cookies
3. Server validates Authelia session via forwarded headers from reverse proxy
4. Returns true if session is valid, false if expired or invalid

This ensures both authentication methods are properly validated before and during SSE connection attempts.

### 2. Exponential Backoff

**Configuration:**
```javascript
const EVENT_SOURCE_RECONNECT_DELAY = 5000; // Initial delay: 5 seconds
const EVENT_SOURCE_MAX_RETRY_DELAY = 60000; // Maximum delay: 60 seconds
let eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
```

**Implementation:**
```javascript
eventSourceReconnectTimer = setTimeout(() => {
    initEventSource();
    // Increase delay for next retry (exponential backoff)
    eventSourceRetryDelay = Math.min(eventSourceRetryDelay * 1.5, EVENT_SOURCE_MAX_RETRY_DELAY);
}, eventSourceRetryDelay);
```

**Retry Schedule:**
- Attempt 1: 5 seconds
- Attempt 2: 7.5 seconds
- Attempt 3: 11.25 seconds
- Attempt 4: 16.875 seconds
- Attempt 5: 25.3 seconds
- Attempt 6: 38 seconds
- Attempt 7+: 60 seconds (capped)

**Benefits:**
- Reduces server load during extended outages
- Gives transient issues time to resolve
- More aggressive early retries for quick recovery
- Conservative later retries to avoid overwhelming the server

### 3. Maximum Retry Limit

**Configuration:**
```javascript
const EVENT_SOURCE_MAX_RETRIES = 20; // Stop trying after 20 consecutive failures
let eventSourceRetryCount = 0;
```

**Implementation:**
```javascript
if (eventSourceRetryCount >= EVENT_SOURCE_MAX_RETRIES) {
    console.error('SSE: Maximum retry attempts reached. Please refresh the page or check your connection.');
    showMessage('Real-time updates unavailable. Please refresh the page.', 'error');
    return;
}
```

**Benefits:**
- Prevents infinite retry loops
- Provides clear feedback to users
- Stops wasting resources after reasonable attempt count
- Users know they need to take action

### 4. Improved Error State Detection

**Implementation:**
```javascript
eventSource.onerror = (error) => {
    // EventSource automatically attempts to reconnect, but readyState tells us the status
    // readyState 0 = CONNECTING, 1 = OPEN, 2 = CLOSED
    const state = eventSource.readyState;
    
    if (state === EventSource.CLOSED) {
        // Connection permanently failed, handle retry logic
        eventSourceRetryCount++;
        console.warn(`SSE: Connection closed (attempt ${eventSourceRetryCount}/${EVENT_SOURCE_MAX_RETRIES}), will retry in ${eventSourceRetryDelay/1000}s`);
        // ... retry logic
    } else if (state === EventSource.CONNECTING) {
        // EventSource is attempting to reconnect automatically
        console.log('SSE: Reconnecting...');
    }
};
```

**Benefits:**
- Distinguishes between temporary and permanent failures
- Avoids interfering with EventSource's automatic reconnection
- Only intervenes when connection is truly closed

### 5. Enhanced Logging

**Before:**
```javascript
console.warn('SSE: Connection error, will retry in 5s', error);
```

**After:**
```javascript
console.warn(`SSE: Connection closed (attempt ${eventSourceRetryCount}/${EVENT_SOURCE_MAX_RETRIES}), will retry in ${eventSourceRetryDelay/1000}s`);
```

**Benefits:**
- Shows retry attempt count (e.g., "attempt 5/20")
- Shows actual delay duration (e.g., "will retry in 11.25s")
- Easier to track retry behavior in console
- Better debugging and issue diagnosis

### 6. Success Recovery

**Implementation:**
```javascript
eventSource.onopen = () => {
    console.log('SSE: Connected to event stream');
    
    // Reset retry counters on successful connection
    eventSourceRetryCount = 0;
    eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
    
    // ... rest of onopen logic
};
```

**Benefits:**
- Fresh start after successful connection
- Next failure starts with short delay
- Doesn't penalize future connections for past failures

### 7. Cleanup Function Update

**Before:**
```javascript
function cleanupEventSource() {
    if (eventSourceReconnectTimer) {
        clearTimeout(eventSourceReconnectTimer);
        eventSourceReconnectTimer = null;
    }
    if (eventSource) {
        eventSource.close();
        eventSource = null;
    }
}
```

**After:**
```javascript
function cleanupEventSource() {
    if (eventSourceReconnectTimer) {
        clearTimeout(eventSourceReconnectTimer);
        eventSourceReconnectTimer = null;
    }
    if (eventSource) {
        eventSource.close();
        eventSource = null;
    }
    // Reset retry counters
    eventSourceRetryCount = 0;
    eventSourceRetryDelay = EVENT_SOURCE_RECONNECT_DELAY;
}
```

**Benefits:**
- Clean state when connection is intentionally closed
- Next connection starts with fresh retry counters

## Error Flow Comparison

### Before Fix

```
Connection Attempt → Error → Close → Wait 5s → Retry
                     ↓
              (No token check)
              (No retry limit)
              (Fixed delay)
                     ↓
              Repeat forever
```

### After Fix

```
Check Token → Expired? → Redirect to Login
     ↓
  Not Expired
     ↓
Connection Attempt → Success → Reset Counters → Stay Connected
                     ↓
                   Error
                     ↓
              Check readyState
                     ↓
         CLOSED? → Check Token Again → Expired? → Redirect
                     ↓                     ↓
                Not Expired            Not Expired
                     ↓                     ↓
              Increment Counter     Close Connection
                     ↓                     ↓
         Reached Max (20)? → Yes → Show Error, Stop
                     ↓
                    No
                     ↓
         Wait (exponential backoff) → Retry
                     ↓
         Delay increases: 5s → 7.5s → 11.25s → ... → 60s (max)
```

## Testing Results

✅ **JavaScript Syntax**: Validated with Node.js
✅ **.NET Build**: Successful compilation
✅ **CodeQL Security**: 0 alerts found
✅ **Code Review**: No issues identified

## Impact Analysis

### Positive Impacts

1. **User Experience**
   - Clear error messages when connection fails
   - Automatic login redirect for expired tokens
   - Less console noise with improved logging

2. **Performance**
   - Reduced network traffic with exponential backoff
   - Lower server load during extended outages
   - Stops retrying after reasonable attempts

3. **Debugging**
   - Better visibility into retry behavior
   - Clear logging of attempt counts and delays
   - Easier to diagnose connection issues

4. **Security**
   - Detects and handles expired tokens properly
   - Prevents indefinite retry loops
   - Proper authentication state management

### No Breaking Changes

- Maintains backward compatibility
- Works with both JWT and Authelia authentication
- Existing functionality unchanged
- Only improves error handling

## Configuration

The retry behavior can be adjusted by modifying these constants:

```javascript
const EVENT_SOURCE_RECONNECT_DELAY = 5000; // Initial delay: 5 seconds
const EVENT_SOURCE_MAX_RETRY_DELAY = 60000; // Maximum delay: 60 seconds
const EVENT_SOURCE_MAX_RETRIES = 20; // Stop trying after 20 consecutive failures
```

### Recommended Settings

**For Fast Networks (Default):**
- Initial Delay: 5 seconds
- Max Delay: 60 seconds
- Max Retries: 20

**For Slow/Unreliable Networks:**
- Initial Delay: 10 seconds
- Max Delay: 120 seconds
- Max Retries: 30

**For Development/Testing:**
- Initial Delay: 2 seconds
- Max Delay: 30 seconds
- Max Retries: 10

## Troubleshooting

### Issue: Still seeing connection errors

**Solution:**

**For JWT Authentication:**
1. Check browser console for specific error messages
2. Verify token is present in localStorage: `localStorage.getItem('jwt_token')`
3. Check if token is expired: Open Developer Tools → Application → Local Storage
4. Try logging out and back in to get a fresh token
5. Check network tab for actual HTTP response from `/api/events/stream`

**For Authelia Authentication:**
1. Check if `authelia_authenticated` flag is set: `localStorage.getItem('authelia_authenticated')`
2. Verify Authelia session is still valid by accessing `/api/auth/status`
3. Check that Authelia cookies are being sent (Network tab → Cookies)
4. Verify reverse proxy is forwarding authentication headers correctly
5. Check Authelia logs for session expiration or authentication errors

### Issue: Gets to max retries and stops

**Solution:**
1. This indicates a persistent connection problem
2. Check server logs for authentication errors
3. Verify the `/api/events/stream` endpoint is accessible
4. Ensure proper CORS/proxy configuration
5. **For Authelia**: Verify reverse proxy configuration and header forwarding
6. Refresh the page to reset and try again

### Issue: Authentication keeps expiring (JWT)

**Solution:**
1. Check token expiration time (default is usually 24 hours)
2. Implement token refresh mechanism if needed
3. Increase token lifetime in backend configuration
4. Consider using Authelia which uses session cookies instead

### Issue: Authentication keeps expiring (Authelia)

**Solution:**
1. Check Authelia session configuration (session.expiration, session.inactivity)
2. Verify cookies are not being cleared by browser or extensions
3. Ensure reverse proxy is maintaining session state
4. Check if Authelia session is being refreshed on activity
5. Review Authelia logs for session expiration events

## Files Modified

- **src/ComicMaintainer.WebApi/wwwroot/js/main.js**
  - Added `checkAuthenticationStatus()` helper function for both JWT and Authelia
  - Made `initEventSource()` function async to support authentication checks
  - Enhanced authentication validation before connection attempts
  - Made `eventSource.onerror` handler async
  - Improved error handling to check both JWT and Authelia authentication
  - Updated `cleanupEventSource()` to reset counters
  - Added detailed logging throughout
- **SSE_CONNECTION_ERROR_FIX.md**
  - Updated documentation to include Authelia authentication support
  - Added troubleshooting sections for both JWT and Authelia

## Related Documentation

- [SSE_AUTHELIA_FIX.md](SSE_AUTHELIA_FIX.md) - Original SSE authentication fix
- [EventSource API](https://developer.mozilla.org/en-US/docs/Web/API/EventSource)
- [Server-Sent Events Spec](https://html.spec.whatwg.org/multipage/server-sent-events.html)

## Security Summary

✅ **No new vulnerabilities introduced**
✅ **Improves security posture by:**
- Detecting expired tokens before connection attempts
- Preventing infinite retry loops
- Properly handling authentication failures
- Providing secure error messages

## Future Enhancements

Possible future improvements (not included in this fix):

1. **Token Refresh**: Automatically refresh JWT tokens before expiration
2. **Connection Health Monitoring**: Track connection quality metrics
3. **Adaptive Retry Strategy**: Adjust retry behavior based on error types
4. **User Preferences**: Allow users to configure retry behavior
5. **Offline Detection**: Detect network offline state and pause retries
