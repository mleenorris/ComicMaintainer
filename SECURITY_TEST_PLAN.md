# Security Test Plan - ComicMaintainer

## Overview

This document defines the comprehensive security testing strategy for ComicMaintainer. All tests listed here should be implemented and maintained as part of the continuous integration process.

**Last Updated:** November 6, 2025  
**Status:** Initial Test Plan

---

## Test Categories

### 1. Authentication Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/AuthenticationSecurityTests.cs`

#### Test Cases

1. **Test_Login_WithSqlInjection_ShouldBeRejected**
   - Input: `' OR '1'='1` in username
   - Expected: Authentication fails, no SQL execution
   - Priority: Critical

2. **Test_Login_WithXssAttempt_ShouldBeSanitized**
   - Input: `<script>alert('xss')</script>` in username
   - Expected: Input sanitized, authentication fails safely
   - Priority: High

3. **Test_Login_RateLimiting_ShouldBlockExcessiveAttempts**
   - Input: 10 failed login attempts from same IP
   - Expected: Rate limit triggered, 429 Too Many Requests
   - Priority: High

4. **Test_Login_WithWeakPassword_ShouldBeRejected**
   - Input: Passwords less than 8 characters
   - Expected: Registration/password change fails
   - Priority: Medium

5. **Test_JwtToken_Tampering_ShouldBeDetected**
   - Input: Modified JWT token signature
   - Expected: 401 Unauthorized
   - Priority: Critical

6. **Test_JwtToken_Expired_ShouldBeRejected**
   - Input: Expired JWT token
   - Expected: 401 Unauthorized
   - Priority: Critical

7. **Test_JwtToken_MissingSignature_ShouldBeRejected**
   - Input: JWT token without signature
   - Expected: 401 Unauthorized
   - Priority: Critical

8. **Test_AccountEnumeration_ShouldNotRevealUserExistence**
   - Input: Valid vs invalid usernames
   - Expected: Same error message for both
   - Priority: Medium

9. **Test_PasswordReset_TokenValidation_ShouldEnforceExpiration**
   - Input: Expired password reset token
   - Expected: Token rejected
   - Priority: Medium

10. **Test_ConcurrentLogin_ShouldBeHandledSafely**
    - Input: Multiple simultaneous login attempts
    - Expected: No race conditions or data corruption
    - Priority: Medium

---

### 2. Authorization Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/AuthorizationSecurityTests.cs`

#### Test Cases

1. **Test_UnauthorizedAccess_ToAdminEndpoint_ShouldBeDenied**
   - Setup: User with "User" role
   - Action: Access admin-only endpoint
   - Expected: 403 Forbidden
   - Priority: Critical

2. **Test_HorizontalPrivilegeEscalation_ShouldBePrevented**
   - Setup: User A tries to access User B's data
   - Expected: 403 Forbidden or 404 Not Found
   - Priority: Critical

3. **Test_VerticalPrivilegeEscalation_ShouldBePrevented**
   - Setup: User tries to perform admin action
   - Expected: 403 Forbidden
   - Priority: Critical

4. **Test_MissingAuthorizationHeader_ShouldBeRejected**
   - Action: Call protected endpoint without token
   - Expected: 401 Unauthorized
   - Priority: High

5. **Test_RoleBasedAccess_AdminRole_CanAccessAllEndpoints**
   - Setup: User with Admin role
   - Expected: All endpoints accessible
   - Priority: High

6. **Test_RoleBasedAccess_ReadOnlyRole_CannotModify**
   - Setup: User with ReadOnly role
   - Action: Attempt POST/PUT/DELETE
   - Expected: 403 Forbidden
   - Priority: High

7. **Test_ApiKeyAuthorization_ValidKey_ShouldSucceed**
   - Setup: Valid API key
   - Expected: Access granted
   - Priority: Medium

8. **Test_ApiKeyAuthorization_InvalidKey_ShouldBeDenied**
   - Setup: Invalid API key
   - Expected: 401 Unauthorized
   - Priority: Medium

---

### 3. Path Traversal Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/PathTraversalSecurityTests.cs`

#### Test Cases

1. **Test_PathTraversal_DoubleDot_ShouldBeBlocked**
   - Input: `../../etc/passwd`
   - Expected: 400 Bad Request
   - Priority: Critical

2. **Test_PathTraversal_EncodedDoubleDot_ShouldBeBlocked**
   - Input: `..%2F..%2Fetc%2Fpasswd`
   - Expected: 400 Bad Request
   - Priority: Critical

