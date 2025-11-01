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

- Python 3.11+
- Docker (for container testing)
- Git

### Install Development Dependencies

```bash
pip install -r requirements.txt
pip install -r requirements-dev.txt
```

### Create Test Environment

```bash
# Create /Config directory for tests
sudo mkdir -p /Config
sudo chmod 777 /Config
```

### Run the Application Locally

```bash
# Set required environment variables
export WATCHED_DIR=/path/to/test/comics
export WEB_PORT=5000

# Start the web application
python src/web_app.py
```

## Making Changes

### Code Style

- Follow PEP 8 guidelines
- Use meaningful variable and function names
- Keep functions focused and small
- Add docstrings to functions and classes
- Use type hints where appropriate

### Commit Messages

Write clear, descriptive commit messages:

```
Add health check endpoint for container orchestration

- Added /health and /api/health endpoints
- Returns status of watched directory, database, and watcher
- Returns 200 OK for healthy, 503 for unhealthy
```

## Testing

⚠️ **IMPORTANT**: All bug fixes and new features **MUST** include tests. See [TESTING_POLICY.md](TESTING_POLICY.md) for detailed requirements.

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

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run tests with verbose output
dotnet test --verbosity normal
```

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

For detailed testing guidelines, see [TESTING_POLICY.md](TESTING_POLICY.md).

## Code Quality

### Run Linters

```bash
# Pylint
pylint src/*.py

# Flake8
flake8 src/
```

### Security Scanning

```bash
# Bandit - Python security scanner
bandit -r src/ -c .bandit

# pip-audit - Check dependencies for vulnerabilities
pip-audit -r requirements.txt
```

### Pre-commit Checks

Before committing, ensure:
- [ ] **All tests pass** (`dotnet test`)
- [ ] **New tests added** for all code changes (see [TESTING_POLICY.md](TESTING_POLICY.md))
- [ ] **Code coverage hasn't decreased**
- [ ] No linter errors
- [ ] Security scans pass
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
- **Docstrings** - For functions and classes

### Documentation Standards

- Use clear, concise language
- Include code examples where helpful
- Update version numbers in CHANGELOG.md
- Keep README.md up to date with features

## Project Structure

```
ComicMaintainer/
├── src/                    # Python source code
│   ├── web_app.py         # Main Flask application
│   ├── watcher.py         # File watcher service
│   ├── process_file.py    # File processing logic
│   └── ...                # Other modules
├── templates/             # HTML templates
├── docs/                  # Documentation
│   ├── archive/          # Historical documentation
│   └── ...               # Current documentation
├── tests/                 # Test files (in root)
├── .github/               # GitHub workflows
├── Dockerfile             # Container definition
├── docker-compose.yml     # Docker Compose config
├── requirements.txt       # Python dependencies
├── requirements-dev.txt   # Development dependencies
└── README.md             # Main documentation
```

## Need Help?

- **Issues**: Check existing issues or create a new one
- **Discussions**: Use GitHub Discussions for questions
- **Security**: See SECURITY.md for reporting vulnerabilities

## License

By contributing to ComicMaintainer, you agree that your contributions will be licensed under the MIT License.

---

Thank you for contributing to ComicMaintainer! 🎉
