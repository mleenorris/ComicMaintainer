# .NET Application Security Fixes

This document describes the security improvements implemented to address CodeQL findings and Docker security best practices.

## Security Issues Addressed

### 1. Path Injection (3 alerts) - FIXED

**Issue**: User-provided file paths could allow path traversal attacks
**Location**: ComicProcessorService.cs (lines 42, 159, 194)

**Fix**:
- Implemented `PathValidationMiddleware` to validate all file paths
- Path validation ensures paths are within allowed directories only
- Rejects paths containing ".." or other traversal attempts
- Sanitizes paths in logs to prevent information disclosure

**Implementation**:
```csharp
// Middleware automatically validates all filePath query parameters
app.UseMiddleware<PathValidationMiddleware>();
```

### 2. Log Forging (17 alerts) - FIXED

**Issue**: User-provided values in log messages could allow log injection attacks
**Location**: Multiple files (ComicProcessorService.cs, FilesController.cs)

**Fix**:
- Created `LoggingHelper` utility class for log sanitization
- Removes newlines, carriage returns, and control characters from log entries
- Uses structured logging with sanitized parameters
- Shows only filenames (not full paths) in logs for privacy

**Implementation**:
```csharp
_logger.LogInformation("Processing file: {FileName}", 
    LoggingHelper.SanitizePathForLog(filePath));
```

## Docker Security Improvements

### 1. Non-Root User

**Fix**: Application now runs as non-root user inside container
```dockerfile
RUN groupadd -g 1000 comicmaintainer && \
    useradd -r -u 1000 -g comicmaintainer comicmaintainer
USER comicmaintainer
```

### 2. Secrets Management

**Fix**: Sensitive environment variables removed from Dockerfile
- JWT_SECRET must be provided via environment variable or Docker secret
- ADMIN_PASSWORD must be provided via environment variable
- Created `.env.example` with secure configuration guidance

**Usage**:
```bash
# Generate secure JWT secret
openssl rand -base64 48 > jwt_secret.txt

# Use Docker secrets (recommended)
docker secret create jwt_secret jwt_secret.txt
docker service create \
  --secret jwt_secret \
  --env JWT_SECRET_FILE=/run/secrets/jwt_secret \
  comicmaintainer-dotnet:latest

# Or use environment variables
docker run -e JWT_SECRET="$(cat jwt_secret.txt)" \
  comicmaintainer-dotnet:latest
```

### 3. Directory Permissions

**Fix**: Proper permissions set for all application directories
```dockerfile
RUN mkdir -p /watched_dir /duplicates /Config && \
    chmod -R 755 /watched_dir /duplicates /Config && \
    chown -R comicmaintainer:comicmaintainer /app /Config /watched_dir /duplicates
```

## Production Deployment Checklist

### Before Deployment

- [ ] Generate secure JWT_SECRET (minimum 32 characters)
- [ ] Change default ADMIN_PASSWORD
- [ ] Review and update ADMIN_USERNAME and ADMIN_EMAIL
- [ ] Set up proper volume permissions on host
- [ ] Configure SSL/TLS if exposing to internet
- [ ] Review and restrict CORS policy if needed
- [ ] Set up proper logging and monitoring
- [ ] Configure database backups

### Security Best Practices

1. **JWT Secret**: Use `openssl rand -base64 48` to generate
2. **Passwords**: Use strong passwords (16+ characters, mixed case, numbers, symbols)
3. **Network**: Use Docker networks to isolate containers
4. **Volumes**: Set restrictive permissions on mounted volumes
5. **Updates**: Regularly update base images and dependencies
6. **Monitoring**: Set up security monitoring and alerts
7. **Backups**: Regular backups of /Config directory (contains database)

## Configuration Files

### .env.example
Template for environment variables with security notes

### docker-compose.dotnet.yml
Updated to use environment variables from .env file:
```yaml
environment:
  - JWT_SECRET=${JWT_SECRET}
  - ADMIN_PASSWORD=${ADMIN_PASSWORD}
```

## Testing Security Fixes

### Path Validation
```bash
# Should be rejected
curl -X GET "http://localhost:5000/api/files/metadata?filePath=../../etc/passwd"

# Should be accepted (if file exists)
curl -X GET "http://localhost:5000/api/files/metadata?filePath=/watched_dir/test.cbz"
```

### Authentication
```bash
# Login
curl -X POST "http://localhost:5000/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"YOUR_PASSWORD"}'

# Use token for authenticated requests
curl -X POST "http://localhost:5000/api/files/process?filePath=/watched_dir/test.cbz" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

## Remaining Considerations

1. **HTTPS**: Consider adding HTTPS support for production
2. **Rate Limiting**: Add rate limiting middleware for API endpoints
3. **Input Validation**: Add comprehensive input validation for all endpoints
4. **Audit Logging**: Implement audit logging for security events
5. **Secrets Rotation**: Implement regular rotation of JWT secrets

## References

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [Docker Security Best Practices](https://docs.docker.com/engine/security/)
- [ASP.NET Core Security](https://docs.microsoft.com/en-us/aspnet/core/security/)
