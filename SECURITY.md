# Security Policy

## Supported Versions

We actively maintain and provide security updates for the latest version of ComicMaintainer.

| Version | Supported          |
| ------- | ------------------ |
| latest  | :white_check_mark: |
| < latest| :x:                |

## Security Scanning

This project implements automated security scanning to identify and address vulnerabilities:

### Automated Scans

1. **Code Security Scanning (Bandit)**
   - Scans Python code for common security issues
   - Runs on every push and pull request
   - Weekly scheduled scans on Monday at 9:00 AM UTC
   - Results available in GitHub Actions artifacts

2. **Dependency Vulnerability Scanning (pip-audit)**
   - Checks Python dependencies for known vulnerabilities
   - Scans against the Python Packaging Advisory Database
   - Runs on every push and pull request
   - Weekly scheduled scans

3. **Docker Image Security Scanning (Trivy)**
   - Scans the Docker image for OS and library vulnerabilities
   - Checks for CRITICAL, HIGH, and MEDIUM severity issues
   - Results uploaded to GitHub Security tab
   - Runs on every push and pull request

### Manual Security Scans

You can run security scans locally before submitting code:

#### Install Security Tools
```bash
pip install bandit pip-audit
```

#### Run Code Security Scan
```bash
# Scan with Bandit
bandit -r src/ -f txt

# Scan with detailed output
bandit -r src/ -f json -o bandit-report.json
```

#### Run Dependency Security Scan
```bash
# Check dependencies for vulnerabilities
pip-audit -r requirements.txt

# Generate detailed report
pip-audit -r requirements.txt --format json --output pip-audit-report.json
```

#### Run Docker Image Scan
```bash
# Build the image
docker build -t comictagger-watcher:scan .

# Scan with Trivy
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image comictagger-watcher:scan
```

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a security issue, please follow these steps:

### Where to Report

Please **DO NOT** report security vulnerabilities through public GitHub issues.

Instead, please report security vulnerabilities by:
1. Opening a private security advisory on GitHub (preferred)
2. Emailing the maintainers directly (see repository contact information)

### What to Include

When reporting a vulnerability, please include:
- Description of the vulnerability
- Steps to reproduce the issue
- Potential impact
- Suggested fix (if available)
- Your contact information for follow-up questions

### Response Timeline

- **Initial Response**: Within 48 hours
- **Status Update**: Within 7 days
- **Fix Timeline**: Varies based on severity
  - Critical: Within 7 days
  - High: Within 14 days
  - Medium: Within 30 days
  - Low: Within 90 days

### Disclosure Policy

- Security issues will be patched privately
- A security advisory will be published after the fix is released
- Credit will be given to the reporter (unless anonymity is requested)
- We follow responsible disclosure practices

## Security Best Practices

When deploying ComicMaintainer:

### Container Security

1. **Use Custom User/Group IDs**
   ```bash
   docker run -d \
     -e PUID=$(id -u) \
     -e PGID=$(id -g) \
     # ... other options
   ```

2. **Mount Volumes with Appropriate Permissions**
   - Ensure watched directories have appropriate access controls
   - Use read-only mounts where possible

3. **Network Security**
   - Expose only necessary ports
   - Use reverse proxy with HTTPS for external access
   - Consider using Docker networks for isolation

4. **Keep Image Updated**
   ```bash
   docker pull iceburn1/comictagger-watcher:latest
   ```

### Application Security

1. **Environment Variables**
   - Never commit sensitive environment variables to source control
   - Use Docker secrets or environment files for configuration

2. **File Permissions**
   - Configure appropriate PUID/PGID for file access
   - Limit write access to only necessary directories

3. **Log Management**
   - Review logs regularly for suspicious activity
   - Configure log rotation to prevent disk space issues
   - Store logs securely with appropriate access controls

4. **Dependencies**
   - Regularly update Python dependencies
   - Review security advisories for ComicTagger and other dependencies

### Known Security Considerations

1. **Binding to All Interfaces (0.0.0.0)**
   - The web interface binds to 0.0.0.0 by default for Docker compatibility
   - This is intentional but should be protected with a reverse proxy for external access
   - Use firewall rules or Docker network configuration to restrict access

