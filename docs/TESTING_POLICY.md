# Testing Policy

Every bug fix and new feature ships with tests. This page says what is expected, what CI enforces, and how to run the
tests.

## What needs tests

| Change | Tests |
| --- | --- |
| Bug fix | A test that fails without the fix and passes with it |
| New feature | Unit tests, and integration tests when several components work together |
| API change | Tests for each new or changed endpoint |
| Security fix | A test that shows the hole is closed |
| Change to how files are processed | Tests with real archives where a mock would hide the behaviour |

Documentation-only changes need no tests. If a change genuinely needs none, such as a pure refactor that existing tests
cover, say why in the pull request and add the `no-tests-needed` label.

## What CI enforces

A pull request into `dotnet` must pass all of these. [GITHUB_ACTIONS_DOTNET.md](GITHUB_ACTIONS_DOTNET.md) describes the
workflows.

| Check | Workflow | Fails when |
| --- | --- | --- |
| Tests included | `pr-test-check.yml` | Server C# or web UI JavaScript changed and nothing under `tests/` did, unless the `no-tests-needed` label is set. EF Core migrations are not counted as source |
| Unit tests | `test-dotnet.yml` | A JavaScript or .NET test fails, or a test hangs for 5 minutes |
| .NET coverage | `test-dotnet.yml` | Line coverage of the unit tests is below 97%. Migrations and generated code are excluded by `coverlet.runsettings` |
| JavaScript coverage | `test-dotnet.yml` | Below 99% lines, 97% branches or 99% functions (`package.json`) |
| Warnings | `test-dotnet.yml` | The projects in `src/` build with a compiler warning (`-p:StrictWarnings=true`). NuGet audit warnings are left to the security scan |
| Windows | `test-dotnet.yml` | Any test fails on Windows |
| macOS | `test-dotnet.yml` | A delete-job test fails on macOS |
| Integration tests | `test-dotnet.yml` | A test with `Category=Integration` fails |
| Container | `integration-tests-e2e.yml` | The image does not start and process a comic, or has a fixable critical or high vulnerability |

Do not lower a gate to get a change through. Raise the .NET gate as coverage grows.

## Kinds of test

- **Unit tests** check one class with its collaborators mocked. Cover the failure paths and the edges as well as the
  normal case.
- **Integration tests** carry `[Trait("Category", "Integration")]` and run the real API through
  `WebApplicationFactory<Program>`: EF Core, the middleware pipeline and authorization.
- **Regression tests** reproduce a bug. Confirm the test fails without the fix.
- **Web UI tests** are in `tests/js`, using `node:test`. Small helpers run in a `node:vm` sandbox, and pages run in jsdom
  through `tests/js/support/dom.js`.

## Writing good tests

- Name tests `MethodName_Scenario_ExpectedBehavior`, and mirror the source layout, such as
  `tests/ComicMaintainer.Tests/Services/MyServiceTests.cs`.
- Keep tests independent. Clean up files and connections in `Dispose`.
- Test behaviour, not implementation details.
- Do not write a test that passes whether or not the bug exists. Check that it fails when the code is broken.
- A test that waits to see that nothing happened needs a positive control first: wait for something that must happen,
  then assert that the other thing did not.
- Build services that take many collaborators through `Helpers/TestServices.cs`, so a test names only the ones it cares
  about.

### Tests that depend on the platform

Skip a test by what it needs, not by which operating system runs it. The attributes in `Helpers/TestCapabilities.cs`
detect the capability once, by trying the operation, and give the reason in the skip message:

| Attribute | Needs |
| --- | --- |
| `[SymbolicLinkFact]`, `[SymbolicLinkTheory]` | Permission to create symbolic links |
| `[ExclusiveLockFact]` | A specific behaviour for a file held open without sharing |
| `[BashFact]` | bash, to run the container scripts |

### Rules a test holds in place

- `ServiceGraphTests.Constructors_DoNotDeclareCollaboratorsAsOptional` fails when a production collaborator is optional.
  See [IOC_DESIGN_PRINCIPLES.md](IOC_DESIGN_PRINCIPLES.md).
- `DocumentedEnvironmentVariablesTests` fails when a document or deployment file sets a variable the app does not read,
  and it reads the `### Configuration Options` table in `README.md`.
- `AndroidAppContractTests` runs the Android app's client against the real server pipeline.
- A test pins the current page-list rules to `ComicReaderService.PageListVersion`. Bump that constant when the rules for
  which archive entries are pages, or their order, change.

## Running tests

```bash
# .NET: everything, or part of it
dotnet test
dotnet test --filter "Category!=Integration"
dotnet test --filter "Category=Integration"
dotnet test --filter "FullyQualifiedName~ComicFileProcessorTests"

# .NET coverage, as CI measures it (requires bash)
./scripts/run-tests-with-coverage.sh

# Web UI
npm ci
npm test
npm run test:coverage

# The container, end to end (requires Docker)
docker build -f Dockerfile.dotnet -t comicmaintainer:ci .
./scripts/container-smoke-test.sh
```

These need no Android workload.

## Testing frameworks

- [xUnit](https://xunit.net/) and [Moq](https://github.com/moq/moq4) for .NET
- `node:test` and [jsdom](https://github.com/jsdom/jsdom) for the web UI

Good examples are `tests/ComicMaintainer.Tests/Services/ComicFileProcessorTests.cs`,
`tests/ComicMaintainer.Tests/Controllers/FilesControllerTests.cs` and `tests/ComicMaintainer.Tests/Integration/`.
