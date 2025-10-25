# Test Branch Setup

This document describes the test branch configuration for Docker builds.

## Overview

A `test` branch has been configured to build Docker images tagged as `test`. This branch was created from the end of PR #338, which implemented automatic version bumping on merge to master.

## Branch Details

- **Branch name**: `test`
- **Created from**: Commit `57821b3fb90a52afb44652c4ca62b0b39f89fe07`
- **PR reference**: End of PR #338 (Implement automatic version bumping on merge to master)

## Creating the Test Branch

To create the test branch, run the provided script:

```bash
./create-test-branch.sh
```

Or manually:

```bash
git checkout -b test 57821b3fb90a52afb44652c4ca62b0b39f89fe07
git push -u origin test
```

## Docker Build Configuration

The Docker build workflow (`.github/workflows/docker-publish.yml`) has been updated to:

1. **Build on push to test branch**: The workflow now triggers on pushes to `master`, `stable`, and `test` branches
2. **Tag as test**: Docker images built from the `test` branch are tagged as `iceburn1/comictagger-watcher:test`

### Docker Tags by Branch

| Branch | Docker Tag(s) |
|--------|---------------|
| master | `latest`, `<version>` (e.g., `1.0.24`) |
| stable | `stable` |
| test   | `test` |

## Usage

Once the test branch is pushed and the workflow runs, you can pull the test image:

```bash
docker pull iceburn1/comictagger-watcher:test
```

Or use it in docker-compose.yml:

```yaml
services:
  comictagger-watcher:
    image: iceburn1/comictagger-watcher:test
    # ... rest of configuration
```

## What's Included in the Test Branch

The test branch contains all changes up to and including PR #338:

- Automatic version bumping workflow
- Enhanced Docker image tagging with version numbers
- Version test suite
- Comprehensive documentation for automated versioning

This provides a stable testing point with the version bumping feature included.
