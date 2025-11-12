# SSE Connection Error Fix with Authelia

## Problem Statement

When using Authelia as a reverse proxy authentication layer, the Server-Sent Events (SSE) endpoint at `/api/events/stream` was failing with connection errors. The browser console showed:

```
main.js:120 SSE: Connection error, will retry in 5s
```

## Root Cause

The issue stemmed from a limitation of the browser's EventSource API and authentication configuration:

1. **EventSource API Limitation**: The browser's `EventSource` API does not support sending custom HTTP headers, including the `Authorization` header. This means JWT tokens stored in `localStorage` couldn't be passed to the SSE endpoint.

2. **Missing Authorization**: The `EventsController` was not protected with the `[Authorize]` attribute, creating a potential security gap.

3. **Authentication Mismatch**: When Authelia is used, authentication happens at the reverse proxy level via forwarded headers. When JWT is used, authentication happens via Bearer tokens. The SSE endpoint needed to support both scenarios.

## Solution Implemented

### 1. Added Authorization to EventsController

Added the `[Authorize]` attribute to secure the SSE endpoint:

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]  // <-- Added
public class EventsController : ControllerBase
```

### 2. Added Query Parameter Token Support

Modified the JWT Bearer authentication configuration to accept tokens via query parameters (for EventSource compatibility):

```csharp
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        // Allow token to be passed via query string for EventSource/SSE connections
        // EventSource API doesn't support custom headers, so we need this for SSE endpoints
        if (string.IsNullOrEmpty(context.Token) && context.Request.Query.ContainsKey("access_token"))
        {
            context.Token = context.Request.Query["access_token"];
        }
        return Task.CompletedTask;
    },
    // ... other event handlers
};
```

### 3. Updated Client-Side Code

Modified the JavaScript to pass the JWT token as a query parameter when initializing EventSource:

```javascript
function initEventSource() {
    // EventSource doesn't support custom headers, so we pass the token as a query parameter
    // This is only needed for JWT authentication; Authelia uses cookies/headers from the proxy
    const token = localStorage.getItem('jwt_token');
    const streamUrl = token 
        ? apiUrl(`/api/events/stream?access_token=${encodeURIComponent(token)}`)
        : apiUrl('/api/events/stream');
    
    eventSource = new EventSource(streamUrl);
    // ... rest of the code
}
```

## How It Works

### With JWT Authentication (Default Mode)

1. User logs in and receives a JWT token stored in `localStorage`
2. When creating the EventSource connection, the token is appended as a query parameter
3. The JWT Bearer handler extracts the token from `access_token` query parameter
4. The endpoint validates the token and establishes the SSE connection

**Request Flow:**
```
Browser → GET /api/events/stream?access_token=<jwt>
       → JWT Bearer Handler extracts token from query string
       → Token validated
       → SSE connection established
```

### With Authelia (Forward Auth Mode)

1. User authenticates with Authelia at the reverse proxy level
2. Authelia sets authentication cookies
3. EventSource connections automatically include cookies
4. The reverse proxy (SWAG/Nginx) forwards authentication headers
5. The AutheliaAuthenticationHandler validates the user from headers
6. SSE connection is established

**Request Flow:**
```
Browser → GET /api/events/stream (with Authelia session cookie)
       → Reverse Proxy validates with Authelia
       → Proxy forwards request with Remote-User header
       → AutheliaAuthenticationHandler validates user
       → SSE connection established
