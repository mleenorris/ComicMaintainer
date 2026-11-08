# Security Policy

## Supported Versions

Security fixes go into the newest release only. Releases are cut automatically from the `dotnet`
branch, so updating to the newest release is how a fix is installed.

| Version | Supported          |
| ------- | ------------------ |
| Newest 3.x release (Docker tag `iceburn1/comicmaintainer:dotnet`) | :white_check_mark: |
| Older releases | :x: |
| The Python version (`iceburn1/comictagger-watcher`, `master` branch) | :x: |

## Reporting a Vulnerability

Please **do not** report security vulnerabilities through public GitHub issues.

Report them privately instead, by opening a
[private security advisory](https://github.com/mleenorris/ComicMaintainer/security/advisories/new)
on GitHub. Please include:

- A description of the vulnerability and its impact
- Steps to reproduce it, and the version (shown in the web interface, or `GET /api/version`)
- A suggested fix, if you have one

### Response Timeline

- **Initial response**: within 48 hours
- **Status update**: within 7 days
- **Fix timeline**, depending on severity:
  - Critical: within 7 days
  - High: within 14 days
  - Medium: within 30 days
  - Low: within 90 days

### Disclosure Policy

- Security issues are fixed privately, and an advisory is published once the fix is released
- Reporters are credited, unless they ask not to be

## Automated Security Scanning

Every scan below runs in GitHub Actions on the `dotnet` branch. A release (the version tag, the
Docker image and the Windows and Android downloads) is only published after the tests and the
container scan of its commit have passed.

| What | Tool | When | Fails on |
| ---- | ---- | ---- | -------- |
| C# and JavaScript source, and the workflows themselves | [CodeQL](https://codeql.github.com/) (`security-and-quality` queries), `codeql-analysis.yml` | Every pull request and push to `dotnet`, and weekly | Findings appear as code scanning alerts |
| NuGet packages the server ships, direct and transitive | `dotnet list package --vulnerable --include-transitive`, `security-scan.yml` | Every pull request and push to `dotnet`, and weekly | A **high** or **critical** advisory |
| The Docker image: Debian packages of the base image and the app's libraries | [Trivy](https://trivy.dev/), in the container test (`integration-tests-e2e.yml`) | Every pull request and push to `dotnet`, on the image that was just built and smoke tested | A **critical** or **high** vulnerability that has a fixed version |
| The Docker image, rebuilt without cache, and the published `iceburn1/comicmaintainer:dotnet` | Trivy, `security-scan.yml` | Weekly | A fixable **critical** or **high** vulnerability |
| NuGet packages, GitHub Actions and the Docker base image | [Dependabot](https://docs.github.com/code-security/dependabot) version and security updates | Weekly | Opens pull requests |

Image scan results, including medium-severity ones, are uploaded to the repository's code scanning
alerts. Low and moderate NuGet advisories are listed in the job summary without failing the check.

### Running the scans locally

```bash
# Vulnerable NuGet packages
dotnet list src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj package --vulnerable --include-transitive

# Build the image and scan it with Trivy
docker build -f Dockerfile.dotnet -t comicmaintainer:scan .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
  aquasec/trivy image --scanners vuln --severity CRITICAL,HIGH comicmaintainer:scan
```

## Deploying Securely

### Container

1. **Run as your own user.** The container starts as root only to create the app user from `PUID`
   and `PGID` and to give it `/Config`; the app itself runs as that user. The application files
   in `/app` stay owned by root, so the app cannot modify itself.
   ```bash
   docker run -d -e PUID=$(id -u) -e PGID=$(id -g) ... iceburn1/comicmaintainer:dotnet
   ```
2. **Library permissions stay yours.** The entrypoint does not change the owner or mode of the
   comic library or the duplicates folder; it only checks that `PUID` can write to them and logs a
   warning if it cannot. Set `FIX_LIBRARY_PERMISSIONS=true` once to give them to `PUID:PGID` with
   owner and group read/write access, then remove it.
3. **Keep the image updated**: `docker pull iceburn1/comicmaintainer:dotnet`, or pin a version
   tag and update it deliberately. The database is backed up automatically before an update
   changes its schema.
4. **Expose only what is needed.** Put the web interface behind a reverse proxy with HTTPS for
   access from outside your network, and set `TRUSTED_PROXIES` to that proxy.

### Secrets

- Set a strong `JWT_SECRET` (at least 32 characters), or let the app generate and keep one in the
  config folder.
- Pass secrets as files rather than variables where you can: `JWT_SECRET_FILE` and
  `ADMIN_PASSWORD_FILE` read Docker or Kubernetes secrets.
- Never commit a `.env` file with real values.
- The config folder and the database backups in it hold password hashes and stored credentials
  (SMTP, ComicVine). Keep them private.

### Kubernetes

ComicMaintainer uses SQLite and a file watcher, so exactly one pod may run at a time. The example
in `docs/kubernetes-deployment.yaml` runs one replica with the `Recreate` strategy and reads its
secrets from a Kubernetes Secret.

## Security Features

- **Authentication and roles** for every API endpoint (JWT sign-in, or Authelia forward
  authentication from trusted proxies only), with read-only, user and admin roles.
- **Path validation**: file paths from requests must resolve, links included, inside the watched
  or duplicates folder before any file is read, changed or deleted.
- **No external programs**: comic archives and images are read in-process (SharpCompress,
  Magick.NET), so no request reaches a shell or another program.
- **Parameterized database access** through Entity Framework Core.
- **Rate limiting** of signed-out requests per client.
- **Automatic security updates** proposed by Dependabot, and the scans above.

## Additional Resources

- [Docker security best practices](https://docs.docker.com/engine/security/)
- [ASP.NET Core security](https://learn.microsoft.com/aspnet/core/security/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
