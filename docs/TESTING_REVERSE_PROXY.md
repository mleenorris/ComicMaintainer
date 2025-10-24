# Testing Reverse Proxy Setup

This guide provides instructions for manually testing the reverse proxy configuration.

## Automated Tests

First, run the automated test suite:

```bash
cd /home/runner/work/ComicMaintainer/ComicMaintainer
python3 test_reverse_proxy.py
```

Expected output: All 6 tests should pass.

## Manual Testing with Docker Compose

### Test 1: Basic Proxy Setup (Root Path)

1. **Start the services:**
   ```bash
   docker-compose -f docker-compose.nginx-proxy.yml up -d
   ```

2. **Verify health check:**
   ```bash
   curl http://localhost/health
   ```
   Expected: JSON response with `"status": "healthy"`

3. **Test API endpoint:**
   ```bash
   curl http://localhost/api/version
   ```
   Expected: JSON response with version number

4. **Test web interface:**
   Open `http://localhost/` in a browser
   Expected: ComicMaintainer web interface loads

5. **Test real-time updates:**
   - In browser, open Developer Console (F12)
   - Run: `new EventSource('/api/events/stream')`
   - Expected: Connection established without errors

6. **Clean up:**
   ```bash
   docker-compose -f docker-compose.nginx-proxy.yml down
   ```

### Test 2: Subpath Deployment

1. **Create custom nginx config** (`nginx-subpath.conf`):
   ```nginx
   server {
       listen 80;
       server_name localhost;
       
       location /comics/ {
           proxy_pass http://comictagger-watcher:5000/;
           proxy_set_header X-Forwarded-Prefix /comics;
           proxy_set_header X-Forwarded-Host $host;
           proxy_set_header X-Forwarded-Proto $scheme;
           proxy_buffering off;
           proxy_read_timeout 600s;
       }
   }
   ```

2. **Update docker-compose** to mount this config instead

3. **Start and test:**
   ```bash
   curl http://localhost/comics/health
   curl http://localhost/comics/api/version
   ```
   Expected: Both should return valid JSON

4. **Test web interface:**
   Open `http://localhost/comics/` in browser
   Expected: Web interface loads and works correctly

### Test 3: HTTPS with Self-Signed Certificate

1. **Generate self-signed certificate:**
   ```bash
   openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
     -keyout ./ssl/nginx.key -out ./ssl/nginx.crt \
     -subj "/CN=localhost"
   ```

2. **Create HTTPS nginx config:**
   ```nginx
   server {
       listen 443 ssl;
       ssl_certificate /etc/nginx/ssl/nginx.crt;
       ssl_certificate_key /etc/nginx/ssl/nginx.key;
       
       location / {
           proxy_pass http://comictagger-watcher:5000;
           proxy_set_header X-Forwarded-Proto https;
           proxy_set_header X-Forwarded-Host $host;
           proxy_buffering off;
       }
   }
   ```

3. **Mount SSL certs in docker-compose:**
   ```yaml
   nginx:
     volumes:
       - ./ssl:/etc/nginx/ssl:ro
   ```

4. **Test HTTPS:**
   ```bash
   curl -k https://localhost/health
   ```
   Expected: Valid JSON response

## Manual Testing with Native nginx

### Setup

1. **Install nginx:**
   ```bash
   sudo apt-get install nginx  # Ubuntu/Debian
   # or
   sudo yum install nginx      # RHEL/CentOS
   ```

2. **Start ComicMaintainer:**
   ```bash
   docker run -d \
     -p 5000:5000 \
     -v /path/to/comics:/watched_dir \
     -v /path/to/config:/Config \
     -e WATCHED_DIR=/watched_dir \
     iceburn1/comictagger-watcher:latest
   ```

3. **Configure nginx** (`/etc/nginx/sites-available/comicmaintainer`):
   ```nginx
   server {
       listen 8080;
       server_name localhost;
       
       location / {
           proxy_pass http://127.0.0.1:5000;
           proxy_set_header Host $host;
           proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
           proxy_set_header X-Forwarded-Proto $scheme;
           proxy_set_header X-Forwarded-Host $host;
           proxy_buffering off;
           proxy_read_timeout 600s;
       }
   }
   ```

4. **Enable site and reload:**
   ```bash
   sudo ln -s /etc/nginx/sites-available/comicmaintainer /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl reload nginx
   ```

### Test Cases

1. **Direct access (bypassing proxy):**
   ```bash
   curl http://localhost:5000/health
   ```
   Should work: `"status": "healthy"`

2. **Through proxy:**
   ```bash
   curl http://localhost:8080/health
   ```
   Should work: Same response as direct access

