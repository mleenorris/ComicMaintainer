# Contributing to ComicMaintainer

Thank you for considering contributing to ComicMaintainer! This document provides guidelines and instructions for contributing to the project.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Setup](#development-setup)
- [Making Changes](#making-changes)
- [Testing](#testing)
- [Code Quality](#code-quality)
- [Submitting Changes](#submitting-changes)
- [Documentation](#documentation)

## Code of Conduct

By participating in this project, you agree to maintain a respectful and inclusive environment for all contributors.

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/YOUR_USERNAME/ComicMaintainer.git
   cd ComicMaintainer
   ```
3. **Create a branch** for your changes:
   ```bash
   git checkout -b feature/your-feature-name
   ```

## Development Setup

### Prerequisites

- .NET 9.0 SDK
- Docker (for container testing)
- Git

The Android client under `src/ComicMaintainer.MauiApp/` targets `net9.0-android` and
needs the MAUI workload (`dotnet workload install maui-android`). It is not part of the
normal development loop, and building `ComicMaintainer.sln` without that workload fails
with NETSDK1147 — build the individual projects below instead.

### Restore and Build

```bash
dotnet restore src/ComicMaintainer.Core/ComicMaintainer.Core.csproj
dotnet restore src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj
dotnet restore tests/ComicMaintainer.Tests/ComicMaintainer.Tests.csproj

dotnet build src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj --configuration Release
```

### Run the Application Locally

```bash
# Point the service at a library and pick a port
export WATCHED_DIR=/path/to/test/comics
export WEB_PORT=5000

dotnet run --project src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj
```

See [.env.example](.env.example) for the full set of supported environment variables.

## Making Changes

### Code Style

- Follow the standard .NET naming conventions (PascalCase for types and members,
  `_camelCase` for private fields)
- Nullable reference types are enabled — keep new code warning-clean
- Use meaningful variable and method names
- Keep methods focused and small
- Add XML doc comments to public types and members
- Explain *why* in comments, not *what*; the code already says what

### Commit Messages

Write clear, descriptive commit messages:

```
Add health check endpoint for container orchestration

- Added /health and /api/health endpoints
- Returns status of watched directory, database, and watcher
- Returns 200 OK for healthy, 503 for unhealthy
```

## Testing

⚠️ **IMPORTANT**: All bug fixes and new features **MUST** include tests. See [TESTING_POLICY.md](docs/TESTING_POLICY.md) for detailed requirements.

### Testing Requirements

**Before submitting a pull request, you MUST:**
1. ✅ Add unit tests for all new/changed code
2. ✅ Add integration tests for features involving multiple components
3. ✅ Ensure all tests pass locally
4. ✅ Verify code coverage hasn't decreased
5. ✅ Include test details in the PR description

### Run All Tests

```bash
# Run all tests
dotnet test

# Run only unit tests (fast)
dotnet test --filter "Category!=Integration"

# Run only integration tests (in-process API via WebApplicationFactory)
dotnet test --filter "Category=Integration"

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run tests with verbose output
dotnet test --verbosity normal
```

### Run End-to-End Container Smoke Test

The Docker-based smoke test verifies that the published image starts, serves
`/api/version`, and the file watcher reacts to a comic dropped into the
watched directory.

```bash
docker build -f Dockerfile.dotnet -t comicmaintainer:ci .
./scripts/container-smoke-test.sh
```

This is the same script run by the **Docker Container Smoke Test** job in
`.github/workflows/integration-tests-e2e.yml` on every PR.

### Run Specific Test File

```bash
# Run specific test class
dotnet test --filter "FullyQualifiedName~ComicFileProcessorTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~ComicFileProcessorTests.NormalizeSeriesName_WithUnderscores_ReplacesWithColons"
```

### Run Tests with Coverage Report

```bash
# Generate HTML coverage report
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults

# View coverage (requires reportgenerator tool)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:"./TestResults/**/coverage.cobertura.xml" -targetdir:"./TestResults/html" -reporttypes:Html
```

### Add New Tests

**When adding new features or fixing bugs:**
1. Create or update test files in `tests/ComicMaintainer.Tests/`
2. Mirror the source code structure (e.g., `Services/MyServiceTests.cs`)
3. Follow existing test patterns (see example test files)
4. Ensure tests are independent and can run in any order
5. Use descriptive test names: `MethodName_Scenario_ExpectedBehavior`
6. Test both success and failure scenarios
7. Test edge cases and boundary conditions
8. Aim for at least 80% code coverage for new code

**Example Unit Test:**
```csharp
[Theory]
[InlineData("input", "expected")]
public void MethodName_Scenario_ExpectedBehavior(string input, string expected)
{
    // Arrange
    var service = new MyService();
    
    // Act
    var result = service.MyMethod(input);
    
    // Assert
    Assert.Equal(expected, result);
}
```

**Example Integration Test:**
```csharp
[Fact]
public async Task ProcessFile_WithValidFile_CompletesSuccessfully()
{
    // Arrange
    var testFile = CreateTestFile();
    
    // Act
    var result = await _processor.ProcessFileAsync(testFile);
    
    // Assert
    Assert.True(result.Success);
    // ... additional assertions
    
    // Cleanup
    CleanupTestFile(testFile);
}
```

### Test Organization

```
tests/
└── ComicMaintainer.Tests/
    ├── Controllers/         # Controller tests
    ├── Services/           # Service tests
    ├── Models/             # Model tests
    └── Utilities/          # Utility tests
```

For detailed testing guidelines, see [TESTING_POLICY.md](docs/TESTING_POLICY.md).

## Code Quality

### Formatting and Analyzers

```bash
# Verify formatting without writing changes (what you want before pushing)
dotnet format src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj --verify-no-changes

# Apply formatting fixes
dotnet format src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj
```

### Security Scanning

```bash
# Check direct and transitive dependencies against the GitHub Advisory Database
dotnet list src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj package \
  --vulnerable --include-transitive
```

CodeQL (C#, JavaScript and Actions) and Trivy container scanning run in CI — see
`.github/workflows/codeql-analysis.yml` and `.github/workflows/security-scan.yml`.

### Pre-commit Checks

Before committing, ensure:
- [ ] **All tests pass** (`dotnet test`)
- [ ] **New tests added** for all code changes (see [TESTING_POLICY.md](docs/TESTING_POLICY.md))
- [ ] **Code coverage hasn't decreased**
- [ ] No new compiler warnings
- [ ] Code is properly formatted
- [ ] Documentation is updated

## Submitting Changes

1. **Push your changes** to your fork:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Create a Pull Request** on GitHub:
   - Provide a clear title and description
   - Reference any related issues
   - Include screenshots for UI changes
   - Ensure CI checks pass

3. **Address review feedback**:
   - Make requested changes
   - Push additional commits to your branch
   - Respond to comments

## Documentation

### Update Documentation

When making changes, update relevant documentation:

- **README.md** - For user-facing features
- **CHANGELOG.md** - For all notable changes
- **Code comments** - For complex logic
- **XML doc comments** - For public types and members

### Documentation Standards

- Use clear, concise language
- Include code examples where helpful
- Update version numbers in CHANGELOG.md
- Keep README.md up to date with features

## Project Structure

```
ComicMaintainer/
├── src/
│   ├── ComicMaintainer.Core/      # Domain logic, EF Core data layer, services
│   │   ├── Services/              # File processing, watcher, metadata, series
│   │   ├── Data/                  # DbContext and interceptors
│   │   ├── Models/                # Entities and DTOs
│   │   └── Migrations/            # EF Core migrations
│   ├── ComicMaintainer.WebApi/    # ASP.NET Core host (primary entry point)
│   │   ├── Controllers/           # REST endpoints
│   │   ├── Hubs/                  # SignalR hubs
│   │   ├── Middleware/            # Cross-cutting request handling
│   │   └── wwwroot/               # Static web UI (HTML, CSS, vanilla JS)
│   └── ComicMaintainer.MauiApp/   # .NET MAUI Android client (optional)
├── tests/
│   └── ComicMaintainer.Tests/     # xUnit test suite
├── docs/                          # Documentation (see docs/README.md for the index)
│   └── archive/                   # Superseded write-ups, kept for history
├── scripts/                       # Build, coverage and smoke-test helpers
├── .github/workflows/             # CI workflows
├── Dockerfile.dotnet              # Container definition
├── docker-compose.dotnet.yml      # Docker Compose config
└── README.md                      # Main documentation
```

> **Branching note:** the default and active branch is `dotnet`. Target your pull
> requests at `dotnet` — CI workflows are filtered to that branch and will not run
> against `main` or `master`.

## Need Help?

- **Issues**: Check existing issues or create a new one
- **Discussions**: Use GitHub Discussions for questions
- **Security**: See SECURITY.md for reporting vulnerabilities

## License

By contributing to ComicMaintainer, you agree that your contributions will be licensed under the MIT License.

---

Thank you for contributing to ComicMaintainer! 🎉
