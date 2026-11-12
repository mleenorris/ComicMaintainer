# Contributing to ComicMaintainer

Thank you for helping out. This guide covers setting up, making a change and getting it merged.

By taking part, you agree to keep this a respectful and inclusive project.

## Getting started

1. Fork the repository on GitHub and clone your fork.
2. Create a branch from `dotnet`, the default branch:
   ```bash
   git checkout dotnet
   git checkout -b feature/your-feature-name
   ```

You need:

- [.NET 11 SDK](https://dotnet.microsoft.com/download)
- Node.js 22.22+, 24.15+ or 26+, for the web UI's tests
- Docker, for the container smoke test
- CodeQL and Trivy, only to run the security scans locally

### Setting up a Windows machine

`scripts/dev.ps1` installs everything above except Docker, at the versions CI uses, under
`%USERPROFILE%\comicmaintainer-tools`. It needs no administrator rights, and it keeps an existing .NET 11 SDK or
Node.js if they are recent enough:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 setup            # add -Android for the Android toolchain
. .\scripts\dev.ps1 env                                                   # put the tools on PATH in this session
```

On Linux or macOS, install the versions named at the top of `scripts/dev.ps1` with your package manager; the `ci`
command below then finds them on PATH when run with PowerShell 7.

### Running CI's checks locally

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 ci                 # everything
powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 ci -Only codeql    # or any of: build, unit, integration,
                                                                            #   js, nuget, codeql, docker
```

`ci` runs the same steps as the workflows: the strict build, unit tests with the coverage gate, integration tests,
the JavaScript tests and their coverage gates, the NuGet advisory check, CodeQL's security-and-quality queries for C#,
JavaScript and the workflows, and, when Docker is installed, the image build, container smoke test and Trivy scan. It
ends with a summary and fails if any step failed. The CodeQL findings are listed in `codeql-findings.txt` under
`%USERPROFILE%\comicmaintainer-tools\ci-results\<checkout folder>`, with the SARIF files beside them.

Two differences from CI remain: the macOS file-operation job only runs on a Mac, and coverage reads about one point
lower on Windows than on Linux, so the unit step's gate is 96.3% on Windows and CI's 97% elsewhere.

## Building and running

```bash
dotnet build ComicMaintainer.sln

export WATCHED_DIR=/path/to/test/comics
export DUPLICATE_DIR=/path/to/test/duplicates
export CONFIG_DIR=/path/to/test/config
dotnet run --project src/ComicMaintainer.WebApi
```

The app listens on `http://localhost:5072` (see `src/ComicMaintainer.WebApi/Properties/launchSettings.json`). Add
`-- --urls http://localhost:5000` to use another address. Swagger UI is at `/swagger` in this mode.

`ComicMaintainer.sln` and a bare `dotnet test` do not need the Android workload. The Android app has its own solution,
`src/ComicMaintainer.Android.sln`. See [MAUI_ANDROID_SETUP.md](docs/MAUI_ANDROID_SETUP.md).

## Project structure

```
ComicMaintainer/
├── src/
│   ├── ComicMaintainer.Core/           # Domain logic: services, models, EF Core data and migrations
│   ├── ComicMaintainer.WebApi/         # ASP.NET Core host
│   │   ├── Controllers/                # REST API and OPDS
│   │   ├── Startup/                    # Service registration and the request pipeline
│   │   └── wwwroot/                    # The web UI (vanilla JavaScript SPA and PWA)
│   ├── ComicMaintainer.MauiApp/        # Android client, built from ComicMaintainer.Android.sln
│   └── Directory.Build.props           # StrictWarnings switch used by CI
├── tests/
│   ├── ComicMaintainer.Tests/          # xUnit tests
│   └── js/                             # node:test tests for the web UI
├── docs/                               # Documentation; docs/archive holds history
├── scripts/                            # Coverage, container smoke test and CI helpers
├── .github/workflows/                  # CI and release
├── Dockerfile.dotnet
└── docker-compose.dotnet.yml
```

## Testing

All bug fixes and new features must include tests. [TESTING_POLICY.md](docs/TESTING_POLICY.md) has the rules and the
gates CI enforces.

```bash
# .NET
dotnet test
dotnet test --filter "Category!=Integration"
dotnet test --filter "Category=Integration"
dotnet test --filter "FullyQualifiedName~ComicFileProcessorTests"

# .NET with coverage, as CI measures it
./scripts/run-tests-with-coverage.sh

# Web UI
npm ci
npm test
npm run test:coverage   # fails below 99% lines, 97% branches, 99% functions

# The container, end to end
docker build -f Dockerfile.dotnet -t comicmaintainer:ci .
./scripts/container-smoke-test.sh
```

`scripts/dev.ps1 ci` runs all of these the way CI does; see [Running CI's checks locally](#running-cis-checks-locally).

[tests/README.md](tests/README.md) describes the test layout and helpers.

## Conventions

CI does not run a formatter, so match the code around your change. CI builds `src/` with warnings as errors, and you can
do the same locally:

```bash
dotnet build ComicMaintainer.sln -c Release -p:StrictWarnings=true
```

### Server

- **Dependency injection.** Use constructor injection and interfaces, and register services in
  `src/ComicMaintainer.WebApi/Startup/`. See [IOC_DESIGN_PRINCIPLES.md](docs/IOC_DESIGN_PRINCIPLES.md).
- **Errors.** Do not add generic `catch (Exception)` blocks to controllers. Unexpected failures reach
  `GlobalExceptionHandler`, which returns a problem document with a correlation id and no exception text. Put the
  user-facing text in `[FailureMessage("...")]` on the action, and catch only the failures you expect and can answer.
- **Logging.** Use `ILogger<T>`. Pass anything that comes from a user or a file name through
  `LoggingHelper.SanitizeForLog` or `LoggingHelper.SanitizePathForLog`.
- **Authorization.** Write actions on the library and settings controllers get their policy from
  `WriteOperationAuthorizationConvention`. When you add a controller with write actions, add it to the convention's
  table. A write that only touches the caller's own state, such as read status, takes `[PerUserWriteOperation]`.
- **Settings.** Each environment override is one row in `AppSettingsEnvironmentOverrides`, which also drives the
  read-only state on the Settings page and the `409` refusal. Add new variables to
  [ENVIRONMENT_VARIABLES.md](docs/ENVIRONMENT_VARIABLES.md).
- **Database.** Schema changes need an EF Core migration. See [DATABASE.md](docs/DATABASE.md).
- **Reader.** Bump `ComicReaderService.PageListVersion` when the rules for which archive entries are pages, or their
  order, change. A test holds the current rules to the current version.

### Web UI

- The SPA is ordered classic scripts in `wwwroot/js/`: `shared.js`, `app/*.js`, then `main.js`. When you add a script,
  add it to the `<script>` list in `index.html`, to `APP_SCRIPTS` in `sw.js`, and to `WwwrootFiles.AppScripts` in the C#
  tests.
- The standalone pages (`reader.html`, `login.html`, `setup.html`, `offline.html`) have no inline scripts. Each loads
  `js/pages/<page>.js`. These are listed in `WwwrootFiles.PageScripts`. The ones the service worker serves offline are
  also in `PAGE_SCRIPTS` in `sw.js`.
- Make requests with `apiFetch` from `js/app/api.js`. It applies credentials, handles `401` and `403`, and adds
  `BASE_PATH`. To log every request in the browser console, run `localStorage.setItem('debug_api', 'true')`.

## Submitting changes

1. Run the tests above, and update the documentation your change affects. Add notable changes to `CHANGELOG.md`.
2. Push your branch and open a pull request into `dotnet`.
3. Fill in the pull request template, and include screenshots for UI changes.
4. Respond to review comments by pushing more commits.

`PR Test Check` fails a pull request that changes server or web UI code without changing `tests/`. If a change genuinely
needs no test, say why in the description and add the `no-tests-needed` label.

Merging to `dotnet` releases automatically. See [GITHUB_ACTIONS_DOTNET.md](docs/GITHUB_ACTIONS_DOTNET.md). A commit
message containing `[skip-version-bump]` is tested and published as `:dotnet` without a new version. Each merge increments
the patch number, unless the project's version has no tag yet, in which case that version is released as it stands.

## Getting help

- **Questions and bugs**: [GitHub Issues](https://github.com/mleenorris/ComicMaintainer/issues)
- **Security problems**: see [SECURITY.md](SECURITY.md), and do not open a public issue

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
