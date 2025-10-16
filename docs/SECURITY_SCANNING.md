# Security Scanning Guide

This document provides detailed information about the security scanning tools and processes implemented in ComicMaintainer.

## Overview

ComicMaintainer implements a comprehensive security scanning strategy using multiple tools to identify vulnerabilities at different levels:

1. **Static Code Analysis** - Identifies security issues in source code
2. **Dependency Scanning** - Checks for known vulnerabilities in Python packages
3. **Container Scanning** - Scans Docker images for OS and library vulnerabilities

## Security Tools

### Bandit - Python Code Security Scanner

**What it does:** Scans Python source code for common security issues using Abstract Syntax Tree (AST) analysis.

**Configuration:** `.bandit` file in repository root

**Common issues detected:**
- SQL injection vulnerabilities
- Hard-coded passwords or secrets
- Use of insecure functions
- Unsafe deserialization
- Shell injection risks
- Weak cryptography

**Running locally:**
```bash
# Basic scan
bandit -r src/

# With configuration file
bandit -r src/ --ini .bandit

# Generate JSON report
bandit -r src/ --ini .bandit -f json -o bandit-report.json
```

**Current Status:** 
- Low severity issues: 4 (try/except pass, subprocess import warnings)
- Medium severity issues: 2 (safe SQL query construction patterns)
- High severity issues: 0
- All issues reviewed and deemed acceptable for the use case

### pip-audit - Dependency Vulnerability Scanner

**What it does:** Checks installed Python packages against the Python Packaging Advisory Database for known vulnerabilities.

**Running locally:**
```bash
# Scan requirements.txt
pip-audit -r requirements.txt

# Generate detailed report
pip-audit -r requirements.txt --format json --output report.json

# Fix vulnerabilities automatically (when possible)
pip-audit -r requirements.txt --fix
```

**Current Status:** ✅ No known vulnerabilities found in dependencies
- watchdog: 6.0.0 - No vulnerabilities
- flask: 3.1.2 - No vulnerabilities  
- gunicorn: 23.0.0 - No vulnerabilities

### Trivy - Docker Image Scanner

**What it does:** Scans Docker images for:
- OS package vulnerabilities
- Application dependencies
- Misconfigurations
- Secrets in images

**Running locally:**
```bash
# Build image
docker build -t comictagger-watcher:scan .

# Scan with Trivy
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image comictagger-watcher:scan

# Scan for HIGH and CRITICAL only
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --severity HIGH,CRITICAL comictagger-watcher:scan

# Generate SARIF report for GitHub
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --format sarif --output trivy-results.sarif comictagger-watcher:scan
```

## Automated Scanning Workflow

### GitHub Actions Integration

The security scanning workflow (`.github/workflows/security-scan.yml`) runs automatically:

**Triggers:**
- Push to `master` or `main` branch
- Pull requests to `master` or `main` branch
- Weekly schedule: Every Monday at 9:00 AM UTC
- Manual trigger via GitHub Actions UI

**Jobs:**

1. **security-scan** - Python code and dependency scanning
   - Runs Bandit on source code
   - Runs pip-audit on dependencies
   - Generates reports and uploads artifacts
   - Fails build on critical vulnerabilities

2. **docker-security-scan** - Container image scanning
   - Builds Docker image
   - Scans with Trivy
   - Uploads results to GitHub Security tab (SARIF format)
   - Generates human-readable reports

**Artifacts:**
- `bandit-security-report` (JSON and TXT formats, 30-day retention)
- `pip-audit-report` (JSON and Markdown formats, 30-day retention)
- `security-summary` (Combined summary, 90-day retention)
- `trivy-security-report` (Table format, 30-day retention)
- SARIF results uploaded to GitHub Security tab

### Viewing Results

**GitHub Actions:**
1. Go to the repository's Actions tab
2. Select "Security Vulnerability Scanning" workflow
3. View the latest run
4. Download artifacts for detailed reports

**GitHub Security Tab:**
1. Go to repository's Security tab
2. Select "Code scanning alerts"
3. View Trivy findings uploaded via SARIF

## Security Thresholds

### Current Policy

The build will fail if:
- Critical or High severity vulnerabilities are found in dependencies
- The workflow can be configured to fail on Medium severity as well

The build will NOT fail on:
- Low severity Bandit warnings (reviewed and accepted)
- Medium severity warnings that are false positives (documented)

### Exceptions

Some warnings are intentionally skipped in `.bandit`:

- **B104** (hardcoded_bind_all_interfaces): Flask app binds to 0.0.0.0 for Docker - this is intentional
- **B603** (subprocess_without_shell_equals_true): Subprocess calls use hardcoded commands
- **B607** (start_process_with_partial_path): System commands from PATH are acceptable

## Best Practices

### Before Committing

1. Run security scans locally:
```bash
pip install -r requirements-dev.txt
bandit -r src/ --ini .bandit
pip-audit -r requirements.txt
```

2. Review and address any HIGH or CRITICAL issues

3. Document accepted risks in commit messages

### During Code Review

1. Review security scan results in PR checks
2. Check for new vulnerabilities introduced
3. Verify exceptions are documented
4. Ensure dependencies are up to date

### Regular Maintenance

1. Review weekly scan results
2. Update dependencies regularly
3. Monitor security advisories for:
   - Python ecosystem
   - ComicTagger dependencies
   - Base Docker image (python:3.11-slim)
4. Keep security tools updated

## Handling Vulnerabilities

### When a Vulnerability is Found

1. **Assess Severity:**
   - Critical/High: Immediate action required
   - Medium: Fix within 2 weeks
   - Low: Fix in next release cycle

2. **Check for Patches:**
   ```bash
   pip-audit -r requirements.txt --fix
   ```

3. **Test the Fix:**
   - Run full test suite (when available)
   - Build and test Docker image
   - Verify functionality not broken

4. **Document:**
   - Update SECURITY.md if needed
   - Note in changelog
   - Update dependencies in requirements.txt

### Reporting New Vulnerabilities

If you discover a security vulnerability:
1. DO NOT open a public issue
2. Follow the process in SECURITY.md
3. Report via GitHub Security Advisory or email

## Security Scanning Metrics

Current scan results (as of last update):

**Code Security (Bandit):**
- Total lines scanned: 3,370
- High severity: 0
- Medium severity: 2 (reviewed, acceptable)
- Low severity: 4 (reviewed, acceptable)

**Dependency Security (pip-audit):**
- Dependencies scanned: 10
- Known vulnerabilities: 0
- All dependencies up to date

**Container Security (Trivy):**
- Scans on every build
- Results in GitHub Security tab

## Additional Resources

- [Bandit Documentation](https://bandit.readthedocs.io/)
- [pip-audit Documentation](https://pypi.org/project/pip-audit/)
- [Trivy Documentation](https://aquasecurity.github.io/trivy/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [Python Security](https://python.readthedocs.io/en/stable/library/security_warnings.html)
- [Docker Security](https://docs.docker.com/engine/security/)

## Continuous Improvement

Security scanning is an ongoing process. We continuously:
- Update scanning tools
- Review and triage findings
- Improve detection rules
- Document security practices
- Educate contributors

For questions or concerns about security scanning, please open a discussion or contact the maintainers.
