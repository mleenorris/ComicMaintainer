# Security Test Coverage

This document lists where ComicMaintainer's security behaviour is tested and what is not covered by automated tests. For the policy and the scans, see [SECURITY.md](../SECURITY.md) and the [Security Scanning Guide](SECURITY_SCANNING.md).

## Running the Tests

```bash
# Everything in the test project
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj

# The in-process API tests only
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj --filter "Category=Integration"

# One area
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj --filter "FullyQualifiedName~Security"
```

Every pull request into `dotnet` runs the unit and integration suites. See the [Testing Policy](TESTING_POLICY.md).

## What Is Tested

Paths are relative to `tests/ComicMaintainer.Tests/`.

| Area | Behaviour | Tests |
|---|---|---|
| Sign-in | Lockout, uniform failure response, published passwords, password confirmation | `Services/AuthServiceSignInTests.cs`, `Services/AuthServiceTests.cs`, `Integration/AuthResponseTests.cs` |
| First-run setup | Setup code, only with no accounts, never under Authelia | `Services/AuthServiceSetupTests.cs`, `Services/SetupTokenProviderTests.cs` |
| Account creation | Only administrators can register accounts | `Integration/RegisterAuthorizationIntegrationTests.cs`, `Controllers/AuthControllerCoverageTests.cs` |
| Account administration | Administrators only; the administrator's own password for changes that grant access; no changes to one's own account or to the last active administrator; sessions and API keys end on role changes, resets and disabling; deletion removes only the account's own data; the Authelia rules | `Services/UserAdministrationServiceTests.cs`, `Controllers/UsersControllerTests.cs`, `Integration/UsersApiIntegrationTests.cs` |
| Session tokens | Revocation, security stamp, stream tokens | `Services/AuthSessionValidatorTests.cs`, `Services/AuthServiceStreamTokenTests.cs`, `Authentication/JwtSessionEventsTests.cs` |
| Signing secret | Published secrets ignored, generated key handling | `Integration/JwtSigningSecretIntegrationTests.cs` |
| API keys | Header only, hashed storage, roles, revocation | `Authentication/ApiKeyAuthenticationHandlerTests.cs`, `Authentication/ApiKeysTests.cs`, `Integration/ApiKeyAuthenticationIntegrationTests.cs` |
| OPDS Basic sign-in | Password and key handling, Authelia rule, attempt budget, remembered credentials | `Authentication/OpdsBasicAuthenticationHandlerTests.cs`, `Authentication/AuthenticationAttemptLimiterTests.cs`, `Middleware/OpdsFeatureGateMiddlewareTests.cs`, `Integration/OpdsCatalogIntegrationTests.cs` |
| Authelia | Trusted proxies, forged headers, role mapping | `Authentication/AutheliaAuthenticationHandlerTests.cs`, `Authentication/AutheliaTrustedProxyTests.cs`, `Authentication/TrustedProxyAllowlistTests.cs`, `Integration/AutheliaTrustedProxyIntegrationTests.cs` |
| Authorization | Write endpoints require a policy; settings are administrator-only | `Authorization/WriteOperationAuthorizationConventionTests.cs`, `Integration/SettingsAuthorizationIntegrationTests.cs` |
| Per-user email data | Who may see and change devices, subscriptions and deliveries | `Models/EmailAccessScopeTests.cs` |
| Path confinement | Requests stay inside the library, links included | `Middleware/PathValidationMiddlewareTests.cs`, `Utilities/LibraryPathValidatorTests.cs`, `Integration/BatchPathConfinementTests.cs`, `Fuzz/FuzzTests.cs` |
| CORS | Allowed origins, credentials | `Security/CorsSecurityTests.cs` |
| Security headers | CSP, HSTS with `X-Forwarded-Proto`, no-store on the API | `Security/SecurityHeadersTests.cs` |
| Rate limiting | Per-client limits, trusted proxies, the sign-in limit, `Retry-After` | `Security/RateLimitingSecurityTests.cs`, `Security/AuthenticationRateLimitTests.cs`, `Security/RateLimitRejectionTests.cs`, `Integration/RateLimitingIntegrationTests.cs` |
| Outbound requests | SSRF guard on series images, redirects, address ranges | `Utilities/PublicNetworkGuardTests.cs`, `Services/SeriesImageStoreTests.cs` |
| Notification secrets | Masking, write-only fields, the ntfy topic | `Notifications/NotificationChannelTests.cs`, `Notifications/NotificationSettingsTests.cs` |
| Hostile archives | Size, ratio, entry-count and RAR header limits | `Utilities/ArchiveEntryReaderTests.cs` |
| Deployment files | The Kubernetes example, documented variables | `Startup/KubernetesManifestTests.cs`, `Startup/DocumentedEnvironmentVariablesTests.cs` |

Some tests are skipped on platforms that cannot run them, such as symlink tests on a machine that cannot create symlinks. They run on the Linux CI runners.

The container itself is exercised by `scripts/container-smoke-test.sh` in CI, and its image is scanned as described in the [Security Scanning Guide](SECURITY_SCANNING.md).

## What Is Not Covered by Automated Tests

- Real reverse proxies. The SWAG, nginx, Traefik, Apache and Caddy configurations are not run against those servers.
- Real external services and clients, such as OPDS reader apps, ntfy, Gotify, Discord, Komga and Kavita. Their tests use fake HTTP handlers.
- HTTPS and TLS configuration. The app does not redirect to HTTPS by itself; that is the reverse proxy's job.
- Penetration testing.

To add a test for a newly found vulnerability, put it in the area above that it belongs to, and fail it against the unfixed code first.