3. **Test_PathTraversal_AbsolutePathOutsideAllowed_ShouldBeBlocked**
   - Input: `/etc/passwd`
   - Expected: 400 Bad Request
   - Priority: Critical

4. **Test_ValidPath_InWatchedDirectory_ShouldBeAllowed**
   - Input: `/watched_dir/comics/test.cbz`
   - Expected: 200 OK (if file exists)
   - Priority: High

5. **Test_ValidPath_InDuplicateDirectory_ShouldBeAllowed**
   - Input: `/duplicates/comics/test.cbz`
   - Expected: 200 OK (if file exists)
   - Priority: High

6. **Test_ValidPath_InConfigDirectory_ShouldBeAllowed**
   - Input: `/Config/settings.json`
   - Expected: 200 OK (if file exists)
   - Priority: High

7. **Test_PathTraversal_SymbolicLinks_ShouldBeValidated**
   - Input: Path with symbolic link outside allowed directories
   - Expected: 400 Bad Request
   - Priority: Medium

8. **Test_PathTraversal_WindowsStyle_ShouldBeBlocked**
   - Input: `..\\..\\windows\\system32`
   - Expected: 400 Bad Request
   - Priority: Medium

9. **Test_PathTraversal_NullByte_ShouldBeBlocked**
   - Input: `test.cbz%00.txt`
   - Expected: 400 Bad Request
   - Priority: Medium

10. **Test_PathValidation_LogsSanitizedPaths**
    - Input: Path with sensitive information
    - Expected: Only filename in logs, not full path
    - Priority: Low

---

### 4. CORS Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/CorsSecurityTests.cs`

#### Test Cases

1. **Test_Cors_AllowedOrigin_ShouldSucceed**
   - Setup: Request from allowed origin
   - Expected: CORS headers present, request succeeds
   - Priority: Critical

2. **Test_Cors_DisallowedOrigin_ShouldBeBlocked**
   - Setup: Request from non-allowed origin
   - Expected: CORS headers absent or reject
   - Priority: Critical

3. **Test_Cors_PreflightRequest_FromAllowedOrigin_ShouldSucceed**
   - Setup: OPTIONS request from allowed origin
   - Expected: 200 OK with CORS headers
   - Priority: High

4. **Test_Cors_PreflightRequest_FromDisallowedOrigin_ShouldBeBlocked**
   - Setup: OPTIONS request from disallowed origin
   - Expected: No CORS headers or rejection
   - Priority: High

