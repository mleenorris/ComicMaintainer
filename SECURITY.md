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

1. **Network Binding Configuration**
   - The web interface binds to 0.0.0.0 by default for Docker compatibility (all network interfaces)
   - For improved security when using a reverse proxy on the same host, set `BIND_ADDRESS=127.0.0.1` to restrict access to localhost only
   - Example with localhost binding:
     ```bash
     docker run -d \
       -e BIND_ADDRESS=127.0.0.1 \
       -e WATCHED_DIR=/watched_dir \
       # ... other options
     ```
   - When binding to 127.0.0.1, ensure your reverse proxy is running on the same host and configured to forward requests
   - Use firewall rules or Docker network configuration for additional access control

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
