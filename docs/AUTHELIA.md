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
   - `Remote-Email`: User's email address (optional; a new account without it gets `<username>@authelia.local`)
   - `Remote-Name`: User's display name (optional; defaults to the username)
   - `Remote-Groups`: Comma-separated list of groups
5. **Application Access**: ComicMaintainer reads these headers and creates/updates user accounts automatically.
   It only accepts them on connections from the reverse proxy, whose addresses you list in `AUTHELIA_TRUSTED_PROXIES`.

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

Add the following environment variables to your ComicMaintainer container. `AUTHELIA_TRUSTED_PROXIES` is
**required**: it lists the addresses of the reverse proxy that is allowed to send the Authelia headers, and the
container refuses to start without it (see [Choosing `AUTHELIA_TRUSTED_PROXIES`](#choosing-authelia_trusted_proxies)).

```yaml
version: '3.8'

services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    container_name: comicmaintainer
    environment:
      - WATCHED_DIR=/watched_dir
      - AUTHELIA_ENABLED=true  # Enable Authelia authentication
      - AUTHELIA_TRUSTED_PROXIES=172.18.0.0/16  # Required: subnet of swag-network (docker network inspect swag-network)
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
  - AUTHELIA_TRUSTED_PROXIES=172.18.0.0/16  # Required (see Step 2)
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
| `AUTHELIA_TRUSTED_PROXIES` | _(none, required)_ | Comma-separated IP addresses and/or CIDR ranges, IPv4 or IPv6 (e.g. `172.18.0.0/16, 10.0.0.5, fd00::/8`), of the reverse proxies allowed to send the Authelia headers. **Required when `AUTHELIA_ENABLED=true`**: the app refuses to start if it is missing or contains an invalid entry. Hostnames are not supported. Ignored when Authelia is disabled. See [Choosing `AUTHELIA_TRUSTED_PROXIES`](#choosing-authelia_trusted_proxies) |
| `AUTHELIA_USER_HEADER` | `Remote-User` | Header containing username |
| `AUTHELIA_EMAIL_HEADER` | `Remote-Email` | Header containing email |
| `AUTHELIA_NAME_HEADER` | `Remote-Name` | Header containing display name |
| `AUTHELIA_GROUPS_HEADER` | `Remote-Groups` | Header containing groups |
| `AUTHELIA_DEFAULT_ROLE` | `User` | Default role (User, Admin, ReadOnly) |
| `AUTHELIA_ADMIN_GROUPS` | _(empty)_ | Comma-separated list of Authelia groups (case-insensitive) whose members get the Admin role. Without admin groups, every signed-in user who is not ReadOnly can administer ComicMaintainer; see [Role Mapping](#role-mapping) |

## Security Considerations

### Important Security Notes

1. **Header Trust**: The `Remote-*` headers decide who the user is, and any HTTP client can send them. ComicMaintainer
   therefore only honours them on connections that come **directly from an address in `AUTHELIA_TRUSTED_PROXIES`**:
   - On a request from any other address (for example a client that reaches the container without going through
     the reverse proxy), the headers are ignored and the request is handled as if it had none: it is anonymous
     unless it carries a valid JWT. The first such request from each address is logged as a warning:
     `Ignoring Authelia headers from 203.0.113.7: the address is not in AUTHELIA_TRUSTED_PROXIES. ...`
   - The check uses the address of the TCP connection, i.e. the proxy itself, never `X-Forwarded-For`, which clients
     can forge. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on the container: it makes ASP.NET Core take
     the connection address from `X-Forwarded-For` sent by any client, which defeats the allowlist.
   - The setting is required: with `AUTHELIA_ENABLED=true`, the app refuses to start when `AUTHELIA_TRUSTED_PROXIES`
     is missing, empty or contains an entry that is not an IP address or CIDR range (see
     [Troubleshooting](#troubleshooting)). This is a breaking change for existing Authelia deployments; see the
     [upgrade note](../CHANGELOG.md#breaking-changes).
   - Every address in the list can sign in as any user, so keep the list as narrow as possible, and make sure your
     reverse proxy removes or overwrites `Remote-*` headers sent by clients (SWAG's `authelia-location.conf` does)
     and only forwards requests that Authelia has authenticated.

2. **Network Isolation**: Deploy ComicMaintainer on an internal Docker network accessible only to SWAG/Nginx:
   ```yaml
   networks:
     swag-network:
       internal: true  # Prevents direct external access
   ```

3. **Role Assignment**: Set `AUTHELIA_ADMIN_GROUPS`, and configure it carefully to ensure only trusted groups receive
   admin access. Without admin groups, every signed-in user who is not ReadOnly can administer ComicMaintainer (see
   [Role Mapping](#role-mapping))

### Choosing `AUTHELIA_TRUSTED_PROXIES`

The value is the address your **reverse proxy connects to ComicMaintainer from**, as seen by the ComicMaintainer
container: not your users' addresses, and not the public address of the proxy. Entries are comma-separated IPv4 or
IPv6 addresses (`172.18.0.5`, `fd00::5`) or CIDR ranges written with their network address (`172.18.0.0/16`, not
`172.18.0.5/16`). Hostnames such as `swag` are not supported: use the Docker network's subnet, or give the proxy a
fixed IP address.

**Docker (SWAG or another proxy container on the same network as ComicMaintainer)**, two options:

- **The network's subnet** (simplest): `docker network inspect swag-network --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}'`
  prints e.g. `172.18.0.0/16`; if the network has IPv6 enabled, add its IPv6 subnet too. This trusts every
  container on that network, so prefer a network that only the proxy and ComicMaintainer share, or the next option.
- **A fixed IP address for the proxy** (narrowest): give SWAG a static address on a network with a defined subnet
  and trust only that address (see the [complete example](#example-complete-setup)). For an existing external
  network, create it with a subnet: `docker network create --subnet 172.30.0.0/24 swag-network`.

Do not publish ComicMaintainer's port (`ports:`) when Authelia is enabled; use `expose:` so that only containers on
the network can reach it. Connections to a published port that Docker relays (for example from the Docker host
itself) arrive from the network's gateway address (e.g. `172.18.0.1`), which is inside the subnet and would
therefore be trusted.

**Proxy running on the Docker host** (not in a container): its connections arrive from the gateway address of
ComicMaintainer's network (`docker network inspect <network> --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}'`).
Trust that single address, and publish the port on loopback only (`127.0.0.1:5000:5000`) so that other machines
cannot reach it. Without Docker, a proxy on the same machine connects from `127.0.0.1` (or `::1`).

**Kubernetes**: the connections come from the ingress controller pods (e.g. ingress-nginx), whose addresses change
when they are rescheduled. Use the cluster's pod CIDR (`kubectl get nodes -o jsonpath='{.items[*].spec.podCIDR}'`
lists the range of each node; commonly inside `10.244.0.0/16`, or `10.42.0.0/16` on k3s; network plugins with their
own IP pools, such as Calico, show the range in their pool configuration instead). Because that trusts every
pod, add a NetworkPolicy that only lets the ingress controller's namespace reach ComicMaintainer (this needs a
network plugin that enforces NetworkPolicies, such as Calico or Cilium):

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: comicmaintainer-from-ingress
  namespace: comicmaintainer
spec:
  podSelector:
    matchLabels:
      app: comicmaintainer
  policyTypes: [Ingress]
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: ingress-nginx
```

If the ingress controller runs with `hostNetwork: true`, its connections come from the node addresses instead.

Never use `0.0.0.0/0` or `::/0`: they trust every client. If the value is wrong, users get the login page (or 401
errors) and the log shows `Ignoring Authelia headers from <address>`; once you have checked that this address
really is your proxy, add it (or its subnet).

### Recommended Setup

```yaml
services:
  comicmaintainer:
    image: iceburn1/comictagger-watcher:latest
    environment:
      - AUTHELIA_ENABLED=true
      - AUTHELIA_TRUSTED_PROXIES=172.18.0.0/16  # Subnet of swag-network (or the proxy's fixed IP)
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
4. **No admin groups configured**: The administration features (for example settings, backups and scheduled jobs)
   normally require the Admin role. If no admin groups are configured at all (neither `AUTHELIA_ADMIN_GROUPS` nor
   `AutheliaSettings:AdminGroups` in `appsettings.json`), ComicMaintainer cannot tell which Authelia users are
   administrators, so it lets **every signed-in user who is not ReadOnly administer it**, including users of local
   accounts, rather than locking everyone out. Once admin groups are configured, only users with the Admin role can
   administer.

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
5. Look for `Ignoring Authelia headers from` in the ComicMaintainer logs (see below)

### Issue: Container Exits at Startup with an `AUTHELIA_TRUSTED_PROXIES` Error

The log shows `AUTHELIA_TRUSTED_PROXIES is required when Authelia is enabled ...` or
`Invalid AUTHELIA_TRUSTED_PROXIES entry '<entry>' ...`.

**Solution:**
1. Authelia mode requires the list of trusted reverse proxies (see the [upgrade note](../CHANGELOG.md#breaking-changes)).
   Set `AUTHELIA_TRUSTED_PROXIES` as described in [Choosing `AUTHELIA_TRUSTED_PROXIES`](#choosing-authelia_trusted_proxies)
2. For an invalid entry, fix the entry named in the message: it must be an IP address or a CIDR range written with
   its network address (`172.18.0.0/16`, not `172.18.0.5/16`); hostnames and ports are not accepted, and entries
   are separated by commas
3. Restart the container

### Issue: Login Page or 401 Errors, Log Shows "Ignoring Authelia headers from ..."

**Solution:**
1. The message names the address the request came from. If that is your reverse proxy, add the address (or its
   network's subnet) to `AUTHELIA_TRUSTED_PROXIES` and restart the container
2. If it is not your proxy, a client reached ComicMaintainer without going through the proxy and its headers were
   rejected: make sure the port is not published and the network is isolated
3. Each address is reported as a warning once; later requests from it are logged at Debug level

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
      swag-network:
        ipv4_address: 172.30.0.10  # Fixed address, trusted by ComicMaintainer
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
      - AUTHELIA_TRUSTED_PROXIES=172.30.0.10  # Only SWAG may send the Authelia headers
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
    ipam:
      config:
        - subnet: 172.30.0.0/24
          ip_range: 172.30.0.128/25  # Dynamic addresses for the other containers, away from SWAG's fixed one
```

## Disabling Authelia

To disable Authelia and return to JWT authentication:

1. Set `AUTHELIA_ENABLED=false` in ComicMaintainer (or remove the variable); `AUTHELIA_TRUSTED_PROXIES` is ignored
   while Authelia is disabled, so it can stay set
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
