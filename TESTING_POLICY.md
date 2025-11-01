# Testing Policy

## Overview
This document outlines the testing requirements for all code changes in the ComicMaintainer project. **All bug fixes and new features MUST include appropriate tests before being merged.**

## Testing Requirements

### Mandatory Test Coverage
Every pull request that includes code changes MUST include:

1. **Unit Tests** - For all new functions, methods, and logic changes
2. **Integration Tests** - For features that interact with multiple components or external systems
3. **Regression Tests** - For bug fixes to ensure the issue doesn't reoccur

### When Tests Are Required

#### ✅ Tests ARE Required For:
- **Bug Fixes**: Tests that reproduce the bug and verify the fix
- **New Features**: Comprehensive unit and integration tests
- **API Changes**: Tests for all new/modified endpoints
- **Business Logic**: Tests for all new/modified business rules
- **Data Processing**: Tests for file processing, transformations, etc.
- **Security Fixes**: Tests that verify the vulnerability is fixed

#### ⚠️ Tests MAY Be Waived For:
- **Documentation-only changes** (README, docs, comments)
- **Configuration changes** that don't affect code logic
- **Trivial formatting/style changes** (with reviewer approval)

Note: Even for documentation changes, if they involve code examples, those examples should be tested.

## Test Categories

### 1. Unit Tests
**Purpose**: Test individual components in isolation

**Guidelines**:
- Test one component/method at a time
- Mock external dependencies
- Test both success and failure scenarios
- Test edge cases and boundary conditions
- Use clear, descriptive test names

**Example**:
```csharp
[Theory]
[InlineData("Batman_The Dark Knight", "Batman:The Dark Knight", false)]
[InlineData("Spider_Man", "Spider:Man", false)]
public void NormalizeSeriesName_WithUnderscores_ReplacesWithColons(
    string input, string expected, bool forComparison)
{
    // Act
    var result = ComicFileProcessor.NormalizeSeriesName(input, forComparison);

    // Assert
    Assert.Equal(expected, result);
}
```

### 2. Integration Tests
**Purpose**: Test interactions between components

**Guidelines**:
- Test real component interactions
- Test with realistic data
- Test full workflows/scenarios
- May use test databases or file systems
- Clean up test artifacts after execution

**Example**:
```csharp
[Fact]
public async Task ProcessFile_WithValidComicFile_UpdatesMetadataAndRenames()
{
    // Arrange
    var testFile = CreateTestComicFile();
    
    // Act
    var result = await _processor.ProcessFileAsync(testFile);
    
    // Assert
    Assert.True(result.Success);
    Assert.True(File.Exists(result.NewPath));
    // ... additional assertions
}
```

### 3. Regression Tests
**Purpose**: Ensure bugs don't reoccur

**Guidelines**:
- Create a test that reproduces the original bug
- Verify the test fails before the fix
- Verify the test passes after the fix
- Include the issue number in the test name/comments

**Example**:
```csharp
[Fact]
public void ProcessFile_Issue123_HandlesDecimalChapterNumbers()
{
    // Regression test for Issue #123
    // Chapter numbers like "71.4" should be preserved
    var result = ComicFileProcessor.ParseChapterNumber("Chapter 71.4");
    Assert.Equal("71.4", result);
}
```

## Test Quality Standards

### Test Naming Convention
- Use descriptive names that explain what is being tested
- Format: `MethodName_Scenario_ExpectedBehavior`
- Examples:
  - `NormalizeSeriesName_WithUnderscores_ReplacesWithColons`
  - `ProcessFile_WhenFileNotFound_ThrowsException`
  - `FormatFilename_WithDecimalIssue_PreservesDecimals`

### Test Organization
- Place tests in the `tests/ComicMaintainer.Tests` directory
- Mirror the source code structure
- Group related tests in the same test class
- Use nested classes for logical grouping when needed

### Code Coverage
- Aim for **minimum 80% code coverage** for new code
- Critical paths and business logic should have 100% coverage
- Coverage reports are automatically generated in CI/CD
- Pull requests show coverage changes in comments

