# Password Security Implementation

## Overview

ComicMaintainer implements multiple security measures to protect user passwords from unauthorized access and transmission over insecure connections.

## Security Measures

### 1. Password Storage

**✅ SECURE: Passwords are NEVER stored in plain text**

- Passwords are hashed using ASP.NET Core Identity's built-in password hashing
- Uses PBKDF2 with HMAC-SHA256, 10,000 iterations (default)
- Each password has a unique salt
- Passwords are stored in the database as hashed values only

### 2. Password Transmission

**🔒 ENFORCED: HTTPS required for password endpoints**

By default, ComicMaintainer enforces HTTPS for all authentication endpoints that handle passwords:
- `/api/auth/login`
- `/api/auth/register`
- `/api/auth/setup`
- `/api/auth/change-password`

**How it works:**
- The `HttpsEnforcementMiddleware` checks if the request is over HTTPS
- Supports both direct HTTPS and reverse proxy HTTPS (via `X-Forwarded-Proto` header)
- Returns `426 Upgrade Required` status code if HTTPS is not used
- Logs security warnings when password transmission is attempted over HTTP

**Configuration:**

The HTTPS enforcement can be configured via the `RequireHttpsForAuth` setting in `AppSettings`:

```json
{
  "AppSettings": {
    "RequireHttpsForAuth": true
  }
}
```

Or via environment variable:
```bash
REQUIRE_HTTPS_FOR_AUTH=true
```

**⚠️ WARNING:** Setting `RequireHttpsForAuth` to `false` will disable HTTPS enforcement and allow passwords to be transmitted in plain text over HTTP. This should ONLY be used in isolated development/testing environments, NEVER in production.

### 3. Frontend Warnings

The web interface displays security warnings when accessed over HTTP:

- Login page shows: "⚠️ Security Warning: You are not using HTTPS. Your password will be transmitted in plain text."
- Setup page shows the same warning
- Change password modal (if implemented) would show similar warnings

## Deployment Recommendations

### Production Deployment (REQUIRED)

1. **Use HTTPS** - Deploy with HTTPS enabled via one of these methods:
   
   a. **Reverse Proxy (Recommended)**
   - Use Nginx, Traefik, Caddy, or similar
   - Configure SSL/TLS termination at the proxy
   - Ensure `X-Forwarded-Proto: https` header is set
   - See [SWAG_PROXY_GUIDE.md](SWAG_PROXY_GUIDE.md) for detailed setup

   b. **Direct HTTPS**
   - Configure SSL certificates directly in ASP.NET Core
   - See [HTTPS_IMPLEMENTATION_SUMMARY.md](HTTPS_IMPLEMENTATION_SUMMARY.md)

2. **Keep RequireHttpsForAuth enabled** (default: true)
   - Never set to false in production
   - The middleware provides defense-in-depth even with HTTPS configured

3. **Use strong passwords**
   - Minimum 8 characters
   - Requires uppercase, lowercase, and digit
   - Consider enabling non-alphanumeric requirement for additional security

### Development/Testing

For local development, you have several options:

1. **Use HTTPS with self-signed certificates** (Recommended)
   ```bash
   dotnet dev-certs https --trust
   ```

2. **Disable HTTPS enforcement** (Only for isolated testing)
   ```bash
   export REQUIRE_HTTPS_FOR_AUTH=false
   dotnet run
   ```
   
   Note: You will see a warning: "⚠️ SECURITY WARNING: HTTPS enforcement for authentication is DISABLED"

## Testing

The implementation includes comprehensive tests in `HttpsEnforcementMiddlewareTests.cs`:

- ✅ Blocks password endpoints over HTTP when enforcement enabled
- ✅ Allows password endpoints over HTTPS when enforcement enabled
- ✅ Allows password endpoints over HTTP when enforcement disabled
- ✅ Supports X-Forwarded-Proto header for reverse proxy scenarios
- ✅ Does not block non-password endpoints

Run tests:
```bash
dotnet test --filter "FullyQualifiedName~HttpsEnforcementMiddlewareTests"
```

## Compliance

This implementation helps meet security requirements:

