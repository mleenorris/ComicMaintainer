# Security Scanning Guide

This guide explains the security scans that run on ComicMaintainer, where their results appear and how to run them yourself. [SECURITY.md](../SECURITY.md) has the summary and the policy for reporting a vulnerability.

## What Is Scanned

| What | Tool | Workflow | When |
|---|---|---|---|
| C# and JavaScript source, and the workflows themselves | CodeQL, `security-and-quality` queries | `codeql-analysis.yml` | Every pull request and push to `dotnet`, and weekly |
| NuGet packages the server ships, direct and transitive | `dotnet list package --vulnerable --include-transitive` | `security-scan.yml` | Every pull request and push to `dotnet`, and weekly |
| The Docker image: base-image packages and the app's libraries | Trivy | `integration-tests-e2e.yml` | Every pull request and push to `dotnet`, on the image just built and smoke tested |
| The Docker image, rebuilt without cache, and the published `iceburn1/comicmaintainer:dotnet` | Trivy | `security-scan.yml` | Weekly |
| NuGet packages, GitHub Actions and the Docker base image | Dependabot | `dependabot.yml` | Weekly; opens pull requests |

There are no JavaScript production dependencies: `package.json` holds only development tools, so `npm audit` has nothing to report for the shipped app.

## What Blocks a Change

| Check | Fails on |
|---|---|
| NuGet listing | A **high** or **critical** advisory in a package the server ships, or a project that could not be checked. Low and moderate advisories are listed in the job summary without failing |
| Trivy, on the container test | A **critical** or **high** vulnerability that has a fixed version. A vulnerability with no fix does not block |
| Trivy, weekly | The same rule, for the rebuilt image and the published image |

A release (the version tag, the Docker image and the Windows and Android downloads) is only published after the tests and the container scan of its commit have passed.

CodeQL findings appear as code scanning alerts and do not fail the build by themselves.

## Where to See the Results

- **Code scanning alerts** in the repository's Security tab hold the CodeQL findings and the Trivy image scan, including medium-severity vulnerabilities.
- **The job summary** of the `Security Vulnerability Scanning` workflow lists every vulnerable NuGet package. The JSON reports are attached to the run as the `vulnerable-nuget-packages` artifact.

## Running the Scans Locally

```bash
# Vulnerable NuGet packages, for each project the server ships
dotnet list src/ComicMaintainer.Core/ComicMaintainer.Core.csproj package --vulnerable --include-transitive
dotnet list src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj package --vulnerable --include-transitive

# Build the image and scan it with Trivy
docker build -f Dockerfile.dotnet -t comicmaintainer:scan .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --scanners vuln --severity CRITICAL,HIGH --ignore-unfixed comicmaintainer:scan
```

To apply the same pass-or-fail rule as CI to the NuGet listing, write JSON reports and pass them to the script CI uses:

```bash
dotnet list src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj package \
  --vulnerable --include-transitive --format json > vulnerable.json
node scripts/check-vulnerable-packages.js vulnerable.json
```

## Handling a Finding

1. **Assess it.** Does the vulnerable code path run in this app? A critical or high finding that blocks a pull request has to be fixed or the image updated before the change can merge.
2. **Update.** For a NuGet package, raise the version in the project file. Dependabot usually proposes this. For the base image, the fix usually arrives by rebuilding on an updated `mcr.microsoft.com/dotnet/aspnet` image.
3. **Test.** Run the test suite, then build and smoke test the image with `scripts/container-smoke-test.sh`.
4. **Note it** in the changelog if users need to act.

To report a vulnerability you have found in ComicMaintainer, follow [SECURITY.md](../SECURITY.md). Do not open a public issue.

## Additional Resources

- [CodeQL documentation](https://codeql.github.com/docs/)
- [Trivy documentation](https://trivy.dev/)
- [Dependabot documentation](https://docs.github.com/code-security/dependabot)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