### Test Data
- Use inline data for simple test cases (`[InlineData]`)
- Use `[Theory]` for parameterized tests
- Create helper methods for complex test data setup
- Clean up test data/files in `Dispose()` or teardown methods

## Running Tests

### Local Testing
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter "FullyQualifiedName~ComicFileProcessorTests"

# Run tests with verbose output
dotnet test --verbosity normal
```

### CI/CD Testing
- Tests run automatically on every push and pull request
- Test results appear in the GitHub Actions workflow
- Coverage reports are posted as PR comments
- Failed tests block PR merging

## Pull Request Test Requirements

### Before Submitting a PR
1. ✅ Write tests for all new/changed code
2. ✅ Run tests locally and verify they pass
3. ✅ Check code coverage locally
4. ✅ Update existing tests if behavior changed
5. ✅ Document what tests were added in the PR description

### PR Review Checklist
Reviewers should verify:
- [ ] Tests are included for all code changes
- [ ] Tests are meaningful and test the right things
- [ ] Tests cover edge cases and error scenarios
- [ ] Test names are clear and descriptive
- [ ] Code coverage hasn't decreased
- [ ] All tests pass in CI/CD

### Handling Test Failures
If tests fail in CI:
1. Review the test output and error messages
2. Fix the issue locally and verify tests pass
3. Push the fix and verify CI tests pass
4. Do not merge PRs with failing tests

## Test Automation

### GitHub Actions Workflows
The project uses GitHub Actions for automated testing:

**Test Workflow** (`.github/workflows/test-dotnet.yml`):
- Runs on every push and PR
- Builds the project
- Runs all tests
- Generates coverage reports
- Posts coverage to PR comments

**PR Test Check Workflow** (`.github/workflows/pr-test-check.yml`):
- Verifies that test files were modified when source files change
- Helps ensure tests are not forgotten
- Can be overridden for documentation-only changes

## Testing Best Practices

### Do's ✅
- Write tests before or alongside code (TDD when possible)
- Test one thing per test method
- Use descriptive test and variable names
- Test both happy path and error cases
- Keep tests fast and independent
- Use mocks/stubs for external dependencies
- Clean up test resources (files, DB connections, etc.)
- Update tests when requirements change

### Don'ts ❌
- Don't skip tests because "it's a small change"
- Don't test implementation details, test behavior
- Don't write flaky tests (tests that randomly fail)
- Don't have tests that depend on execution order
- Don't leave commented-out test code
- Don't commit failing tests (use `Skip` attribute if needed)
- Don't test framework/library code

## Examples and Resources

### Testing Framework
- **xUnit** - Primary testing framework
- **Moq** - Mocking library for unit tests
- **xUnit Documentation**: https://xunit.net/
- **Moq Documentation**: https://github.com/moq/moq4

### Example Test Files
See these files for testing patterns:
- `tests/ComicMaintainer.Tests/Services/ComicFileProcessorTests.cs` - Unit test examples
- `tests/ComicMaintainer.Tests/Services/ComicFileProcessorIntegrationTests.cs` - Integration test examples
- `tests/ComicMaintainer.Tests/Controllers/FilesControllerTests.cs` - Controller test examples

## Getting Help

### Questions About Testing?
- Check existing test files for examples
- Review this policy document
- Ask in pull request comments
- Discuss in GitHub Issues

### Reporting Issues
If you find gaps in test coverage or testing infrastructure issues:
1. Create a GitHub Issue
2. Label it with `testing` and `enhancement`
3. Describe the problem and proposed solution

## Policy Enforcement

### Automated Checks
- CI/CD will fail if tests fail
- Coverage reports show if coverage decreased
- PR checks verify test files are included

### Manual Review
- Reviewers will verify test quality
- PRs without adequate tests will be sent back for revision
- Exceptions require explicit approval and justification

### Consequences
- PRs without tests will not be merged
- Repeated violations may result in PR privileges being restricted
- Critical security fixes may be merged with tests added separately (rare exception)

## Version History

- **v1.0** (2025-11-01): Initial testing policy created

---

**Remember**: Tests are not optional. They are a critical part of maintaining code quality and preventing regressions. Taking time to write good tests saves time in the long run by catching bugs early and making refactoring safer.
