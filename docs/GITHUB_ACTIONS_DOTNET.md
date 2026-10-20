# GitHub Actions Workflow - .NET Docker Build

This document describes the GitHub Actions workflow for building and publishing the .NET version of ComicMaintainer to Docker Hub.

## Workflow File

`.github/workflows/docker-publish-dotnet.yml`

## Triggers

The workflow is triggered on:

- **Completion of the "Automatic Version Bump" workflow** (`workflow_run`) on the `dotnet` branch:
  - A merge to `dotnet` starts `.github/workflows/version-bump.yml`, which commits the next
    version to `ComicMaintainer.WebApi.csproj`, tags it and creates the GitHub release.
  - The image is built only after that run finishes, from the newest commit on `dotnet`, so it
    carries the version of that release.
  - It is built whatever the outcome of the bump, including when the bump was skipped with
    `[skip-version-bump]`.

- **Pull Request** to branch:
  - `dotnet`

- **Manual run** (`workflow_dispatch`) - republishes the newest commit of the selected branch.
  Run it on `dotnet`.

The image is deliberately not built on the push to `dotnet`. That push is the merge commit, which
still holds the previous version, and the bump commit cannot start a build of its own: commits
pushed by a workflow with `GITHUB_TOKEN` do not trigger other workflows. Building on push
therefore published every image one version behind the newest release.

Only one publish runs at a time. A newer one cancels an older one that is still in progress, so
the `dotnet` tag cannot be overwritten by a stale image that happened to finish last.

## Docker Image Tags

The workflow creates a Docker image with a single tag:

### .NET Branch
When PRs are merged to the `dotnet` branch, the following tag is created:
- `iceburn1/comicmaintainer:dotnet`

This uses the same Docker Hub repository as the Python version (`iceburn1/comicmaintainer`) but with the `dotnet` tag to distinguish it from the Python version (which uses `latest`, `stable`, etc.)

## Workflow Steps

1. **Checkout code** - Retrieves the repository code
2. **Set up .NET** - Installs .NET 10.0 SDK
3. **Get version** - Extracts version from `ComicMaintainer.WebApi.csproj`
4. **Determine Docker tags** - Calculates appropriate tags based on branch
5. **Cache Docker layers** - Caches build layers for faster subsequent builds
6. **Set up Docker Buildx** - Configures Docker for multi-platform builds
7. **Log in to Docker Hub** - Authenticates with Docker Hub (not for pull requests)
8. **Build and push** - Builds the Docker image using `Dockerfile.dotnet` and pushes to Docker Hub

## Required Secrets

The workflow requires the following secrets to be configured in the GitHub repository:

- `DOCKER_USERNAME` - Docker Hub username
- `DOCKER_PASSWORD` - Docker Hub password or access token

### Setting up Secrets

1. Go to your GitHub repository
2. Navigate to Settings → Secrets and variables → Actions
3. Click "New repository secret"
4. Add the following secrets:
   - Name: `DOCKER_USERNAME`, Value: Your Docker Hub username
   - Name: `DOCKER_PASSWORD`, Value: Your Docker Hub password or access token

**Note**: It's recommended to use a Docker Hub access token instead of your password for better security.

## Version Management

The version is defined in the `src/ComicMaintainer.WebApi/ComicMaintainer.WebApi.csproj` file:

```xml
<PropertyGroup>
  <Version>1.0.0</Version>
  <AssemblyVersion>1.0.0.0</AssemblyVersion>
  <FileVersion>1.0.0.0</FileVersion>
</PropertyGroup>
```

The patch version is bumped automatically on every merge to `dotnet`, and the image is published
afterwards with the new version (see [Triggers](#triggers)).

To set a version by hand:
1. Edit the `<Version>`, `<AssemblyVersion>` and `<FileVersion>` tags in the .csproj file
2. Commit with `[skip-version-bump]` in the message so the automatic bump leaves it alone, and push
3. The workflow builds the image with the new version once the skipped version bump run completes

## Build Artifacts

The workflow produces Docker images for the `linux/amd64` platform. The images are:

- Built from `Dockerfile.dotnet`
- Include all dependencies and the compiled .NET application
- Support dynamic PUID/PGID configuration
- Include security hardening (non-root user, path validation, etc.)

## Pull Request Behavior

When a pull request is created:
- The Docker image is **built** but **not pushed** to Docker Hub
- This allows validation of the Docker build process without publishing
- Once the PR is merged and the version bump has finished, the image will be built and pushed

## Monitoring Builds

To monitor build status:
1. Go to your GitHub repository
2. Click on the "Actions" tab
3. Select "Build and Push .NET Docker Image" workflow
4. View individual workflow runs and their logs

## Troubleshooting

### Build Fails
- Check the workflow logs in the Actions tab
- Verify .NET 10.0 SDK is available
- Ensure `Dockerfile.dotnet` is present and valid

### Push Fails
- Verify Docker Hub credentials are correct in repository secrets
- Check Docker Hub rate limits
- Ensure you have permission to push to the repository

### Version Not Updated
- The image is published a few minutes after the merge, once the "Automatic Version Bump" run
  has finished. Check that a "Build and Push .NET Docker Image" run started after it and
  succeeded, then pull the image again
- If no run started, run the workflow manually on `dotnet` (Actions → "Build and Push .NET
  Docker Image" → Run workflow)
- Verify the version in `ComicMaintainer.WebApi.csproj` is correct
- Check that the grep command in the workflow successfully extracts the version

## Usage

Once secrets are configured and PRs are merged to the `dotnet` branch, the workflow runs automatically after each version bump. To use the published image:

```bash
# Pull the .NET version
docker pull iceburn1/comicmaintainer:dotnet

# Run the container
docker run -e PUID=$(id -u) -e PGID=$(id -g) \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  iceburn1/comicmaintainer:dotnet
```

## Local Testing

To test the Docker build locally before pushing:

```bash
# Build the image
docker build -f Dockerfile.dotnet -t comicmaintainer:dotnet-test .

# Run the image
docker run -e PUID=$(id -u) -e PGID=$(id -g) \
  -v $(pwd)/test_comics:/watched_dir \
  -v $(pwd)/config:/Config \
  comicmaintainer:dotnet-test
```

## Related Documentation

- [Docker Deployment Guide](DOCKER_DEPLOYMENT_DOTNET.md)
- [Security Fixes](archive/SECURITY_FIXES_DOTNET.md)
- [.NET Implementation Summary](archive/DOTNET_IMPLEMENTATION_SUMMARY.md)
