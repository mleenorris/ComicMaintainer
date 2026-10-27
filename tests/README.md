# ComicMaintainer Tests

This directory contains unit tests for the ComicMaintainer project.

## Test Structure

```
tests/
├── ComicMaintainer.Tests/
│   ├── Authentication/    # Authelia forward-auth handler
│   ├── Authorization/     # Write-operation authorization convention
│   ├── Controllers/       # API Controller tests
│   ├── Data/              # EF Core context, factory and SQLite setup
│   ├── ErrorReporting/    # Automated error report pipeline
│   ├── Fuzz/              # Randomized-input tests of models and path helpers
│   ├── HealthChecks/      # Health check implementations
│   ├── Helpers/           # Shared test doubles
│   ├── Hubs/              # SignalR progress hub
│   ├── Infrastructure/    # Global exception handler
│   ├── Integration/       # In-process API tests (WebApplicationFactory, Category=Integration)
│   ├── Middleware/        # Request pipeline middleware and filters
│   ├── Models/            # Model tests
│   ├── Reader/            # Reading progress, sessions and preferences
│   ├── Security/          # CORS and security headers
│   ├── Services/          # Service layer tests
│   ├── Startup/           # Service registration and startup wiring
│   ├── Utilities/         # Utility class tests
│   ├── Wwwroot/           # Static checks over the shipped web UI files
│   └── xunit.runner.json  # xUnit configuration
├── js/                     # node:test tests for the web UI's JavaScript
│   └── support/
│       ├── sandbox.js     # Runs wwwroot scripts in a bare node:vm context
│       ├── dom.js         # Loads the real HTML pages in jsdom with browser stubs
│       ├── app.js         # bootApp(): the signed-in SPA at a route
│       └── *-helpers.js   # Per-area fixtures and helpers (core, email, jobs, library, reader, series, settings, sw)
└── README.md
```

## Running Tests

### Basic Test Run
```bash
dotnet test
```

### Run Tests with Detailed Output
```bash
dotnet test --verbosity normal
```

### Run Tests for Specific Project
```bash
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj
```

### Run Tests with Code Coverage
```bash
# From the repository root
./scripts/run-tests-with-coverage.sh

# Or manually (coverlet.runsettings excludes EF migrations and generated code):
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings \
  --results-directory ./TestResults
```

### JavaScript Tests
```bash
npm ci                  # installs jsdom, the only dependency
npm test                # all tests/js/**/*.test.js
npm run test:coverage   # plus a coverage table and coverage/lcov.info
```

Page-level tests load a real page from `wwwroot` with `loadPage()` from
`tests/js/support/dom.js` and drive it like a browser:

```js
const { loadPage, respond } = require('./support/dom');

test('reports a busy server', async () => {
    const page = loadPage({                       // index.html by default
        localStorage: { jwt_token: token },       // signed-in session
        routes: { 'GET /api/preferences': {} }
    });
    await page.settle();                          // let boot-time fetches resolve
    page.route('POST /api/jobs', respond(409, { error: 'busy' }));
    page.click('#startButton');
    await page.waitFor(() => page.fetchCalls.some(c => c.path === '/api/jobs'));
});
```

Use `t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] })` and
`t.mock.timers.tick(ms)` for debounces and polling (tick one interval period at
a time); `page.settle()`/`page.waitFor()` keep using real timers. Values read
from the page (`page.run(code)`) belong to the page's realm: compare them with
`assert.deepStrictEqual` via `page.evaluate(code)` or `plain(value)`.

Unrouted requests answer 404 and are listed in `page.unmatchedRequests`;
`page.navigations`, `page.dialogs`, `page.eventSources` and
`page.serviceWorkerRegistrations` record what the page did. Redirects and
reloads are only recorded (the page stays on its document); fragment changes
are applied and fire `hashchange`. Scripts run with `file:` URLs so Node's
coverage attributes them to the files under `wwwroot`. Inline handlers in
markup the app renders with `renderHtml()`/`appendHtml()` run as they would in
a browser, so `page.click()` on a rendered button calls its `onclick` code.

Tests of the signed-in SPA can use `bootApp()` from `tests/js/support/app.js`,
which loads `index.html` at a route with a valid session token and the
`/api/auth/*` answers already routed (`user: { canModifyLibrary: false,
canAdminister: false }` boots a read-only user), then waits for the boot:

```js
const { bootApp } = require('./support/app');

const page = await bootApp({ hash: '#/settings', routes: { 'GET /api/settings': { ... } } });
```

The standalone pages (`reader.html`, `login.html`, `setup.html`,
`offline.html`) keep no inline code: each loads its own classic script from
`wwwroot/js/pages/`, so `loadPage({ html: 'reader.html', url: ... })` runs
`js/shared.js` and `js/pages/reader.js` and measures them like the app scripts.

## Code Coverage

### Viewing Coverage Reports

After running tests with coverage, you can generate HTML reports:

1. Install the report generator tool (one-time setup):
   ```bash
   dotnet tool install -g dotnet-reportgenerator-globaltool
   ```

2. Generate the HTML report:
   ```bash
   reportgenerator \
     -reports:"./TestResults/**/coverage.cobertura.xml" \
     -targetdir:"./TestResults/CoverageReport" \
     -reporttypes:"Html;HtmlSummary"
   ```

3. Open `./TestResults/CoverageReport/index.html` in your browser

### Coverage Thresholds

The project aims for:
- **Minimum**: 60% code coverage
- **Target**: 80% code coverage

