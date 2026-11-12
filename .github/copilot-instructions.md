# ComicMaintainer agent instructions

ComicMaintainer is a self-hosted comic library manager: ASP.NET Core on .NET 11, EF Core with SQLite, a vanilla-JavaScript
SPA and PWA, and a small .NET MAUI Android client. The default branch is `dotnet`. There is no Python code any more;
anything in `docs/archive` describes the past.

## Layout

- `src/ComicMaintainer.Core`: services, models, EF Core context (`Data/`) and migrations (`Migrations/`)
- `src/ComicMaintainer.WebApi`: controllers, `Startup/` registration and pipeline, `wwwroot/` web UI
- `src/ComicMaintainer.MauiApp`: Android app, in its own solution `src/ComicMaintainer.Android.sln`
- `tests/ComicMaintainer.Tests`: xUnit and Moq; `tests/js`: `node:test` with jsdom
- `docs/`: current documentation, indexed by `docs/README.md`; `docs/archive/` is history and is not maintained

## Build and test

```bash
dotnet build ComicMaintainer.sln                                      # no Android workload needed
dotnet build ComicMaintainer.sln -c Release -p:StrictWarnings=true    # as CI builds src/: warnings are errors
dotnet test
dotnet test --filter "Category!=Integration"
dotnet test --filter "FullyQualifiedName~SomeTests"
npm ci
npm test
npm run test:coverage
```

Only build `src/ComicMaintainer.MauiApp` or `src/ComicMaintainer.Android.sln` if the Android workload is installed.

## Rules CI enforces

Pull requests target `dotnet`. They fail on any of these:

- Server C# or web UI JavaScript changed with no change under `tests/`, unless the `no-tests-needed` label is set
- .NET line coverage below 97%, or JavaScript coverage below 99% lines, 97% branches, 99% functions
- Compiler warnings in `src/`
- A failing test on Linux or Windows, in the macOS delete-job tests, or in the container smoke test
- A fixable critical or high vulnerability in the image, or a high or critical advisory in a NuGet package

Skip a test only by capability, never by operating system name: use `[SymbolicLinkFact]`, `[SymbolicLinkTheory]`, `[ExclusiveLockFact]`
or `[BashFact]` from `tests/ComicMaintainer.Tests/Helpers/TestCapabilities.cs`. Build services that need many collaborators
with `Helpers/TestServices.cs`. Tests that wait for "nothing happened" need a positive control first.

## Conventions

- **Dependency injection.** Constructor injection, interfaces, registrations in `Startup/`. Every collaborator production
  registers is a required parameter, and `ServiceGraphTests` enforces it. Do not hide a dependency behind
  `IServiceProvider`, `Func<T>` or `Lazy<T>`. See `docs/IOC_DESIGN_PRINCIPLES.md`.
- **Errors.** No generic `catch (Exception)` in controllers. Failures reach `GlobalExceptionHandler`. Use
  `[FailureMessage]` for the user-facing text, narrow catches for expected failures, and never put exception text in a
  response.
- **Authorization.** `WriteOperationAuthorizationConvention` protects write actions by controller name. Add a new
  controller to its table. Mark writes to the caller's own state with `[PerUserWriteOperation]`.
- **Logging.** `ILogger<T>`. Pass user input and paths through `LoggingHelper.SanitizeForLog` or `SanitizePathForLog`.
- **Settings.** Environment overrides live in the one table `AppSettingsEnvironmentOverrides`. Document new variables in
  `docs/ENVIRONMENT_VARIABLES.md`. `tests/.../Startup/DocumentedEnvironmentVariablesTests.cs` fails on a documented
  variable the app does not read, and it reads the `### Configuration Options` table in `README.md`.
- **Database.** Schema changes need an EF Core migration in `src/ComicMaintainer.Core/Migrations`. See `docs/DATABASE.md`.
- **Reader.** Bump `ComicReaderService.PageListVersion` when the rules for which archive entries are pages, or their
  order, change.
- **New web UI scripts.** Add to the `<script>` list in `index.html`, `APP_SCRIPTS` in `sw.js` and
  `WwwrootFiles.AppScripts` in the tests. Standalone pages load `js/pages/<page>.js` and have no inline scripts.
- **Web UI requests.** Use `apiFetch` from `js/app/api.js`, and build URLs with `apiUrl` so `BASE_PATH` works.

## Documentation

Update the docs a change affects. Documents describe how things work now; history goes in `CHANGELOG.md`. Check a route,
setting or default against the code before documenting it.
