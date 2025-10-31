# GitHub Actions Workflow - .NET Docker Build

This document describes the GitHub Actions workflow for building and publishing the .NET version of ComicMaintainer to Docker Hub.

## Workflow File

`.github/workflows/docker-publish-dotnet.yml`

## Triggers

The workflow is triggered on:

- **Push** to branches:
  - `master` - Main development branch
  - `stable` - Stable release branch
  - `copilot/implement-next-steps-dotnet` - .NET development branch

- **Pull Request** to branches:
  - `master`
  - `copilot/implement-next-steps-dotnet`

## Docker Image Tags

The workflow creates Docker images with different tags based on the branch:

### Master Branch
When code is pushed to `master`, the following tags are created:
- `iceburn1/comicmaintainer-dotnet:latest`
- `iceburn1/comicmaintainer-dotnet:<version>` (e.g., `1.0.0`)
- `iceburn1/comicmaintainer-dotnet:dotnet-latest`

### Stable Branch
When code is pushed to `stable`, the following tag is created:
- `iceburn1/comicmaintainer-dotnet:stable`

### Development Branch (copilot/implement-next-steps-dotnet)
When code is pushed to the .NET development branch, the following tags are created:
- `iceburn1/comicmaintainer-dotnet:dotnet-dev`
- `iceburn1/comicmaintainer-dotnet:dotnet-<version>` (e.g., `dotnet-1.0.0`)

## Workflow Steps

1. **Checkout code** - Retrieves the repository code
2. **Set up .NET** - Installs .NET 9.0 SDK
3. **Get version** - Extracts version from `ComicMaintainer.WebApi.csproj`
4. **Determine Docker tags** - Calculates appropriate tags based on branch
5. **Cache Docker layers** - Caches build layers for faster subsequent builds
6. **Set up Docker Buildx** - Configures Docker for multi-platform builds
7. **Log in to Docker Hub** - Authenticates with Docker Hub (only on push, not PRs)
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

To update the version:
1. Edit the `<Version>` tag in the .csproj file
2. Commit and push the change
3. The workflow will automatically build with the new version tag

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
- Once the PR is merged, the image will be built and pushed

## Monitoring Builds

To monitor build status:
1. Go to your GitHub repository
2. Click on the "Actions" tab
3. Select "Build and Push .NET Docker Image" workflow
4. View individual workflow runs and their logs

## Troubleshooting

### Build Fails
- Check the workflow logs in the Actions tab
- Verify .NET 9.0 SDK is available
- Ensure `Dockerfile.dotnet` is present and valid

### Push Fails
- Verify Docker Hub credentials are correct in repository secrets
- Check Docker Hub rate limits
- Ensure you have permission to push to the repository

### Version Not Updated
- Verify the version in `ComicMaintainer.WebApi.csproj` is correct
- Check that the grep command in the workflow successfully extracts the version

## Local Testing

To test the Docker build locally before pushing:

```bash
# Build the image
docker build -f Dockerfile.dotnet -t comicmaintainer-dotnet:test .

# Run the image
docker run -e PUID=$(id -u) -e PGID=$(id -g) \
  -v $(pwd)/test_comics:/watched_dir \
  -v $(pwd)/config:/Config \
  comicmaintainer-dotnet:test
```

## Related Documentation

- [Docker Deployment Guide](DOCKER_DEPLOYMENT_DOTNET.md)
- [Security Fixes](SECURITY_FIXES_DOTNET.md)
- [.NET Implementation Summary](DOTNET_IMPLEMENTATION_SUMMARY.md)