- **OWASP A02:2021 – Cryptographic Failures**: Passwords are hashed, not stored in plain text
- **OWASP A07:2021 – Identification and Authentication Failures**: Enforces secure transmission of credentials
- **PCI DSS 4.1**: Transmission of cardholder data (similar to passwords) must be encrypted
- **GDPR Article 32**: Appropriate security measures including encryption of personal data in transit

## Frequently Asked Questions

**Q: Why am I getting "426 Upgrade Required" errors?**

A: You're trying to access authentication endpoints over HTTP. Configure HTTPS or set `RequireHttpsForAuth=false` for testing only.

**Q: I'm using a reverse proxy, but still getting errors. Why?**

A: Ensure your reverse proxy is setting the `X-Forwarded-Proto: https` header. Check your proxy configuration.

**Q: Can I disable HTTPS enforcement for development?**

A: Yes, but only for isolated development/testing. Set `RequireHttpsForAuth=false` in your configuration. Never do this in production.

**Q: Are API keys affected by this?**

A: No, API key authentication is not affected by HTTPS enforcement. However, you should still use HTTPS to protect API keys in transit.

**Q: What about the /api/auth/setup-required endpoint?**

A: This endpoint is not protected by HTTPS enforcement because it doesn't transmit passwords. It only checks if setup is required.

## Implementation Details

### Files Modified/Created

1. **New Files:**
   - `src/ComicMaintainer.WebApi/Middleware/HttpsEnforcementMiddleware.cs` - Middleware implementation
   - `tests/ComicMaintainer.Tests/Middleware/HttpsEnforcementMiddlewareTests.cs` - Tests
   - `docs/PASSWORD_SECURITY.md` - This documentation

2. **Modified Files:**
   - `src/ComicMaintainer.Core/Configuration/AppSettings.cs` - Added RequireHttpsForAuth setting
   - `src/ComicMaintainer.WebApi/Program.cs` - Registered middleware
   - `src/ComicMaintainer.WebApi/wwwroot/login.html` - Added HTTPS warning
   - `src/ComicMaintainer.WebApi/wwwroot/setup.html` - Added HTTPS warning

### Architecture

```
┌─────────────────────────────────────────────────┐
│  Client (Browser)                                │
│  - Shows warning if not HTTPS                    │
│  - Sends credentials                             │
└─────────────────────────────────────────────────┘
                    │
                    ↓ (HTTPS Required)
┌─────────────────────────────────────────────────┐
│  Reverse Proxy (Optional)                        │
│  - SSL/TLS Termination                           │
│  - Sets X-Forwarded-Proto: https                 │
└─────────────────────────────────────────────────┘
                    │
                    ↓
┌─────────────────────────────────────────────────┐
│  HttpsEnforcementMiddleware                      │
│  - Checks if HTTPS or X-Forwarded-Proto: https   │
│  - Returns 426 if HTTP and protected endpoint    │
│  - Logs security warnings                        │
└─────────────────────────────────────────────────┘
                    │
                    ↓ (Request allowed)
┌─────────────────────────────────────────────────┐
│  AuthController                                  │
│  - Receives credentials over secure connection   │
│  - Passes to AuthService                         │
└─────────────────────────────────────────────────┘
                    │
                    ↓
┌─────────────────────────────────────────────────┐
│  ASP.NET Core Identity (UserManager)             │
│  - Hashes password with PBKDF2                   │
│  - Stores only hashed value                      │
└─────────────────────────────────────────────────┘
                    │
                    ↓
┌─────────────────────────────────────────────────┐
│  Database (SQLite)                               │
│  - Stores hashed passwords only                  │
│  - Never stores plain text                       │
└─────────────────────────────────────────────────┘
```

## Summary

✅ **Passwords are NOT stored in plain text** - They are hashed using industry-standard PBKDF2

✅ **Passwords are NOT sent in plain text** - HTTPS is enforced by default for authentication endpoints

✅ **Users are warned** - Frontend displays warnings when HTTPS is not used

✅ **Configurable** - Can be disabled for isolated testing, but enabled by default

✅ **Well-tested** - Comprehensive test coverage for all scenarios

✅ **Standards-compliant** - Meets OWASP, PCI DSS, and GDPR requirements
