# GitHub Actions

How the repository is tested and released. The workflows are in `.github/workflows/`.

## Overview

| Workflow | File | Runs on |
| --- | --- | --- |
| Release | `version-bump.yml` | Every push to `dotnet` |
| .NET Tests | `test-dotnet.yml` | Pull requests into `dotnet`, by hand, and called by Release |
| Integration Tests (E2E Container) | `integration-tests-e2e.yml` | Pull requests into `dotnet`, by hand, and called by Release |
| Build and Push .NET Docker Image | `docker-publish-dotnet.yml` | Called by Release, and by hand |
| Build Windows Release | `build-windows-release.yml` | Called by Release, and by hand |
| Build Android APK | `build-android-apk.yml` | Called by Release, and by hand |
| PR Test Check | `pr-test-check.yml` | Pull requests into `dotnet` |
| Security Vulnerability Scanning | `security-scan.yml` | Pull requests, pushes to `dotnet`, weekly and by hand |
| CodeQL Advanced Security Scanning | `codeql-analysis.yml` | Pull requests, pushes to `dotnet`, weekly and by hand |
| Claude Code, Claude Code Review | `claude.yml`, `claude-code-review.yml` | `@claude` mentions; pull request updates |
| Cleanup Copilot Branches | `cleanup-copilot-branches.yml` | Pull request closed; deletes `copilot/` branches |

[Dependabot](../.github/dependabot.yml) proposes dependency updates.

## Pull requests

A pull request into `dotnet` runs these. See [TESTING_POLICY.md](TESTING_POLICY.md) for what each one requires.

- **.NET Tests**: `unit-tests` runs the JavaScript tests and the .NET unit tests on Linux with the coverage gates, and
  builds `src/` with warnings as errors. `windows-tests` runs the whole suite on Windows, `file-operation-tests` runs
  the delete-job tests on macOS, and `integration-tests` runs `Category=Integration`.
- **Integration Tests (E2E Container)**: builds the `linux/amd64` image, runs `scripts/container-smoke-test.sh`, and scans
  the image with Trivy. A fixable critical or high vulnerability fails the check.
