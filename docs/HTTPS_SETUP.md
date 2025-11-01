# HTTPS Setup Guide

This guide explains how to configure HTTPS for ComicMaintainer. There are two main approaches:

1. **Direct HTTPS**: The application runs with HTTPS natively (simple, good for development)
2. **Reverse Proxy**: HTTPS is handled by a reverse proxy like Nginx or Traefik (recommended for production)

## Table of Contents
- [Direct HTTPS Setup](#direct-https-setup)
  - [Development (Self-Signed Certificates)](#development-self-signed-certificates)
  - [Production (Trusted Certificates)](#production-trusted-certificates)
- [Reverse Proxy HTTPS](#reverse-proxy-https)
- [Comparison](#comparison)
- [Security Considerations](#security-considerations)
- [Troubleshooting](#troubleshooting)

## Direct HTTPS Setup

### Development (Self-Signed Certificates)

Perfect for development, testing, or private networks where browser certificate warnings are acceptable.

#### Step 1: Generate Self-Signed Certificate

Using Docker (recommended):
```bash
docker run --rm \
  -v /path/to/config:/Config \
  mleenorris/comicmaintainer:latest \
  /generate_self_signed_cert.sh /Config/ssl 365 localhost
```

Or manually with OpenSSL:
```bash
mkdir -p /path/to/config/ssl
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout /path/to/config/ssl/selfsigned.key \
  -out /path/to/config/ssl/selfsigned.crt \
  -subj "/C=US/ST=State/L=City/O=Organization/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,DNS:*.localhost,IP:127.0.0.1"
```

#### Step 2: Run with HTTPS

**Docker CLI:**
```bash
docker run -d \
  --name comictagger-watcher \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/Config/ssl/selfsigned.crt \
  -e SSL_KEYFILE=/Config/ssl/selfsigned.key \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  mleenorris/comicmaintainer:latest
```

**Docker Compose:**
```yaml
version: '3.8'

services:
  comictagger-watcher:
    image: mleenorris/comicmaintainer:latest
    container_name: comictagger-watcher
    restart: unless-stopped
    
    environment:
      - WATCHED_DIR=/watched_dir
      - SSL_CERTFILE=/Config/ssl/selfsigned.crt
      - SSL_KEYFILE=/Config/ssl/selfsigned.key
      - PUID=1000
      - PGID=1000
    
    volumes:
      - /path/to/comics:/watched_dir
      - /path/to/config:/Config
    
    ports:
      - "5000:5000"
```

#### Step 3: Access the Application

Open your browser and navigate to: `https://localhost:5000`

**Note**: You'll see a security warning because the certificate is self-signed. This is expected and safe for development. Click "Advanced" → "Proceed to localhost (unsafe)" or similar depending on your browser.

### Production (Trusted Certificates)

For production deployments, use certificates from a trusted Certificate Authority.

#### Option A: Let's Encrypt with Certbot

**Step 1: Install Certbot**
```bash
# On Ubuntu/Debian
sudo apt-get update
sudo apt-get install certbot

# On CentOS/RHEL
sudo yum install certbot
```

**Step 2: Generate Certificate**
```bash
# HTTP challenge (requires port 80)
sudo certbot certonly --standalone -d your-domain.com

# DNS challenge (works with any port)
sudo certbot certonly --manual --preferred-challenges dns -d your-domain.com
```

Certificates will be stored in `/etc/letsencrypt/live/your-domain.com/`

**Step 3: Run with Let's Encrypt Certificates**
```bash
docker run -d \
  --name comictagger-watcher \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -v /etc/letsencrypt:/certs:ro \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/certs/live/your-domain.com/fullchain.pem \
  -e SSL_KEYFILE=/certs/live/your-domain.com/privkey.pem \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  mleenorris/comicmaintainer:latest
```

**Step 4: Set Up Automatic Renewal**
```bash
# Add cron job for automatic renewal
sudo crontab -e

# Add this line (checks twice daily)
0 0,12 * * * certbot renew --quiet --deploy-hook "docker restart comictagger-watcher"
```

#### Option B: Commercial Certificate

If you have a commercial certificate from a provider (e.g., DigiCert, Comodo):

1. Place your certificate files in a secure location:
   - Certificate: `/path/to/certs/cert.crt`
   - Private key: `/path/to/certs/cert.key`
   - CA bundle (if provided): `/path/to/certs/ca-bundle.crt`

2. Run with your certificates:
```bash
docker run -d \
  --name comictagger-watcher \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -v /path/to/certs:/certs:ro \
  -e WATCHED_DIR=/watched_dir \
  -e SSL_CERTFILE=/certs/cert.crt \
  -e SSL_KEYFILE=/certs/cert.key \
  -e SSL_CA_CERTS=/certs/ca-bundle.crt \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 5000:5000 \
  mleenorris/comicmaintainer:latest
```

### Using Standard HTTPS Port (443)

To use the standard HTTPS port 443 instead of 5000:

```bash
docker run -d \
  --name comictagger-watcher \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  -v /etc/letsencrypt:/certs:ro \
  -e WATCHED_DIR=/watched_dir \
  -e WEB_PORT=443 \
  -e SSL_CERTFILE=/certs/live/your-domain.com/fullchain.pem \
  -e SSL_KEYFILE=/certs/live/your-domain.com/privkey.pem \
  -e PUID=$(id -u) \
  -e PGID=$(id -g) \
  -p 443:443 \
  mleenorris/comicmaintainer:latest
```

Access at: `https://your-domain.com` (no port number needed)

**Note**: Running on port 443 may require root privileges on the host system, or you can use Docker's port mapping from a high port to 443.

## Reverse Proxy HTTPS

For production environments, using a reverse proxy is recommended. See the [Reverse Proxy Setup Guide](REVERSE_PROXY.md) for detailed configuration examples with:

- Nginx
- Traefik
- Apache
- Caddy

Reverse proxies provide additional benefits:
- Automatic certificate management and renewal
- Advanced security features
- Better performance (caching, compression)
- Centralized management for multiple services

## Comparison

| Feature | Direct HTTPS | Reverse Proxy |
|---------|-------------|---------------|
| **Setup Complexity** | Simple | Moderate |
| **Best For** | Single service, development | Multiple services, production |
| **Certificate Management** | Manual | Automatic (with Let's Encrypt) |
| **Additional Features** | Basic HTTPS only | Caching, load balancing, WAF, auth |
| **Performance** | Good | Excellent (with caching) |
| **Port Flexibility** | Any port | Standard ports (80/443) |
| **Maintenance** | Manual cert renewal | Automated |

## Security Considerations

### For Self-Signed Certificates (Development)

⚠️ **Self-signed certificates are NOT suitable for production!**

- Browser warnings will appear for all users
- No protection against man-in-the-middle attacks
- Not trusted by mobile apps or API clients
- Use only for development/testing or private networks

### For Production Certificates

✅ **Best Practices:**

1. **Use Strong Certificates**
   - Minimum 2048-bit RSA or 256-bit ECC
   - Valid certificates from trusted CAs only

2. **Keep Certificates Secure**
   - Store private keys with restricted permissions (600)
   - Never commit private keys to version control
   - Mount certificate volumes as read-only (`:ro`)

3. **Enable Automatic Renewal**
   - Set up cron jobs for Let's Encrypt renewal
   - Test renewal process regularly
   - Monitor certificate expiration dates

4. **Configure Strong TLS**
   - Gunicorn uses secure defaults (TLS 1.2+)
   - Consider using a reverse proxy for advanced TLS configuration

5. **Network Security**
   - Use firewalls to restrict access
   - Enable authentication if exposing to the internet
   - Consider VPN for remote access

6. **Monitor and Update**
   - Keep Docker images updated
   - Monitor security advisories
   - Review logs regularly

### File Permissions

Ensure certificate files have proper permissions:

```bash
# Set restrictive permissions on private key
chmod 600 /path/to/certs/cert.key
chmod 644 /path/to/certs/cert.crt

# If using Docker, ensure the container user can read the files
# The container runs as PUID/PGID (default: 99/100)
chown 99:100 /path/to/certs/*
```

## Troubleshooting

### Issue: "Certificate file not found" Error

**Solution:**
- Verify the certificate file paths are correct
- Check file permissions (readable by the container user)
- Ensure volumes are mounted correctly
- Check container logs: `docker logs comictagger-watcher`

### Issue: Browser Shows "Connection Not Secure"

**For Self-Signed Certificates:**
- This is expected behavior
- Click "Advanced" and proceed (only for development!)

**For Production Certificates:**
- Verify certificate is from a trusted CA
- Check certificate chain is complete (use `SSL_CA_CERTS` for intermediate certificates)
- Verify domain name matches certificate CN/SAN
- Test certificate: `openssl s_client -connect your-domain.com:5000`

### Issue: Application Not Starting with HTTPS

**Solution:**
1. Check logs for specific error messages:
   ```bash
   docker logs comictagger-watcher
   ```

2. Verify certificate format:
   ```bash
   openssl x509 -in /path/to/cert.crt -text -noout
   openssl rsa -in /path/to/cert.key -check
   ```

3. Test certificates match:
   ```bash
   openssl x509 -noout -modulus -in cert.crt | openssl md5
   openssl rsa -noout -modulus -in cert.key | openssl md5
   # Output should match
   ```

4. Check if port is already in use:
   ```bash
   netstat -tlnp | grep 5000
   ```

### Issue: Certificate Expired or About to Expire

**Solution:**
```bash
# Check certificate expiration
openssl x509 -in /path/to/cert.crt -noout -dates

# For Let's Encrypt, renew manually
sudo certbot renew --force-renewal

# Restart container to load new certificate
docker restart comictagger-watcher
```

### Issue: "Mixed Content" Warnings in Browser

**Solution:**
- Ensure all resources are loaded over HTTPS
- Check for hardcoded HTTP URLs in configuration
- Verify `X-Forwarded-Proto` header if behind a proxy

### Issue: Performance Degradation with HTTPS

**Solution:**
- HTTPS has minimal overhead with modern systems
- Consider using HTTP/2 (requires reverse proxy)
- Enable compression at proxy level if applicable
- Check certificate chain isn't too long

## Testing Your HTTPS Setup

### Basic Connectivity
```bash
# Test HTTPS connection
curl -k https://localhost:5000

# Get certificate information
openssl s_client -connect localhost:5000 -showcerts
```

### Verify Certificate
```bash
# Check certificate details
openssl s_client -connect your-domain.com:5000 2>/dev/null | openssl x509 -noout -dates -subject -issuer
```

### SSL Labs Test (Production Only)
For production deployments, test your SSL configuration:
https://www.ssllabs.com/ssltest/

## Additional Resources

- [Certbot Documentation](https://certbot.eff.org/)
- [Let's Encrypt Documentation](https://letsencrypt.org/docs/)
- [OpenSSL Documentation](https://www.openssl.org/docs/)
- [Mozilla SSL Configuration Generator](https://ssl-config.mozilla.org/)
- [Reverse Proxy Setup Guide](REVERSE_PROXY.md)
