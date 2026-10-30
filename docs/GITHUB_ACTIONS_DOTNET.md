# GitHub Actions Workflow - .NET Docker Build

This document describes the GitHub Actions workflow for building and publishing the .NET version of ComicMaintainer to Docker Hub.

## Workflow File

`.github/workflows/docker-publish-dotnet.yml`

## Triggers

The workflow is triggered on:

- **Completion of the "Automatic Version Bump" workflow** (`workflow_run`) on the `dotnet` branch:
  - A merge to `dotnet` starts `.github/workflows/version-bump.yml`, which commits the next
    version to `ComicMaintainer.WebApi.csproj`, tags it and creates the GitHub release.
  - The image is built only after that run finishes, from the commit the bump tagged as the
    release, so it carries the version of that release. The bump records that commit in a
    `release-commit` artifact, because `dotnet` already holds a later commit when another merge
    lands before the bump finishes.
  - It is built whatever the outcome of the bump. When the bump recorded no release, for example
    because it was skipped with `[skip-version-bump]`, the newest commit on `dotnet` is built.

- **Pull Request** to branch:
  - `dotnet`

- **Manual run** (`workflow_dispatch`), run on `dotnet`:
  - With **version** empty, it republishes the newest commit of the selected branch, e.g. after a
    failed push to Docker Hub.
  - With **version** set, it rebuilds that release and pushes only its version tag. See
    [Backfilling a version tag](#backfilling-a-version-tag).

The image is deliberately not built on the push to `dotnet`. That push is the merge commit, which
still holds the previous version, and the bump commit cannot start a build of its own: commits
pushed by a workflow with `GITHUB_TOKEN` do not trigger other workflows. Building on push
therefore published every image one version behind the newest release.

Only one publish runs at a time. A publish triggered while another is running waits for it
instead of cancelling it, because a cancelled publish would never push its release's version
tag. Only one publish can wait, and a newer one replaces it, so the newest build is always the
last to push `dotnet`. When several releases are merged while a publish is running, the ones in
between are not published; backfill them if you need their version tags. Backfills and pull
request builds never wait for, or hold up, a publish.

## Docker Image Tags

Images are pushed to `iceburn1/comicmaintainer` on Docker Hub with these tags:

| Tag | Image | Moves |
|-----|-------|-------|
| `dotnet` | The newest build of the `dotnet` branch | On every publish, unless `dotnet` has moved past the release being published |
| `X.Y.Z`, e.g. `2.0.404` | That release | Never, except when the release is republished or backfilled |

The version tag is the three-part `<Version>` from `ComicMaintainer.WebApi.csproj`. The web
interface shows the four-part assembly version, so `2.0.404.0` there is the tag `2.0.404`.

A version tag is pushed only when the commit being built is the one the version bump tagged
`vX.Y.Z`. A later commit, such as a merge whose bump was skipped with `[skip-version-bump]`,
still carries the previous version number, so its build updates `dotnet` only and the run shows
the notice "This commit is not the vX.Y.Z release, so only :dotnet is published". This keeps
each version tag on the code of its release.

A release is built from the commit its version bump tagged even when more commits have reached
`dotnet` since, for example because another pull request was merged while the bump was
finishing. Its build then pushes only the version tag and shows the notice "dotnet has moved on
since vX.Y.Z, so only :X.Y.Z is published". The build of the newer commits moves `dotnet`
instead, so `dotnet` never goes back to an older release, even when an old build is re-run.

Version tags started with the release that added them. Earlier releases have no tag until they
are backfilled.

### Backfilling a version tag

To publish the tag of an earlier release:

1. Go to Actions → "Build and Push .NET Docker Image" → Run workflow
2. Leave the branch on `dotnet` and enter the release in **version**, such as `2.0.399` (not
   `v2.0.399` or `2.0.399.0`)

The run checks out the release's `v2.0.399` git tag, checks that its `.csproj` holds that version,
and builds and pushes `iceburn1/comicmaintainer:2.0.399` only. It never touches `dotnet`.

- The image is rebuilt from the release's source with today's base images, so it is not
  byte-for-byte the image that was published at the time. Backfilling a version that already
  has a tag replaces it with such a rebuild.
- Releases before 2.0.288 were published for `linux/amd64` only and are rebuilt for that
  platform only.

To tag the exact image that was published instead, copy its digest from the release's publish
run (the "Image digest" step and the run summary list it; older runs show it in the "Build and
push" step's log, and GitHub keeps run logs for 90 days by default), then, with push access to
the repository:

```bash
docker buildx imagetools create -t iceburn1/comicmaintainer:2.0.399 \
  iceburn1/comicmaintainer@sha256:<digest>
```

## Workflow Steps

1. **Validate requested version** - Manual backfills only: checks that **version** looks like `2.0.399`
2. **Download release record** - After a version bump only: downloads the bump's
   `release-commit` artifact, which holds the commit it tagged. A bump that recorded no release
   leaves none
3. **Read release record** - Checks that the record holds a commit hash
4. **Checkout code** - Retrieves the release's `vX.Y.Z` tag for a backfill, the recorded release
   commit after a version bump, and otherwise the newest commit of the branch
5. **Set up .NET** - Installs .NET 10.0 SDK
6. **Get version** - Reads `<Version>` from `ComicMaintainer.WebApi.csproj`. Fails if it is
   missing or malformed, or for a backfill, if it is not the requested version
7. **Determine Docker tags and platforms** - `dotnet`, plus `X.Y.Z` if the commit is that
   release's tagged commit. Only `X.Y.Z` for a backfill, or for a release that `dotnet` has moved
   past. Fails if a recorded release commit is not tagged with its version
8. **Cache Docker layers** - Caches build layers for faster subsequent builds
9. **Set up QEMU** - Lets the runner execute the arm64 image's package installation
10. **Set up Docker Buildx** - Configures Docker for multi-platform builds
11. **Log in to Docker Hub** - Authenticates with Docker Hub (not for pull requests)
12. **Build and push** - Builds the Docker image using `Dockerfile.dotnet` and pushes to Docker Hub
13. **Image digest** - Lists the pushed digest and tags in the run summary

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

The workflow produces Docker images for the `linux/amd64` and `linux/arm64` platforms (`linux/arm64`
since 2.0.288). The images are:

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
- A run that shows "dotnet has moved on since vX.Y.Z" published only that release's version tag.
  The run for the newer commit moves `dotnet`
- Verify the version in `ComicMaintainer.WebApi.csproj` is correct
- Check that the grep command in the workflow successfully extracts the version

### Version Tag Missing
- The run shows "This commit is not the vX.Y.Z release, so only :dotnet is published" when it
  built a commit after the release, for example because the bump was skipped. That is expected
- Check that the "Automatic Version Bump" run succeeded and pushed the `vX.Y.Z` git tag
- When several releases are merged within a few minutes, a publish that is still waiting to
  start is replaced by the next one, so that release gets no tag. Either way,
  [backfill](#backfilling-a-version-tag) the tag

## Usage

Once secrets are configured and PRs are merged to the `dotnet` branch, the workflow runs automatically after each version bump. To use the published image:

```bash
# Pull the newest release
docker pull iceburn1/comicmaintainer:dotnet

# Or pin a specific release, e.g. to roll back
docker pull iceburn1/comicmaintainer:2.0.404

# Run the container
docker run -e PUID=$(id -u) -e PGID=$(id -g) \
  -v /path/to/comics:/watched_dir \
  -v /path/to/config:/Config \
  iceburn1/comicmaintainer:dotnet
```

See [Pin or Roll Back to a Version](DOCKER_DEPLOYMENT_DOTNET.md#pin-or-roll-back-to-a-version)
before running an older release against an existing `/Config`.

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
