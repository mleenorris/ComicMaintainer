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

### Automatic fixes

When a scan of `dotnet` finishes, `security-autofix.yml` looks at the open code scanning alerts. If
any has a security severity of **medium** or higher, and can be fixed (Trivy findings count only once
a fixed version exists), Claude fixes them, adds tests, and opens one pull request labelled
`security-autofix`. It goes through the usual checks and review before it can be merged.

- No new attempt starts while a `security-autofix` pull request is open.
- Each set of alerts is attempted once; an alert that is still open is attempted again after about a
  week, when the record of the attempt expires.
- Alerts are never dismissed or suppressed automatically. Claude explains in the pull request any
  alert it left alone.
- It can also be started by hand from the Actions tab (**Security Autofix** → **Run workflow**).

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
- Pass secrets as files rather than variables where you can: `JWT_SECRET_FILE`,
  `ADMIN_PASSWORD_FILE` and `SETUP_TOKEN_FILE` read Docker or Kubernetes secrets.
- Never commit a `.env` file with real values.
- The config folder and the database backups in it hold password hashes and stored credentials
  (SMTP, ComicVine, notification tokens). Keep them private.

### Kubernetes

ComicMaintainer uses SQLite and a file watcher, so exactly one pod may run at a time. The example
in `docs/kubernetes-deployment.yaml` runs one replica with the `Recreate` strategy and reads its
secrets from a Kubernetes Secret.

## Security Features

### Signing In

- **Authentication and roles** for every API endpoint, with read-only, user and admin roles.
  Requests are signed in with a session token, Authelia forward authentication from trusted
  proxies only, or an API key. HTTP Basic is accepted only on the OPDS catalog.
- **Session tokens** are sent in the `Authorization` header and are never accepted in a URL. Each
  is revoked by signing out, a password change or reset, a role change, or disabling or deleting
  the account. The event stream uses its own single-use token, valid for 60 seconds.
- **Account administration** is for administrators only, on the Users page and `/api/users`.
  Creating an account, changing a role, setting a password and enabling an account need the
  administrator's own password, so a stolen session token alone cannot create or take over an
  account. Nobody can change their own role, status or account there, or set their own password,
  and the last active administrator cannot be demoted, disabled or deleted. A disabled account is
  refused however it signs in, Authelia included.
- **First-run setup** requires a one-time code from the server's log, or `SETUP_TOKEN`. It is only
  available while no account exists, and never under Authelia.
- **Published example passwords**, such as `Admin123!`, are refused when setting a password, at
  startup and at sign-in.
- **Account lockout**: five wrong passwords in a row, whether at sign-in, password change, API key
  creation or an administrator confirming a change on the Users page, lock the account for 15
  minutes. The lockout ends on its own, or when an administrator sets a new password for the
  account. The trade-off: anyone who knows a username can keep that account locked by failing
  repeatedly.
- **Attempt limits**: sign-in, registration, first-run setup, password change, API key creation
  and OPDS sign-in share a limit of 10 attempts per 5 minutes per client address. Requests over
  it get `429` with a `Retry-After` header. Behind a reverse proxy, set `TRUSTED_PROXIES`, or all
  clients share that one limit.
- **Uniform failures**: every sign-in failure answers `Invalid username or password`, and failed
  attempts are logged with the client's address to `auth.log`.

### API Keys

- A key acts as the account that owns it, with that account's current roles. It is shown once and
  stored only as a SHA-256 hash.
- Keys are accepted only in the `X-API-Key` header, never in a URL or as a bearer token. On the
  OPDS catalog, a key can be used as the HTTP Basic password instead.
- Creating a key requires the account's current password, except in an Authelia session, whose
  account has no password of its own. An API key cannot create a key or change a password.
- Changing the password or signing out of all sessions revokes the key, as do an administrator
  resetting the password and disabling the account.

### OPDS Catalog

The catalog is off by default and answers 404 until it is enabled. HTTP Basic is only accepted
under `/opds`:

- Attempts share the sign-in limit above, and wrong passwords count towards the account lockout.
- A value in the exact form of a generated API key that matches no current key uses one attempt
  but never counts towards the lockout, so a device holding an old key cannot lock out its owner.
- Credentials are only base64-encoded, so use HTTPS.
- With Authelia enabled only API keys are accepted, because `/opds` has to bypass Authelia.
- OPDS clients repeat their credentials on every request, so a verified password is remembered in
  memory for up to 5 minutes after its last use and at most 1 hour, as a salted hash and never in
  clear. Every request still re-checks the account, so a disabled or deleted account, a password
  change or a lockout applies at once.

### Outbound Requests

- **Series image downloads** are guarded against server-side request forgery. Automatic redirects
  are off and every hop is checked again, HTTPS is never downgraded to HTTP, and the app resolves
  the host itself when connecting, refusing private, loopback, link-local and cloud metadata
  addresses, including IPv6 forms that embed them (IPv4-mapped and NAT64). Because the guard must
  make the connection itself, these downloads do not use `HTTP_PROXY` or `HTTPS_PROXY`.
- **Notification targets** may be private addresses, because a Komga or Kavita server on your LAN
  is the normal setup. Configuring and testing them is therefore limited to administrators.
  Redirects are not followed. Tokens, API keys and webhook URLs are write-only, are never logged,
  are left out of traces and are masked in automated error reports. The ntfy topic is returned
  only to administrators, because on a public ntfy server knowing it is enough to subscribe.

### Your Data

- **Path validation**: file paths from requests must resolve, links included, inside the watched
  or duplicates folder before any file is read, changed or deleted.
- **Private by user**: e-reader devices, subscriptions and delivery history belong to the user who
  created them.
- **Private caching**: comic pages and covers are sent with `Cache-Control: private`, so shared
  proxies and CDNs do not store them.
- **Logs** are readable through the API by administrators only.
- **Archive limits**: an archive that would expand or read past fixed limits is refused, so a
  crafted file cannot exhaust memory. See the
  [Performance Tuning Guide](docs/PERFORMANCE_TUNING.md#archive-limits).

### The Application

- **No external programs**: comic archives and images are read in-process (SharpCompress,
  Magick.NET), so no request reaches a shell or another program.
- **Parameterized database access** through Entity Framework Core.
- **Content Security Policy** with `connect-src 'self'`, so injected markup cannot open a
  connection to another origin. Behind a proxy that reports HTTPS, the app also sends
  `Strict-Transport-Security`.
- **Rate limiting** of requests per client as well as the attempt limits above.
- **Automatic security updates** proposed by Dependabot, and the scans above.

Automated error reports are off until an administrator turns them on and supplies a GitHub token.
They are filed against the public `mleenorris/ComicMaintainer` repository, with paths, email
addresses and credentials redacted.

## Additional Resources

- [Docker security best practices](https://docs.docker.com/engine/security/)
- [ASP.NET Core security](https://learn.microsoft.com/aspnet/core/security/)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