5. **Test_Cors_WithCredentials_RequiresSpecificOrigin**
   - Setup: Request with credentials from wildcard origin
   - Expected: Should fail (can't use * with credentials)
   - Priority: High

6. **Test_Cors_AllowedMethods_OnlySpecified_ShouldBeAllowed**
   - Setup: Request with allowed method
   - Expected: Request succeeds
   - Priority: Medium

7. **Test_Cors_DisallowedMethods_ShouldBeBlocked**
   - Setup: Request with method not in allowed list
   - Expected: CORS error
   - Priority: Medium

8. **Test_Cors_CustomHeaders_ShouldBeValidated**
   - Setup: Request with custom headers
   - Expected: Only allowed headers accepted
   - Priority: Medium

---

### 5. Input Validation Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/InputValidationSecurityTests.cs`

#### Test Cases

1. **Test_SqlInjection_InFilePath_ShouldBeBlocked**
   - Input: `'; DROP TABLE Users; --`
   - Expected: Safely handled, no SQL execution
   - Priority: Critical

2. **Test_XssInjection_InFilename_ShouldBeSanitized**
   - Input: `<script>alert('xss')</script>.cbz`
   - Expected: Sanitized before storage/display
   - Priority: High

3. **Test_CommandInjection_InParameters_ShouldBeBlocked**
   - Input: `; rm -rf /`
   - Expected: Not executed as shell command
   - Priority: Critical

4. **Test_XmlBomb_ShouldBeRejected**
   - Input: Malicious XML with billion laughs attack
   - Expected: Parser should reject
   - Priority: High

5. **Test_JsonDeserializationBomb_ShouldBeRejected**
   - Input: Deeply nested JSON (10000+ levels)
   - Expected: Rejection or safe handling
   - Priority: High

6. **Test_LargeFileUpload_ShouldBeRejected**
   - Input: File larger than max size limit
   - Expected: 413 Payload Too Large
   - Priority: Medium

7. **Test_InvalidFileExtension_ShouldBeRejected**
   - Input: .exe file disguised as .cbz
   - Expected: File type validation fails
   - Priority: Medium

8. **Test_IntegerOverflow_InPagination_ShouldBeHandled**
   - Input: page=2147483647
   - Expected: Safe handling, no overflow
   - Priority: Medium

9. **Test_NegativeNumbers_InPageSize_ShouldBeRejected**
   - Input: pageSize=-1
   - Expected: Validation error
   - Priority: Low

10. **Test_SpecialCharacters_InSearchQuery_ShouldBeSanitized**
    - Input: Search with regex special chars
    - Expected: Escaped or sanitized
    - Priority: Low

---

### 6. Error Handling Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/ErrorHandlingSecurityTests.cs`

#### Test Cases

1. **Test_DatabaseError_ShouldNotExposeDetails**
   - Setup: Trigger database error
   - Expected: Generic error message, no DB details
   - Priority: Critical

2. **Test_ExceptionStackTrace_ShouldNotBeExposed**
   - Setup: Trigger exception
   - Expected: No stack trace in API response
   - Priority: Critical

3. **Test_FileSystemError_ShouldNotExposePaths**
   - Setup: Trigger file not found error
   - Expected: Generic message, no system paths
   - Priority: High

4. **Test_ValidationError_ShouldProvideUserFriendlyMessage**
   - Setup: Submit invalid data
   - Expected: Clear validation message, no technical details
   - Priority: Medium

5. **Test_AuthenticationError_ShouldNotIndicateUserExistence**
   - Setup: Login with non-existent user vs wrong password
   - Expected: Same error message for both
   - Priority: Medium

6. **Test_InternalServerError_ShouldLogDetails**
   - Setup: Trigger 500 error
   - Expected: Full details in server logs, generic message to user
   - Priority: Medium

7. **Test_404Error_ShouldNotRevealDirectoryStructure**
   - Setup: Request non-existent endpoint
   - Expected: Generic 404, no path information
   - Priority: Low

---

### 7. Security Headers Tests

**File:** `tests/ComicMaintainer.Tests/Security/SecurityHeadersTests.cs`

#### Test Cases

1. **Test_XContentTypeOptions_ShouldBeNoSniff**
   - Expected: `X-Content-Type-Options: nosniff`
   - Priority: High

2. **Test_XFrameOptions_ShouldBeDeny**
   - Expected: `X-Frame-Options: DENY` or `SAMEORIGIN`
   - Priority: High

3. **Test_XssProtection_ShouldBeEnabled**
   - Expected: `X-XSS-Protection: 1; mode=block`
   - Priority: Medium

4. **Test_ReferrerPolicy_ShouldBeStrict**
   - Expected: `Referrer-Policy: strict-origin-when-cross-origin`
   - Priority: Medium

5. **Test_PermissionsPolicy_ShouldRestrictDangerousFeatures**
   - Expected: Geolocation, microphone, camera disabled
   - Priority: Medium

6. **Test_Hsts_WhenHttps_ShouldBePresent**
   - Setup: HTTPS request or X-Forwarded-Proto: https
   - Expected: `Strict-Transport-Security` header
   - Priority: High

7. **Test_Hsts_WhenHttp_ShouldNotBePresent**
   - Setup: HTTP request
   - Expected: No HSTS header
   - Priority: Medium

8. **Test_ContentSecurityPolicy_WhenHttps_ShouldUpgradeInsecure**
   - Setup: HTTPS request
   - Expected: `Content-Security-Policy: upgrade-insecure-requests`
   - Priority: Medium

9. **Test_CacheControl_ForSensitiveEndpoints_ShouldPreventCaching**
   - Setup: Request to /api/auth/*
   - Expected: `Cache-Control: no-store, no-cache`
   - Priority: Low

---

### 8. Rate Limiting Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/RateLimitingSecurityTests.cs`

#### Test Cases

1. **Test_LoginRateLimit_ExceedingLimit_ShouldReturn429**
   - Setup: 6 login attempts in 15 minutes
   - Expected: 429 Too Many Requests on 6th attempt
   - Priority: Critical

2. **Test_LoginRateLimit_AfterTimeout_ShouldReset**
   - Setup: Wait for rate limit window to expire
   - Expected: New requests allowed
   - Priority: High

3. **Test_RegisterRateLimit_ExceedingLimit_ShouldReturn429**
   - Setup: 4 registrations in 1 hour
   - Expected: 429 on 4th attempt
   - Priority: High

4. **Test_RateLimit_PerIpAddress_ShouldBeSeparate**
   - Setup: Requests from different IPs
   - Expected: Each IP has separate limit
   - Priority: High

5. **Test_RateLimit_RetryAfter_Header_ShouldBePresent**
   - Setup: Trigger rate limit
   - Expected: `Retry-After` header with seconds
   - Priority: Medium

6. **Test_RateLimit_AuthenticatedVsAnonymous_MayHaveDifferentLimits**
   - Setup: Compare limits for auth vs unauth users
   - Expected: Potentially higher limits for authenticated
   - Priority: Low

---

### 9. HTTPS & TLS Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/HttpsSecurityTests.cs`

#### Test Cases

1. **Test_HttpsRedirection_InProduction_ShouldRedirect**
   - Setup: HTTP request in production mode
   - Expected: 301/302 redirect to HTTPS
   - Priority: High

2. **Test_HttpsRedirection_InDevelopment_ShouldNotRedirect**
   - Setup: HTTP request in development mode
   - Expected: No redirect
   - Priority: Medium

3. **Test_Hsts_MaxAge_ShouldBeOneYear**
   - Expected: max-age=31536000
   - Priority: Medium

4. **Test_Hsts_IncludeSubDomains_ShouldBePresent**
   - Expected: includeSubDomains directive
   - Priority: Low

5. **Test_TlsVersion_ShouldBeMinimum12**
   - Setup: Attempt connection with TLS 1.0/1.1
   - Expected: Connection refused
   - Priority: High (if HTTPS implemented)

6. **Test_CipherSuites_ShouldBeSecure**
   - Setup: Check allowed cipher suites
   - Expected: Only strong ciphers allowed
   - Priority: Medium (if HTTPS implemented)

---

### 10. Session Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/SessionSecurityTests.cs`

#### Test Cases

1. **Test_JwtExpiration_AfterTimeout_ShouldInvalidate**
   - Setup: Wait for token expiration
   - Expected: 401 Unauthorized
   - Priority: High

2. **Test_JwtRevocation_AfterLogout_ShouldInvalidate**
   - Setup: Logout then use same token
   - Expected: 401 Unauthorized (if token blacklist implemented)
   - Priority: Medium

3. **Test_SessionFixation_ShouldRegenerateToken**
   - Setup: Login with pre-existing session
   - Expected: New token issued
   - Priority: Medium

4. **Test_ConcurrentSessions_ShouldBeAllowedOrBlocked**
   - Setup: Login from multiple devices
   - Expected: Based on policy (allow or block)
   - Priority: Low

---

### 11. API Security Tests

**File:** `tests/ComicMaintainer.Tests/Security/ApiSecurityTests.cs`

#### Test Cases

1. **Test_ApiVersioning_OldVersion_ShouldBeHandled**
   - Setup: Request with old API version
   - Expected: Graceful handling or rejection
   - Priority: Low

2. **Test_ContentType_Validation_ShouldEnforce**
   - Setup: Send JSON with text/plain Content-Type
   - Expected: 415 Unsupported Media Type
   - Priority: Medium

3. **Test_HttpMethods_NotAllowed_ShouldReturn405**
   - Setup: Use unimplemented HTTP method
   - Expected: 405 Method Not Allowed
   - Priority: Medium

4. **Test_ApiDocumentation_ShouldNotExposeSecrets**
   - Setup: Check Swagger/OpenAPI docs
   - Expected: No secrets, credentials, or sensitive info
   - Priority: Low

---

### 12. Docker Security Tests

**File:** `tests/security/docker_security_tests.sh`

#### Test Cases

1. **Test_Container_RunsAsNonRoot**
   ```bash
   docker inspect <container> | jq '.[0].Config.User'
   # Expected: Not "0" or "root"
   ```
   Priority: Critical

2. **Test_Container_NoPrivilegedMode**
   ```bash
   docker inspect <container> | jq '.[0].HostConfig.Privileged'
   # Expected: false
   ```
   Priority: Critical

3. **Test_Container_LimitedCapabilities**
   ```bash
   docker inspect <container> | jq '.[0].HostConfig.CapAdd'
   # Expected: Minimal capabilities
   ```
   Priority: High

4. **Test_Image_NoSecretsInLayers**
   ```bash
   docker history <image> | grep -i "secret\|password\|key"
   # Expected: No matches
   ```
   Priority: Critical

5. **Test_Image_VulnerabilityScan_Passes**
   ```bash
   trivy image <image> --severity CRITICAL,HIGH
   # Expected: No CRITICAL or HIGH vulnerabilities
   ```
   Priority: High

6. **Test_FilePermissions_Restrictive**
   ```bash
   docker exec <container> ls -la /Config
   # Expected: Proper ownership (PUID:PGID)
   ```
   Priority: Medium

7. **Test_EnvironmentVariables_NoHardcodedSecrets**
   ```bash
   docker inspect <container> | jq '.[0].Config.Env'
   # Expected: No actual secret values
   ```
   Priority: Critical

---

## Test Execution Strategy

### Continuous Integration

1. **On Every Commit**
   - Unit tests for security functions
   - Basic authentication tests
   - Input validation tests

2. **On Pull Request**
   - Full security test suite
   - Integration tests
   - Security header tests

3. **Weekly Scheduled**
   - Dependency vulnerability scan
   - Docker image security scan
   - Static code analysis (CodeQL)

4. **Before Release**
   - Complete security test suite
   - Manual penetration testing
   - Third-party security audit (for major releases)

### Test Environment

- **Unit Tests**: In-memory database, mocked dependencies
- **Integration Tests**: Test database, isolated environment
- **Security Tests**: Dedicated test environment mimicking production

---

## Test Coverage Goals

| Category | Current Coverage | Target Coverage |
|----------|------------------|-----------------|
| Authentication | 60% | 95% |
| Authorization | 40% | 95% |
| Path Validation | 0% | 100% |
| CORS | 0% | 100% |
| Input Validation | 30% | 90% |
| Error Handling | 0% | 90% |
| Security Headers | 0% | 100% |
| Rate Limiting | 0% | 100% |
| Overall | 25% | 90% |

---

## Success Criteria

A security test passes if:
1. ✅ Expected behavior is demonstrated
2. ✅ No security exceptions are thrown unexpectedly
3. ✅ Logs contain appropriate security events
4. ✅ No sensitive information is leaked
5. ✅ Response time is within acceptable limits

A security test fails if:
1. ❌ Unauthorized access is granted
2. ❌ Sensitive information is exposed
3. ❌ Malicious input is not blocked
4. ❌ Security headers are missing
5. ❌ Rate limits are not enforced

---

## Test Maintenance

### Regular Review
- Review test suite quarterly
- Update tests when new features are added
- Remove obsolete tests
- Add tests for newly discovered vulnerabilities

### Test Quality
- Keep tests independent and isolated
- Use descriptive test names
- Document complex security scenarios
- Maintain test data securely

### Reporting
- Weekly test execution reports
- Security test coverage dashboard
- Failed test root cause analysis
- Security metrics trending

---

## Implementation Priority

### Phase 1 (Immediate - Week 1)
- [ ] Path traversal tests
- [ ] CORS configuration tests
- [ ] Basic input validation tests
- [ ] Authentication security tests

### Phase 2 (High Priority - Weeks 2-3)
- [ ] Authorization tests
- [ ] Error handling tests
- [ ] Security headers tests
- [ ] Docker security tests

### Phase 3 (Medium Priority - Weeks 4-6)
- [ ] Rate limiting tests (after implementation)
- [ ] Advanced input validation
- [ ] Session security tests
- [ ] API security tests

### Phase 4 (Ongoing)
- [ ] Continuous improvement
- [ ] New vulnerability coverage
- [ ] Penetration testing
- [ ] Security audit findings

---

## Resources

### Tools Required
- xUnit - Test framework
- Moq - Mocking library
- FluentAssertions - Assertion library
- WebApplicationFactory - Integration testing
- Trivy - Container scanning
- OWASP ZAP - Penetration testing

### Documentation
- OWASP Testing Guide
- ASP.NET Core Security Documentation
- CWE/SANS Top 25
- NIST Cybersecurity Framework

---

## Version History

- **v1.0** (2025-11-06): Initial security test plan created
  - Defined 12 test categories
  - Listed 90+ test cases
  - Established coverage goals
  - Set implementation priorities

---

## Next Steps

1. Create test files for each category
2. Implement Phase 1 tests
3. Configure CI/CD to run security tests
4. Set up security test reporting
5. Schedule first security audit

**Test Plan Owner:** Security Team  
**Review Schedule:** Quarterly  
**Next Review:** February 2026
