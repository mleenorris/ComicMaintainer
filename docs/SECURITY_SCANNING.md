# Security Scanning Guide

This document describes the security scanning tools and processes used by ComicMaintainer on the
`dotnet` branch.

## Overview

Security scanning covers three layers:

1. **Static code analysis** - CodeQL, run through GitHub's code scanning default setup
   (see [CODEQL_CONFIGURATION.md](CODEQL_CONFIGURATION.md))
2. **Dependency scanning** - known vulnerabilities in NuGet packages
3. **Container scanning** - OS and library vulnerabilities in the published Docker image

Dependabot (`.github/dependabot.yml`) additionally opens pull requests for NuGet, GitHub Actions
and Docker base image updates.

## Automated scanning workflow

`.github/workflows/security-scan.yml` runs automatically:

**Triggers:**
- Push to `dotnet`
- Pull requests targeting `dotnet`
- Weekly schedule: every Monday at 9:00 AM UTC
- Manual trigger via the GitHub Actions UI

**Jobs:**

1. **dependency-scan** - restores `ComicMaintainer.Core`, `ComicMaintainer.WebApi` and the test
   project (the MAUI app is skipped because it needs the `maui-android` workload) and runs
   `dotnet list package --vulnerable --include-transitive`. The job fails when any project
   reports vulnerable packages.
2. **docker-security-scan** - builds `Dockerfile.dotnet` and scans the resulting image with Trivy
   at `CRITICAL,HIGH,MEDIUM` severity. Results are uploaded to the Security tab as SARIF and
   kept as a human-readable artifact.

**Artifacts:**
- `nuget-vulnerability-report` (text, 30-day retention)
- `trivy-security-report` (table format, 30-day retention)
- Trivy SARIF results in the GitHub Security tab

## Running the scans locally

### NuGet dependencies

```bash
dotnet restore src/ComicMaintainer.Core/ComicMaintainer.Core.csproj
dotnet list src/ComicMaintainer.Core/ComicMaintainer.Core.csproj package --vulnerable --include-transitive

# Also useful: packages that are no longer maintained
dotnet list src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj package --deprecated
```

### Docker image

```bash
docker build -f Dockerfile.dotnet -t comicmaintainer:scan .

docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --severity HIGH,CRITICAL comicmaintainer:scan
```

## Viewing results

**GitHub Actions:** Actions tab → "Security Vulnerability Scanning" → latest run → download
artifacts for the detailed reports.

**GitHub Security tab:** Security → Code scanning alerts → filter by tool (CodeQL or Trivy).

## Policy

- Critical or High severity vulnerabilities in dependencies fail the build and must be fixed
  before merging.
- Medium severity findings are triaged and fixed within the next release cycle.
- Packages that are pinned because a newer version does not build (`SixLabors.ImageSharp`,
  `SharpCompress`) carry a comment explaining why, and matching `ignore` entries in
  `.github/dependabot.yml`. Revisit those pins when upgrading becomes possible.

## Handling a vulnerability

1. Assess severity: Critical/High require immediate action, Medium within the release cycle.
2. Update the package version in the relevant `.csproj`, or the base image in `Dockerfile.dotnet`.
3. Build and run the tests: `dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj`.
4. Note the change in `CHANGELOG.md`.

### Reporting new vulnerabilities

If you discover a security vulnerability:
1. Do **not** open a public issue.
2. Follow the process in [SECURITY.md](../SECURITY.md).

## Additional resources

- [Trivy documentation](https://aquasecurity.github.io/trivy/)
- [CodeQL documentation](https://codeql.github.com/docs/)
- [`dotnet list package` documentation](https://learn.microsoft.com/dotnet/core/tools/dotnet-list-package)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