2. **Subprocess Calls**
   - The application uses subprocess calls for process management
   - All subprocess calls use hardcoded commands with safe parameters
   - No user input is passed directly to shell commands

3. **SQL Queries**
   - SQLite is used for storing markers and job state
   - Parameterized queries are used to prevent SQL injection
   - Some dynamic SQL construction is used safely for field updates

4. **File System Access**
   - The application needs file system access to process comic files
   - Access is limited to configured directories (WATCHED_DIR, DUPLICATE_DIR)
   - Consider using read-only mounts where processing is not required

## Error Report Redaction

Automated error reporting (`docs/ERROR_REPORTING.md`) is the only feature that can
send data from a self-hosted instance to a third party. It is disabled by default and
transmits nothing until an administrator explicitly enables it.

### Redaction is a hard gate

Every outbound report is redacted **in its entirety** — the whole payload, not only
the log lines — before it is stored, previewed or delivered. The following are
replaced with `[redacted]`:

- Absolute paths and library folder structure
- Comic and series filenames
- Usernames and e-mail addresses
- SMTP credentials
- ComicVine API keys
- JWT signing keys and bearer tokens
- GitHub tokens (`ghp_*`, `github_pat_*` and related prefixes)
- Authelia forwarded-auth headers
- IPv4 and IPv6 addresses

Two things are kept deliberately, because a report is worthless without them: source
filenames in stack frames (public repository files that identify the faulting code)
and the host of an outbound URL (which identifies the external provider that failed).
URL paths and queries are removed, and IP-literal hosts are dropped entirely.

### Fail-closed behaviour

The redactor performs a single left-to-right pass over one ordered pattern so no
character is classified twice and a placeholder cannot be re-parsed. All quantifiers
are bounded and a match timeout is enforced; if the timeout fires, the redactor
returns `[redacted]` for the whole value rather than risking an unredacted leak.

`debug.log` is never attached wholesale — only a bounded window of already-redacted
lines around the error.

### Consent

The default mode transmits nothing: the instance builds a pre-filled issue URL that
the administrator reviews and submits themselves. The exact payload is shown in the UI
before anything is sent or any URL is opened.

Automatic mode is opt-in and requires a credential the operator supplies. Use a
fine-grained token scoped to the single target repository with **Issues: write** and
no other permission. The token is stored in `user-settings.json`, is never returned by
the API, and is never written to the log.

### Untrusted report content

`POST /api/errorreports/client` accepts browser-supplied content, so its payload is
attacker-controlled by any signed-in user. Every field is length-capped at the model
level, redacted like any other report, and never auto-transmitted.

Because a report body is read by an automated agent, only reports originating from the
reporter identity are auto-assigned. User-submitted reports require a maintainer to
apply `ready-for-agent` first, and agent sessions are capped per day.

### Reporting a redaction failure

If a preview or a filed issue contains data that should have been stripped, report it
through the process in [Where to Report](#where-to-report) rather than opening a
public issue.

## Security Features

This project implements several security features:

1. **No Shell=True in Subprocess Calls**
   - All subprocess calls avoid shell=True to prevent command injection

2. **Parameterized Database Queries**
   - SQLite queries use parameterization to prevent SQL injection

3. **Input Validation**
   - File paths are validated before processing
   - File extensions are checked before processing

4. **Least Privilege**
   - Container runs as non-root user (nobody:users by default)
   - Customizable PUID/PGID for proper file permissions

5. **Dependency Management**
   - Minimal dependencies to reduce attack surface
   - Regular dependency updates
   - Automated vulnerability scanning

## Compliance

This project aims to follow security best practices including:

- OWASP Top 10 awareness
- CWE (Common Weakness Enumeration) guidelines
- Docker security best practices
- Python security recommendations (PEP 8, security-focused linting)

## Additional Resources

- [Docker Security Best Practices](https://docs.docker.com/engine/security/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [Python Security](https://python.readthedocs.io/en/stable/library/security_warnings.html)
- [Bandit Documentation](https://bandit.readthedocs.io/)
- [pip-audit Documentation](https://pypi.org/project/pip-audit/)

## Version History

- **2024-10**: Initial security policy and automated scanning implementation