## Test Frameworks and Libraries

- **xUnit**: Primary testing framework
- **Moq**: Mocking framework for dependencies
- **Coverlet**: Code coverage collection
- **FluentAssertions** (optional): More expressive assertions

## Writing Tests

### Test Naming Convention
```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange
    // Act
    // Assert
}
```

### Example Test
```csharp
[Fact]
public void GetStatus_WatcherRunning_ReturnsOkWithEnabledTrue()
{
    // Arrange
    _mockWatcher.Setup(w => w.IsRunning).Returns(true);

    // Act
    var result = _controller.GetStatus();

    // Assert
    var okResult = Assert.IsType<OkObjectResult>(result.Result);
    Assert.NotNull(okResult.Value);
}
```

### Theory Tests (Data-Driven)
```csharp
[Theory]
[InlineData("test.cbz", true)]
[InlineData("test.txt", false)]
public void IsComicArchive_VariousExtensions_ReturnsCorrectResult(string fileName, bool expected)
{
    var result = ComicFileExtensions.IsComicArchive(fileName);
    Assert.Equal(expected, result);
}
```

## Continuous Integration

Tests are automatically run on:
- Push to `main`, `develop` or `dotnet` branches
- Pull requests targeting `main`, `develop` or `dotnet`
- Manual workflow dispatch

There are two CI workflows that gate every PR:

| Workflow | Job(s) | What it runs |
|---|---|---|
| `.github/workflows/test-dotnet.yml` | **Unit Tests** | `npm run test:coverage` (JavaScript), then `dotnet test --filter "Category!=Integration"` with coverage |
|  | **Integration Tests** | `dotnet test --filter "Category=Integration"` (in-process API tests) |
| `.github/workflows/integration-tests-e2e.yml` | **Docker Container Smoke Test** | Builds `Dockerfile.dotnet`, starts the container, hits `/api/version`, and verifies the file watcher reacts to a fixture `.cbz` |

All three jobs must pass for a PR to be considered green. To make them
required for merge, add their check names to branch protection for
`main`/`develop` in repository settings.

## Test Categories

### Unit Tests
- Test individual components in isolation
- Use mocks for dependencies
- Fast execution
- Run on every push and PR by the **Unit Tests** job in
  `.github/workflows/test-dotnet.yml`.

### Integration Tests
- Marked with `[Trait("Category", "Integration")]`.
- Two flavours:
  1. **In-process API integration tests** (`tests/ComicMaintainer.Tests/Integration/`):
     spin up the real Web API via `WebApplicationFactory<Program>` and exercise it
     over HTTP. Real EF Core (SQLite) is used, so DB migrations and most
     middleware run end-to-end.
  2. **End-to-end container smoke test** (`scripts/container-smoke-test.sh`):
     builds the Docker image (`Dockerfile.dotnet`), starts a container, verifies
     `/api/version` is healthy, and checks that the file watcher reacts to a
     fixture `.cbz` dropped into the mounted watch directory. Run in CI by
     `.github/workflows/integration-tests-e2e.yml`.
- Both run on every PR targeting `main`/`develop`.

### Running only one category locally

```bash
# Unit tests only (fast)
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj \
    --filter "Category!=Integration"

# In-process integration tests only
dotnet test tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj \
    --filter "Category=Integration"

# End-to-end container smoke test (requires Docker)
docker build -f Dockerfile.dotnet -t comicmaintainer:ci .
./scripts/container-smoke-test.sh
```

## Best Practices

1. **Keep tests independent**: Each test should be able to run independently
2. **Use meaningful names**: Test names should describe what they test
3. **Follow AAA pattern**: Arrange, Act, Assert
4. **One assertion per test**: Focus on testing one thing
5. **Clean up resources**: Dispose of resources in test teardown
6. **Mock external dependencies**: Keep tests fast and reliable

## Current Test Coverage

Measured with the full suites: .NET with coverlet and `coverlet.runsettings`,
JavaScript with `npm run test:coverage`.

| Suite | Tests | Lines | Branches |
|---|---|---|---|
| .NET (`ComicMaintainer.Core`, `ComicMaintainer.WebApi`) | 4,678 | 99.0% | 97.5% |
| JavaScript (`wwwroot/**/*.js`) | 1,130 | 99.9% | 98.3% |

The **Unit Tests** CI job collects .NET coverage from `Category!=Integration`
only (4,610 tests: 98.8% lines, 97.4% branches); the OpenTelemetry wiring in
`TelemetryExtensions` is covered by the integration tests.

What remains uncovered is mostly defensive: cleanup `catch` blocks for
file-system failures that cannot be provoked when tests run as root, retry
paths behind database insert races, and element-exists guards in the web UI.

## Adding New Tests

When adding new features:
1. Write tests first (TDD) or alongside the feature
2. Ensure existing tests still pass
3. Aim for high code coverage of new code
4. Add both positive and negative test cases
5. Test edge cases and error conditions

## Troubleshooting

### Tests Failing Locally But Passing in CI
- Check .NET SDK version matches CI
- Ensure all dependencies are restored
- Check for environment-specific issues

### Slow Test Execution
- Ensure tests are truly unit tests (no I/O)
- Check for excessive setup/teardown
- Consider parallelization settings in xunit.runner.json

### Coverage Not Generated
- Ensure coverlet.collector package is installed
- Check that `--collect:"XPlat Code Coverage"` flag is used
- Verify TestResults directory permissions
