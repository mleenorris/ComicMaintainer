# Authelia Integration Guide

This guide explains how to integrate ComicMaintainer with [Authelia](https://www.authelia.com/), a powerful authentication and authorization server.

## What is Authelia?

Authelia is an open-source authentication and authorization server providing two-factor authentication and single sign-on (SSO) for your applications. When deployed as a forward authentication provider with SWAG/Nginx, Authelia:

- Handles user authentication and session management
- Provides two-factor authentication (2FA) support
- Offers single sign-on (SSO) across multiple applications
- Passes authenticated user information to your application via HTTP headers

## How It Works

When Authelia is enabled:

1. **User Access**: User attempts to access ComicMaintainer
2. **Authelia Check**: SWAG/Nginx forwards the request to Authelia
3. **Authentication**: If not authenticated, Authelia prompts for login
4. **Header Injection**: Once authenticated, Authelia adds user information headers:
   - `Remote-User`: Username
   - `Remote-Email`: User's email address
   - `Remote-Name`: User's display name
   - `Remote-Groups`: Comma-separated list of groups
5. **Application Access**: ComicMaintainer reads these headers and creates/updates user accounts automatically

## Prerequisites

- ComicMaintainer deployed behind SWAG or another reverse proxy
- Authelia installed and configured
- Basic understanding of Docker and reverse proxy configuration

## Configuration Steps

### Step 1: Enable Authelia in SWAG

#### For Subdomain Deployment

Edit `/path/to/swag/config/nginx/proxy-confs/comicmaintainer.subdomain.conf`:

```nginx
server {
    # ... existing configuration ...
    
    # Uncomment the line below to enable Authelia
    include /config/nginx/authelia-server.conf;
    
    location / {
        # Uncomment the line below to enable Authelia
        include /config/nginx/authelia-location.conf;
        
        # ... rest of configuration ...
    }
}
```

#### For Subfolder Deployment

Edit `/path/to/swag/config/nginx/proxy-confs/comicmaintainer.subfolder.conf`:

```nginx
location ^~ /comics/ {
    # Uncomment the line below to enable Authelia
    include /config/nginx/authelia-location.conf;
    
    # ... rest of configuration ...
}
```

### Step 2: Enable Authelia in ComicMaintainer

Add the following environment variable to your ComicMaintainer container:

```yaml
version: '3.8'

services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - AUTHELIA_ENABLED=true  # Enable Authelia authentication
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    networks:
      - swag-network
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    external: true
```

### Step 3: Configure Role Mapping (Optional)

By default, all users authenticated via Authelia receive the "User" role. You can map Authelia groups to admin roles:

```yaml
environment:
  - AUTHELIA_ENABLED=true
  - AUTHELIA_DEFAULT_ROLE=User           # Default role for all users
  - AUTHELIA_ADMIN_GROUPS=admins,admin   # Authelia groups that get Admin role
```

### Step 4: Restart Services

```bash
docker restart comicmaintainer
docker restart swag
```

## Environment Variables

ComicMaintainer supports the following Authelia-related environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `AUTHELIA_ENABLED` | `false` | Enable Authelia authentication |
| `AUTHELIA_USER_HEADER` | `Remote-User` | Header containing username |
| `AUTHELIA_EMAIL_HEADER` | `Remote-Email` | Header containing email |
| `AUTHELIA_NAME_HEADER` | `Remote-Name` | Header containing display name |
| `AUTHELIA_GROUPS_HEADER` | `Remote-Groups` | Header containing groups |
| `AUTHELIA_DEFAULT_ROLE` | `User` | Default role (User, Admin, ReadOnly) |
| `AUTHELIA_ADMIN_GROUPS` | _(empty)_ | Comma-separated list of admin groups |

## Security Considerations

### Important Security Notes

1. **Header Trust**: When Authelia is enabled, ComicMaintainer trusts the authentication headers. Ensure:
   - Authelia is properly configured in your reverse proxy
   - The application is NOT directly accessible (bypass the reverse proxy)
   - Your reverse proxy strips/overwrites authentication headers from external requests

2. **Network Isolation**: Deploy ComicMaintainer on an internal Docker network accessible only to SWAG/Nginx:
   ```yaml
   networks:
     swag-network:
       internal: true  # Prevents direct external access
   ```

3. **Role Assignment**: Configure `AUTHELIA_ADMIN_GROUPS` carefully to ensure only trusted groups receive admin access

### Recommended Setup

```yaml
services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    environment:
      - AUTHELIA_ENABLED=true
      - AUTHELIA_ADMIN_GROUPS=admins
    networks:
      - swag-network  # Internal network only
    expose:
      - 5000  # Don't use 'ports' - only expose to internal network
    restart: unless-stopped
```

## User Management

### Automatic User Creation

When a user successfully authenticates via Authelia:
- ComicMaintainer automatically creates a user account if one doesn't exist
- User information (email, display name) is synchronized from Authelia headers
- Users are assigned roles based on their Authelia groups

### Role Mapping

1. **Default Role**: Specified by `AUTHELIA_DEFAULT_ROLE` (default: "User")
2. **Admin Role**: Assigned if user is in any group listed in `AUTHELIA_ADMIN_GROUPS`
3. **Automatic Updates**: User roles are updated on each login based on current group membership

### Available Roles

- **Admin**: Full access to all features and settings
- **User**: Standard access to view and manage comics
- **ReadOnly**: View-only access (no modifications)

## Troubleshooting

### Issue: Users Not Being Authenticated

**Solution:**
1. Check that Authelia includes are uncommented in SWAG config
2. Verify `AUTHELIA_ENABLED=true` is set in ComicMaintainer
3. Check SWAG logs: `docker logs swag 2>&1 | grep authelia`
4. Check ComicMaintainer logs: `docker logs comicmaintainer | grep -i authelia`

### Issue: Users Not Getting Admin Role

**Solution:**
1. Verify user is in the correct Authelia group
2. Check `AUTHELIA_ADMIN_GROUPS` matches your Authelia group names (case-insensitive)
3. Check logs: `docker logs comicmaintainer | grep -i "role"`

### Issue: Headers Not Being Passed

**Solution:**
1. Ensure `authelia-location.conf` is included in the location block
2. Ensure `authelia-server.conf` is included in the server block (subdomain only)
3. Check that Authelia is running and accessible from SWAG
4. Verify SWAG config syntax: `docker exec swag nginx -t`

### Issue: Application Still Showing Login Page

**Solution:**
- Authelia handles authentication at the proxy level, not in the application
- When configured correctly, users won't see the ComicMaintainer login page
- They'll be redirected to Authelia's login page instead
- After authentication, they'll have automatic access to ComicMaintainer

### Issue: "Failed to create or retrieve user" Error

**Solution:**
1. Check database permissions in `/Config` directory
2. Verify the ComicMaintainer database is healthy
3. Check container logs for detailed error messages
4. Ensure `Remote-User` header contains a valid username

## Testing Your Setup

### 1. Test Authelia Configuration

```bash
# Check SWAG nginx configuration
docker exec swag nginx -t

# Check Authelia is running
docker ps | grep authelia

# Test Authelia endpoint
curl -I https://auth.yourdomain.com
```

### 2. Test Authentication Flow

1. Open browser in incognito/private mode
2. Navigate to `https://comics.yourdomain.com` (or your configured URL)
3. You should be redirected to Authelia login page
4. Log in with Authelia credentials
5. You should be redirected back to ComicMaintainer with automatic access

### 3. Verify User Creation

```bash
# Check ComicMaintainer logs for user creation
docker logs comicmaintainer | grep -i "created new user from authelia"

# Check assigned roles
docker logs comicmaintainer | grep -i "assigned role"
```

### 4. Test API Access

```bash
# Attempt to access API endpoint (should redirect to Authelia)
curl -I https://comics.yourdomain.com/api/version
```

## Example Complete Setup

Here's a complete docker-compose.yml showing SWAG, Authelia, and ComicMaintainer:

```yaml
version: '3.8'

services:
  swag:
    image: lscr.io/linuxserver/swag:latest
    container_name: swag
    cap_add:
      - NET_ADMIN
    environment:
      - PUID=1000
      - PGID=1000
      - URL=yourdomain.com
      - SUBDOMAINS=comics,auth
      - VALIDATION=http
    volumes:
      - /path/to/swag/config:/config
    ports:
      - 443:443
      - 80:80
    networks:
      - swag-network
    restart: unless-stopped

  authelia:
    image: authelia/authelia:latest
    container_name: authelia
    environment:
      - TZ=America/New_York
    volumes:
      - /path/to/authelia/config:/config
    networks:
      - swag-network
    expose:
      - 9091
    restart: unless-stopped

  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - AUTHELIA_ENABLED=true
      - AUTHELIA_ADMIN_GROUPS=admins
      - PUID=1000
      - PGID=1000
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    networks:
      - swag-network
    expose:
      - 5000
    restart: unless-stopped

networks:
  swag-network:
    driver: bridge
```

## Disabling Authelia

To disable Authelia and return to JWT authentication:

1. Set `AUTHELIA_ENABLED=false` in ComicMaintainer (or remove the variable)
2. Comment out the Authelia includes in SWAG config
3. Restart both containers

```bash
docker restart comicmaintainer
docker restart swag
```

Users will need to log in using the ComicMaintainer login page with their username/password.

## Additional Resources

- [Authelia Documentation](https://www.authelia.com/docs/)
- [SWAG Documentation](https://docs.linuxserver.io/general/swag)
- [ComicMaintainer Reverse Proxy Guide](REVERSE_PROXY.md)
- [SWAG Configuration Examples](swag-configs/README.md)

## Support

If you encounter issues with Authelia integration:
1. Check the [Troubleshooting](#troubleshooting) section above
2. Review the [ComicMaintainer issues](https://github.com/mleenorris/ComicMaintainer/issues)
3. Check the [Authelia issues](https://github.com/authelia/authelia/issues)
4. Ensure you're using the latest version of all components