```

## Security Considerations

### Security Improvements
- ✅ SSE endpoint now requires authentication (via `[Authorize]` attribute)
- ✅ Prevents unauthorized access to real-time updates
- ✅ Works with both JWT and Authelia authentication schemes

### Query Parameter Token Security
- **Concern**: Tokens in query strings can appear in logs
- **Mitigation**: 
  - Only used for SSE endpoint which is a long-lived connection
  - Alternative would require cookies, which EventSource supports but JWT doesn't use by default
  - This is a common pattern for SSE authentication due to EventSource limitations
  - Production deployments should use HTTPS to encrypt the entire request including query parameters

### Best Practices
- Use HTTPS in production to encrypt tokens in transit
- Configure reverse proxy logging to exclude or sanitize `access_token` parameter
- Consider using Authelia for production deployments (uses cookies instead of query parameters)

## SWAG/Nginx Configuration

When using Authelia with SWAG, ensure your configuration includes:

```nginx
server {
    # ... SSL configuration
    
    # Enable Authelia
    include /config/nginx/authelia-server.conf;
    
    location / {
        # Enable Authelia authentication
        include /config/nginx/authelia-location.conf;
        
        include /config/nginx/proxy.conf;
        set $upstream_app comicmaintainer;
        set $upstream_port 5000;
        proxy_pass http://$upstream_app:$upstream_port;
        
        # SSE support - critical for real-time updates
        proxy_buffering off;
        proxy_cache off;
        proxy_http_version 1.1;
        
        # WebSocket/SSE upgrade headers
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

## ComicMaintainer Configuration

### With Authelia

Set these environment variables in your `docker-compose.yml`:

```yaml
services:
  comicmaintainer:
    environment:
      - AUTHELIA_ENABLED=true              # Enable Authelia mode
      - AUTHELIA_DEFAULT_ROLE=User         # Default role for users
      - AUTHELIA_ADMIN_GROUPS=admins,admin # Groups that get Admin role
```

### With JWT (Default)

No additional configuration needed. JWT authentication is enabled by default.

## Testing

### Test JWT Authentication
1. Log in to ComicMaintainer
2. Open browser developer tools → Network tab
3. Look for a connection to `/api/events/stream?access_token=...`
4. Status should be `200 OK` with type `eventsource`
5. Connection should stay open and receive heartbeat messages

### Test Authelia Authentication
1. Configure Authelia in your reverse proxy
2. Set `AUTHELIA_ENABLED=true` in ComicMaintainer
3. Access ComicMaintainer through the reverse proxy
4. Authenticate with Authelia
5. Open browser developer tools → Network tab
6. Look for a connection to `/api/events/stream`
7. Status should be `200 OK` with type `eventsource`
8. Connection should stay open and receive heartbeat messages

## Files Modified

1. **src/ComicMaintainer.WebApi/Controllers/EventsController.cs**
   - Added `[Authorize]` attribute
   - Added `using Microsoft.AspNetCore.Authorization;`

2. **src/ComicMaintainer.WebApi/Program.cs**
   - Added `OnMessageReceived` event handler to JWT Bearer configuration
   - Extracts token from `access_token` query parameter

3. **src/ComicMaintainer.WebApi/wwwroot/js/main.js**
   - Modified `initEventSource()` function
   - Appends JWT token as query parameter when available

## Troubleshooting

### Issue: Still getting SSE connection errors

**Solution:**
1. Check browser console for specific error messages
2. Verify authentication is working (try other API endpoints)
3. Check that the token is present in localStorage (for JWT mode)
4. Verify Authelia headers are being forwarded (for Authelia mode)

### Issue: 401 Unauthorized on SSE endpoint

**Solution (JWT Mode):**
1. Verify JWT token is present in localStorage
2. Check token hasn't expired
3. Log out and log back in to get a fresh token

**Solution (Authelia Mode):**
1. Verify `AUTHELIA_ENABLED=true` is set
2. Check Authelia is properly configured in reverse proxy
3. Verify Authelia authentication headers are being forwarded
4. Check ComicMaintainer logs for authentication details

### Issue: SSE connects but no events received

**Solution:**
1. This is likely unrelated to authentication
2. Check that operations are actually occurring (file processing, job updates)
3. Verify EventBroadcasterService is working
4. Check application logs for event broadcasting

## References

- [EventSource API Documentation](https://developer.mozilla.org/en-US/docs/Web/API/EventSource)
- [SSE Specification](https://html.spec.whatwg.org/multipage/server-sent-events.html)
- [JWT Bearer Authentication](https://docs.microsoft.com/en-us/aspnet/core/security/authentication/)
- [Authelia Forward Authentication](https://www.authelia.com/integration/proxies/introduction/)

## Related Issues

- EventSource API limitation with custom headers
- Server-Sent Events authentication patterns
- Forward authentication with Authelia
