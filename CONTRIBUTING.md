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

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Node.js 22.22+, 24.15+ or 26+, for the web UI's tests
- Docker, for the container smoke test

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
message containing `[skip-version-bump]` is tested and published as `:dotnet` without a new version.

## Getting help

- **Questions and bugs**: [GitHub Issues](https://github.com/mleenorris/ComicMaintainer/issues)
- **Security problems**: see [SECURITY.md](SECURITY.md), and do not open a public issue

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