- **PR Test Check**: fails when server or web UI code changed without a change under `tests/`.
- **Security scanning and CodeQL**: see [Security scans](#security-scans).

## Release

A push to `dotnet` starts one Release run. Only a commit that passed its tests is released.

1. `tests` and `container-tests` run `test-dotnet.yml` and `integration-tests-e2e.yml` for the pushed commit.
2. `bump-version` checks the commit is still the newest on `dotnet`. It raises the patch number of `<Version>`,
   `<AssemblyVersion>` and `<FileVersion>` in `ComicMaintainer.WebApi.csproj`, and pushes the bump commit and the
   `vX.Y.Z` tag together. It then creates a GitHub release with generated notes.
3. `publish-image` calls `docker-publish-dotnet.yml` with the release commit, or the tested commit when no version was
   released.
4. `windows-release` and `android-release` call their workflows with the tag, and attach the downloads to the release.

Workflows are called directly because a tag pushed with `GITHUB_TOKEN` cannot start another workflow.

Releases run one at a time, in push order. A newer push waits instead of cancelling a running release, and replaces any
older push that is still waiting. If `dotnet` has moved past the tested commit, that run releases nothing, and the run
for the newer push releases both.

A commit message containing `[skip-version-bump]` or `Bump version to` gets no new version.
It is still tested and published as `:dotnet`.

To set a version by hand, such as for a major release, edit those three properties, commit with `[skip-version-bump]`,
and create the tag and release yourself.

## Docker image

`docker-publish-dotnet.yml` builds `Dockerfile.dotnet` for `linux/amd64` and `linux/arm64`.

| Tag | Image | Moves |
| --- | --- | --- |
| `dotnet` | The newest commit on `dotnet` | On every publish of that commit |
| `X.Y.Z`, such as `3.0.0` | That release | Only when the release is republished or backfilled |

The version tag is the three-part `<Version>`. The web interface shows the four-part assembly version, so `3.0.0.0`
there is the tag `3.0.0`.

Each platform is built on a runner of its own architecture, so `arm64` runs natively. Each image is started and smoke
tested, then pushed by digest with no tag. Only when every platform has passed are the tags created, as one multi-platform
image.

The publish decides which tags a commit gets:

- A commit that is the newest on `dotnet` and carries a `vX.Y.Z` tag gets `dotnet` and `X.Y.Z`.
- A commit that is the newest on `dotnet` with no such tag, such as a `[skip-version-bump]` push, gets `dotnet` only. The
  run shows "This commit is not the vX.Y.Z release, so only :dotnet is published".
- A release that `dotnet` has moved past gets `X.Y.Z` only.
- Any other commit publishes nothing, and the run says so.

Publishes that move `dotnet` run one at a time and never cancel each other, so the newest commit is always the last to
move the tag.

### Publishing by hand

Run the workflow from the Actions tab on the `dotnet` branch:

- With **version** empty, it republishes the newest commit on `dotnet`, for example after a failed push to Docker Hub.
  Start a new run rather than re-running an old one, which builds its original commit again.
- With **version** set, it backfills a release. See below.

### Backfilling a version tag

To publish the tag of an earlier release:

1. Go to Actions > "Build and Push .NET Docker Image" > Run workflow.
2. Leave the branch on `dotnet` and enter the release in **version**, such as `2.0.399`. Do not add a `v` or a fourth
   part.

The run checks out the release's `v2.0.399` git tag, checks that its `.csproj` holds that version, and pushes
`iceburn1/comicmaintainer:2.0.399` only. It never touches `dotnet`.

The image is rebuilt from the release's source with today's base images, so it is not byte-for-byte the image first
published. Releases before 2.0.288 were `linux/amd64` only and are rebuilt for that platform only.

To tag the exact image that was published instead, copy its digest from that release's publish run, then, with push
access:

```bash
docker buildx imagetools create -t iceburn1/comicmaintainer:2.0.399 \
  iceburn1/comicmaintainer@sha256:<digest>
```

### Required secrets

| Secret | Used for |
| --- | --- |
| `DOCKER_USERNAME` | Docker Hub user |
| `DOCKER_PASSWORD` | Docker Hub password or, better, an access token |
| `CODECOV_TOKEN` | Optional: uploads coverage on pushes to `dotnet` |
| `CLAUDE_CODE_OAUTH_TOKEN` | The two Claude workflows |

## Windows and Android downloads

`build-windows-release.yml` publishes self-contained `win-x64` and `win-x86` builds as ZIP files. `build-android-apk.yml`
publishes the signed APK from `src/ComicMaintainer.Android.sln`.

Both attach their files to the release of the tag they are given. Run either by hand with a tag to rebuild a release's
downloads, or with no tag to build the selected branch and keep the result as a workflow artifact without publishing
anything.

## Security scans

| Scan | Runs on | Fails on |
| --- | --- | --- |
| NuGet packages, in `security-scan.yml` | Pull requests, pushes to `dotnet`, weekly | A high or critical advisory in a shipped package, direct or transitive, through `scripts/check-vulnerable-packages.js` |
| Image built for the change, in `integration-tests-e2e.yml` | Pull requests, pushes to `dotnet` | A fixable critical or high vulnerability |
| No-cache rebuild, and the published `:dotnet` image, in `security-scan.yml` | Weekly, on Monday at 9:00 UTC, and by hand | A fixable critical or high vulnerability |
| CodeQL for C#, JavaScript and workflow files | Pull requests, pushes to `dotnet`, weekly | See [CODEQL_CONFIGURATION.md](CODEQL_CONFIGURATION.md) |

Medium and higher findings from Trivy are uploaded to the repository's code scanning alerts.

## Troubleshooting

**A push to `dotnet` released nothing.** Check the Release run. If `dotnet` moved past the tested commit, the run for the
newer push releases it. If the tests failed, no version is made and no image is published.

**A version tag is missing.** Check the notices on the publish job, then [backfill](#backfilling-a-version-tag) it.

**The push to Docker Hub failed.** Check the secrets and Docker Hub's rate limits, then run the workflow by hand with
**version** empty.

**A new base-image vulnerability blocks every pull request.** The Trivy gate fails on fixable critical and high
vulnerabilities, so the fix is usually to pick up the updated base image or package.

## Local testing

```bash
docker build -f Dockerfile.dotnet -t comicmaintainer:ci .
./scripts/container-smoke-test.sh
```

See also [Pin or Roll Back to a Version](DOCKER_DEPLOYMENT_DOTNET.md#pin-or-roll-back-to-a-version) before running an
older release against an existing `/Config`.
