# ComicMaintainer Tests

This directory contains unit tests for the ComicMaintainer project.

## Test Structure

```
tests/
├── ComicMaintainer.Tests/
│   ├── Controllers/        # API Controller tests
│   ├── Services/          # Service layer tests
│   ├── Utilities/         # Utility class tests
│   └── xunit.runner.json  # xUnit configuration
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

# Or manually:
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

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
- Push to `main` or `develop` branches
- Pull requests targeting `main` or `develop`
- Manual workflow dispatch

See `.github/workflows/test-dotnet.yml` for CI configuration.

## Test Categories

### Unit Tests
- Test individual components in isolation
- Use mocks for dependencies
- Fast execution

### Integration Tests (Future)
- Test component interactions
- Use real or test databases
- Slower execution

## Best Practices

1. **Keep tests independent**: Each test should be able to run independently
2. **Use meaningful names**: Test names should describe what they test
3. **Follow AAA pattern**: Arrange, Act, Assert
4. **One assertion per test**: Focus on testing one thing
5. **Clean up resources**: Dispose of resources in test teardown
6. **Mock external dependencies**: Keep tests fast and reliable

## Current Test Coverage

As of the last update:
- **Total Tests**: 146
- **Controllers**: 30 tests (WatcherController: 6, JobsController: 3, FilesController: 21)
- **Services**: 72 tests (ComicProcessor: 12, ComicArchive: 8, FileWatcher: 9, ComicFileProcessor: 32, Integration: 8)
- **Utilities**: 31 tests (ComicFileExtensions: 18, LoggingHelper: 11)
- **Models**: 11 tests (ComicInfo, ComicMetadata, ComicFile, ProcessingJob)
- **Code Coverage**: 27.95%

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