3. **Header verification:**
   Check ComicMaintainer logs to verify X-Forwarded headers are received

4. **SSE through proxy:**
   ```bash
   curl -N http://localhost:8080/api/events/stream
   ```
   Should establish connection and wait for events

## Testing Checklist

Use this checklist to verify all functionality works through reverse proxy:

- [ ] Health check endpoint responds
- [ ] Version API endpoint responds
- [ ] File list API endpoint responds
- [ ] Web interface loads in browser
- [ ] Web interface is fully functional (can navigate, search, etc.)
- [ ] Server-Sent Events connection establishes
- [ ] Real-time updates work (test by starting a batch job)
- [ ] Batch processing completes without timeout
- [ ] Static files load correctly (CSS, JS, images)
- [ ] PWA manifest is accessible
- [ ] Service worker registers correctly (check browser console)
- [ ] API requests from frontend use relative URLs
- [ ] No CORS errors in browser console
- [ ] Long-running operations (10+ minutes) don't timeout
- [ ] WebSocket upgrade works (if applicable)

## Testing with Different Proxies

### nginx
See examples above

### Apache
```apache
<VirtualHost *:8080>
    ProxyPreserveHost On
    ProxyPass / http://127.0.0.1:5000/
    ProxyPassReverse / http://127.0.0.1:5000/
    RequestHeader set X-Forwarded-Proto "http"
    ProxyTimeout 600
</VirtualHost>
```

Test: `curl http://localhost:8080/health`

### Caddy
```
localhost:8080 {
    reverse_proxy localhost:5000
}
```

Test: `curl http://localhost:8080/health`

### Traefik
See docker-compose example in docs/REVERSE_PROXY.md

## Troubleshooting Tests

### Issue: 502 Bad Gateway
**Check:** Is ComicMaintainer running?
```bash
curl http://localhost:5000/health
```

### Issue: 504 Gateway Timeout
**Check:** Are proxy timeouts configured?
```bash
# nginx config should have:
proxy_read_timeout 600s;
```

### Issue: SSE doesn't work
**Check:** Is buffering disabled?
```bash
# nginx config should have:
proxy_buffering off;
proxy_cache off;
```

### Issue: 404 on assets with subpath
**Check:** Is X-Forwarded-Prefix set?
```bash
# nginx config should have:
proxy_set_header X-Forwarded-Prefix /comics;
```

## Performance Testing

Test batch processing with timing:

```bash
# Start a batch job and measure time
time curl -X POST http://localhost:8080/api/jobs/process-all

# Check job status
curl http://localhost:8080/api/jobs/<job-id>
```

Verify:
- Job completes without timeout
- Progress updates are received via SSE
- Total time is reasonable for library size

## Load Testing (Optional)

Use `ab` (Apache Bench) or `wrk` to test performance:

```bash
# Install ab
sudo apt-get install apache2-utils

# Test API endpoint
ab -n 1000 -c 10 http://localhost:8080/api/version

# Test file list endpoint
ab -n 100 -c 5 http://localhost:8080/api/files
```

Verify:
- Response times are acceptable
- No errors occur
- Proxy doesn't become bottleneck

## Security Testing

1. **Test header injection:**
   ```bash
   curl -H "X-Forwarded-For: <script>alert('xss')</script>" \
        http://localhost:8080/api/version
   ```
   Verify: No XSS vulnerability

2. **Test path traversal:**
   ```bash
   curl http://localhost:8080/../../../etc/passwd
   ```
   Verify: Returns 404, not file contents

3. **Test authentication bypass:**
   If authentication is configured at proxy level, verify it cannot be bypassed

## Documentation Verification

Verify all examples in documentation work:

- [ ] nginx-reverse-proxy-example.conf examples work
- [ ] docs/REVERSE_PROXY.md examples work
- [ ] docker-compose.nginx-proxy.yml works
- [ ] All curl commands in docs return expected results
- [ ] Configuration examples are valid and complete

## Success Criteria

All tests pass when:
- ✅ Automated tests pass (6/6)
- ✅ Health check responds through proxy
- ✅ API endpoints work through proxy
- ✅ Web interface loads and functions correctly
- ✅ SSE/real-time updates work through proxy
- ✅ Batch processing completes without timeout
- ✅ No console errors in browser
- ✅ Both root path and subpath deployments work
- ✅ HTTPS works when configured
- ✅ All documentation examples work

## Reporting Issues

If tests fail, collect:
1. Proxy configuration file
2. Proxy error logs
3. ComicMaintainer logs (enable DEBUG_MODE=true)
4. Browser console errors (if web interface issue)
5. Output of failing curl commands
6. Network trace (browser DevTools Network tab)

Include all of the above when reporting issues on GitHub.
