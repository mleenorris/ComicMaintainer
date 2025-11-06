# CodeQL Configuration

This document explains the CodeQL security scanning configuration for the ComicMaintainer repository.

## Overview

The repository uses branch-aware CodeQL analysis to scan for security vulnerabilities in the appropriate languages for each branch:

- **dotnet branch**: Analyzes C# and JavaScript code (for .NET projects)
- **master and python branches**: Analyzes Python and JavaScript code

## Workflow Configuration

The CodeQL workflow is defined in `.github/workflows/codeql-analysis.yml` and consists of two separate jobs:

### 1. analyze-dotnet Job

**Triggers**: Runs when the branch is `dotnet` or when a PR targets `dotnet`

**Languages Analyzed**:
- **C#**: For .NET Core/ASP.NET Core code
- **JavaScript**: For frontend and Node.js code

**Build Process**:
- Sets up .NET 9.0.x
- Restores dependencies for Core, WebApi, and Tests projects (excluding MAUI)
- Builds projects in Release configuration

**Path Exclusions**:
- `**/node_modules` - Node.js dependencies
- `**/wwwroot/lib` - Frontend libraries
- `**/bin`, `**/obj` - Build artifacts
- `**/coveragereport` - Test coverage reports
- `**/*.min.js`, `**/*.min.css` - Minified files

### 2. analyze-python Job

**Triggers**: Runs when the branch is `master` or `python`, or when a PR targets these branches

**Languages Analyzed**:
- **Python**: For Python application code
- **JavaScript**: For frontend code

**Setup Process**:
- Sets up Python 3.11
- Installs dependencies from requirements.txt
- Uses pip caching for faster builds

**Path Exclusions**:
- `**/node_modules` - Node.js dependencies
- `**/__pycache__` - Python bytecode cache
- `**/*.pyc` - Python compiled files
- `**/venv`, `**/.venv` - Python virtual environments
- `**/*.min.js`, `**/*.min.css` - Minified files

## Triggers

The workflow runs on:

1. **Push events**: When code is pushed to master, main, dotnet, or python branches
2. **Pull request events**: When PRs target master, main, dotnet, or python branches
3. **Scheduled scans**: Weekly on Wednesdays at 3:00 AM UTC (cron: `0 3 * * 3`)
4. **Manual triggers**: Can be triggered manually via workflow_dispatch

## Query Suite

Both jobs use the `security-and-quality` query suite, which includes:
- Security vulnerability detection
- Code quality checks
- Best practice violations
- Common coding errors

## Timeouts

- **Job timeout**: 60 minutes
- **Analysis timeout**: 30 minutes per language

## Results

- Results are uploaded to GitHub Security tab
- Analysis summaries are stored as artifacts for 90 days
- Separate artifact names distinguish between branches:
  - `codeql-summary-dotnet-{language}` for dotnet branch
  - `codeql-summary-python-{language}` for master/python branches

## Benefits of Branch-Aware Configuration

1. **Accuracy**: Analyzes only the languages present in each branch
2. **Performance**: Avoids trying to analyze non-existent code
3. **Clarity**: Clear separation of analysis results by branch/language
4. **Maintainability**: Single workflow file manages all branches

## Viewing Results

1. Navigate to the repository's **Security** tab
2. Click on **Code scanning alerts**
3. Filter by branch, language, or severity as needed
4. Click on individual alerts for detailed information and remediation guidance

## Maintenance

When adding new branches with different languages:
1. Add a new job in the workflow file
2. Configure appropriate language matrix
3. Add conditional logic based on branch name
4. Update this documentation
