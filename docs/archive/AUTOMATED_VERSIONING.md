# Automated Versioning

This document describes the automated version bumping system implemented in ComicMaintainer.

## Overview

Every merge to the `master` branch automatically triggers a version bump workflow that:
1. Increments the patch version (e.g., 1.0.0 → 1.0.1)
2. Updates `src/version.py` with the new version
3. Updates `CHANGELOG.md` with a new version entry
4. Commits the changes back to `master`
5. Creates a Git tag for the new version (e.g., `v1.0.1`)
6. Publishes a GitHub release
7. Docker images are automatically tagged with the new version

## How It Works

### Version Bump Workflow

The `.github/workflows/version-bump.yml` workflow:
- **Triggers**: On push to `master` branch
- **Skips**: When commit message contains `[skip-version-bump]` or starts with `Bump version to`
- **Increments**: Patch version (MAJOR.MINOR.PATCH → MAJOR.MINOR.PATCH+1)

### Docker Image Tagging

The `.github/workflows/docker-publish.yml` workflow:
- Builds Docker images on push to `master`
- Tags images with both:
  - `latest` - Always points to the most recent build
  - `<version>` - Specific version (e.g., `1.0.1`)

### Version Format

The version follows [Semantic Versioning](https://semver.org/) format:
- **Format**: `MAJOR.MINOR.PATCH`
- **Example**: `1.0.0`
- **Location**: `src/version.py`

```python
__version__ = "1.0.0"
```

## Usage Examples

### Pulling Docker Images

```bash
# Pull latest version
docker pull iceburn1/comictagger-watcher:latest

# Pull specific version
docker pull iceburn1/comictagger-watcher:1.0.1
```

### Checking Version

The version is exposed via the API:

```bash
curl http://localhost:5000/api/version
# Output: {"version": "1.0.1"}
```

It's also displayed in the web interface header.

## Manual Version Bumping

If you need to manually bump the version:

1. Edit `src/version.py`:
   ```python
   __version__ = "1.0.1"  # Change to desired version
   ```

2. Update `CHANGELOG.md` with the new version entry:
   ```markdown
   ## [1.0.1] - 2024-XX-XX
   
   ### Changed
   - Your changes here
   ```

3. Commit with `[skip-version-bump]` to prevent automatic bumping:
   ```bash
   git commit -m "Manual version bump to 1.0.1 [skip-version-bump]"
   ```

4. Create and push a tag:
   ```bash
   git tag -a v1.0.1 -m "Release version 1.0.1"
   git push origin v1.0.1
   ```

## Skipping Version Bump

To skip the automatic version bump for a specific merge:

1. Include `[skip-version-bump]` in your commit message
2. Or, name your commit starting with `Bump version to`

Example:
```bash
git commit -m "Update documentation [skip-version-bump]"
```

## Version History

All version changes are tracked in:
- `CHANGELOG.md` - Human-readable change log
- Git tags - Machine-readable version markers
- GitHub Releases - Release notes and artifacts

## Future Enhancements

Potential improvements to the versioning system:

- Support for manual major/minor version bumps via commit message tags
  - `[bump:major]` - Increment major version (1.0.0 → 2.0.0)
  - `[bump:minor]` - Increment minor version (1.0.0 → 1.1.0)
  - `[bump:patch]` - Increment patch version (1.0.0 → 1.0.1) (default)
- Pre-release versions (e.g., `1.0.0-beta.1`)
- Changelog generation from commit messages
- Automatic detection of breaking changes

## Testing

A test suite ensures version format validity:

```bash
# Run version tests
python test_version.py

# Or with pytest
pytest test_version.py -v
```

The test verifies:
- Version follows semantic versioning format (MAJOR.MINOR.PATCH)
- All components are non-negative integers
- Version is a string type

## Troubleshooting

### Workflow Not Triggering

**Problem**: Version bump workflow doesn't run after merge.

**Solutions**:
- Check if commit message contains `[skip-version-bump]`
- Verify GitHub Actions are enabled in repository settings
- Check workflow run history for errors

### Version Not Updated in Docker Image

**Problem**: Docker image still shows old version.

**Solutions**:
- Wait for both workflows to complete (version-bump, then docker-publish)
- Check GitHub Actions logs for errors
- Pull the specific version tag instead of `latest`:
  ```bash
  docker pull iceburn1/comictagger-watcher:1.0.1
  ```

### Merge Conflict in version.py

**Problem**: Multiple PRs merged simultaneously cause conflicts.

**Solutions**:
- The second merge will fail; rebase and merge again
- The version-bump workflow will handle the update after successful merge
- Git tags ensure no version is lost

## Architecture

```
Merge to master
    ↓
version-bump.yml triggers
    ↓
1. Extract current version from src/version.py
2. Increment patch version
3. Update src/version.py
4. Update CHANGELOG.md
5. Commit changes [skip-version-bump]
6. Push to master
7. Create Git tag (v1.0.1)
8. Create GitHub Release
    ↓
docker-publish.yml triggers
    ↓
1. Extract version from src/version.py
2. Build Docker image
3. Tag with 'latest' and '<version>'
4. Push to Docker Hub
```

## Security

- Workflows use `GITHUB_TOKEN` with minimal required permissions
- Version commits are signed by `github-actions[bot]`
- Git tags are annotated and signed
- No external dependencies required for versioning logic
