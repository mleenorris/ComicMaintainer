# Changelog

All notable changes to ComicTagger Watcher Service will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **The web UI now announces what it is doing to assistive technology.** `showMessage()` is
  the library page's only notification channel — roughly a hundred call sites, including
  every error path — but `#messageContainer` was not a live region, so none of it reached
  screen readers. The container is now a polite live region and errors are additionally
  marked `role="alert"` so a failure interrupts rather than queueing behind routine progress
  chatter. The sign-in and first-run setup pages had the same problem in a more damaging
  form: a rejected sign-in wrote to `#errorMessage` and nothing else, leaving a screen reader
  user with no indication the attempt had failed. Those elements are now `role="alert"` /
  `role="status"`, and because they are `display: none` until shown (which keeps them out of
  the accessibility tree) the element is revealed *before* the text is written, so the change
  happens in a region that is already exposed.

- **Reduced-motion users no longer get frozen loading spinners.** The
  `prefers-reduced-motion` reset zeroed every animation on the page, which included the
  spinners. Several of those replace the button label while a request is in flight, so the
  reset turned "signing in…" into a static circle with no text and no other feedback. WCAG
  2.2.2 exempts motion that is essential to conveying information, so spinners now keep
  animating while decorative motion stays suppressed.

- **Viewport resize work is debounced.** Two separate `resize` listeners re-evaluated media
  queries and wrote to the DOM on every event, which fires continuously while a window is
  dragged or a device rotated. They now share a single debounced dispatcher.

- **Service worker update checks pause in hidden tabs.** The check ran every minute for the
  lifetime of the page, so a pinned tab left open kept issuing a request a minute
  indefinitely — to show an update banner nobody was looking at. It is now skipped while the
  tab is hidden, and runs when the tab becomes visible again so a returning user is not left
  waiting. Both paths share the same minimum interval: `sw.js` is served `no-store` and the
  main script always bypasses the HTTP cache, so every check is a real request and an
  unthrottled focus-triggered check would be noisier than the plain interval it replaced.

- **Documentation reorganised.** The repository root had accumulated 69 markdown files, most of
  them point-in-time fix write-ups and PR summaries, which buried the handful of documents
  people actually look for. The root now keeps only `README.md`, `QUICKSTART.md`,
  `CONTRIBUTING.md`, `SECURITY.md` and `CHANGELOG.md`; current reference material moved to
  `docs/`, and superseded write-ups to `docs/archive/`. Nothing was deleted, all files were
  moved with history preserved, and every internal link was updated.
  - `docs/README.md` is now an accurate index of current documentation, and
    `docs/archive/README.md` states plainly that its contents are unmaintained snapshots
    rather than a description of current behaviour.

- **Read/unread status is now per-user.** It previously lived in two global places
  (`ComicFiles.IsRead` and the `FileReadStatuses` table), so in a multi-user deployment one
  user marking an issue read flipped it for everyone, and that global state could disagree
  with the per-user progress the reader itself showed. Read status and the resume page are
  now stored per user in `UserFileReadStatuses`, and marking a file read/unread from the
  library also updates that user's reader progress so "Continue Reading" stays consistent.
  - Existing read state is migrated automatically. Because the old data does not record
    *who* read a file, it is attributed to a single account: an `Admin` if one exists,
    otherwise any user. Where per-user reader progress already existed it takes precedence,
    so issues finished in the reader stay attributed to the user who actually read them.
    **In a multi-user deployment, non-admin users may need to re-mark issues read.**
  - Marking a file read or unread no longer requires the `CanModifyLibrary` policy, since it
    only affects the calling user. `ReadOnly` users can now track their own progress. This
    resolves the asymmetry noted in the previous release, where the reader's mark-read was
    open but the library's bulk mark-read was treated as a library mutation.
  - `FileReadStatusEntity` and `ComicFiles.IsRead` are retained but unused so the migration
    can be rolled back; both are scheduled for removal in v3.0.

### Fixed
- Per-file metadata is no longer zero padded. The ComicInfo `<Number>` (issue)
  field now stores the bare issue number (e.g. `12`, not `0012`) and the
  `<Title>` field is set to `Chapter <issue>` (e.g. `Chapter 12`). Only the
  generated filename continues to apply the configured zero padding.
- Allow pinning a series name for series stuck in a "bad state" with no cached
  metadata record (e.g. series grouped purely from files on disk). Setting a
  non-empty name now creates a minimal manual cache record instead of failing
  with a 404, so these series can be fixed and their on-disk `<Series>` tags
  retagged. Reverting such a series to automatic remains a no-op.

### Added
- **Email comics to an e-reader.** Issues can be sent to saved e-reader addresses one at a
  time, as a whole series, or as an arbitrary selection, and a series can be subscribed so
  that every newly processed issue is delivered automatically once the watcher has finished
  renaming and normalizing it. Each delivery is sent either as the original archive or
  converted to a fixed-layout EPUB 3 first, which is the format e-readers actually render
  page-per-image rather than reflowing. Sends are queued and drained by a single background
  consumer, so a large series does not block the API or the watcher and does not hammer the
  mail server; already-delivered files are skipped by default, making a re-send of a series
  safe. Only files inside the watched or duplicate directory can be sent — symlinks and
  junctions are resolved first, so a link inside the library cannot point at a file outside
  it — the SMTP password is never returned by the API and is redacted from logs, and
  deliveries larger than the configured attachment limit fail rather than being sent.
  STARTTLS is required unless implicit TLS is selected or the new `SMTP_ALLOW_INSECURE`
  opt-in is enabled for a trusted local relay, and sending requires the `CanModifyLibrary`
  policy. See [Email to E-Reader](docs/EMAIL_DELIVERY.md).
- **Offline awareness.** A `fetch()` made with no network rejects with a bare
  "Failed to fetch", which the UI surfaced as a generic "Failed to load …" — indistinguishable
  from a server fault, so users retried against a server they could not reach. A banner now
  states the actual cause and disappears by itself when the connection returns.
- **Last-resort error reporting.** An uncaught exception or rejected promise used to stop the
  surrounding code silently, leaving spinners spinning and stale data on screen with nothing
  to explain why. `unhandledrejection` and `error` are now handled globally and surface a
  message, rate limited so a failure inside a render or loop path cannot bury the UI in
  banners. Reports are suppressed while offline, where the offline banner already explains
  the cause.
- Keyboard focus indicators, a `<main>` landmark and an accessible name for the theme toggle
  on the sign-in and first-run setup pages. These are standalone pages that do not load
  `css/main.css`, so they had missed the global `:focus-visible` rule the rest of the app
  gained: they cleared the default focus outline for mouse users and never restored one,
  leaving keyboard users with no visible focus on the sign-in form.
- Multi-architecture Docker images. `linux/arm64` is now published alongside `linux/amd64`,
  so the image runs natively on Apple Silicon and common ARM NAS/SBC hosts. The .NET build
  runs natively on the build host and cross-compiles, so adding the second architecture does
  not require emulating the whole SDK.
- Container `HEALTHCHECK` in `Dockerfile.dotnet`, wired to the existing `/health` liveness
  endpoint, so `docker ps` and orchestrators can see container health without extra
  configuration. It deliberately uses `/health` rather than `/health/ready`: readiness also
  checks the database, and a transient database problem should not cause a restart loop.
- Dependabot configuration for NuGet, GitHub Actions and Docker base images, with Microsoft
  runtime packages grouped so their coordinated releases arrive as a single pull request.
- Batch processing jobs now survive a restart. Job state is persisted, and any job still
  queued or running when the process stops is reported as `Interrupted` on the next startup,
  along with how many files it had already processed.
  - Previously a restart (including the app's own `POST /api/settings/restart`) discarded
    in-flight jobs entirely, leaving the UI polling a job id the server no longer recognised
    and showing a progress bar that never moved.
  - Interrupted jobs are **not** resumed automatically: the batch operations are not
    transactional, so re-running one could repeat side effects on files already handled. The
    UI reports the interruption and the progress made so you can decide whether to re-run it.
  - Jobs now record which operation created them, so an interrupted job can be described
    meaningfully after a restart.
  - Completed job records are pruned after 7 days, keeping the 50 most recent regardless of age.
- Health check endpoint at `/health` and `/api/health` for Docker and Kubernetes orchestration
  - Returns 200 OK when healthy, 503 when unhealthy
  - Checks watched directory, database connectivity, and watcher process status
  - Includes version information and file count
- Docker Compose configuration file (`docker-compose.yml`) for easier setup and deployment
  - Pre-configured with sensible defaults
  - Includes health checks and volume mappings
  - Easy customization for user/group IDs
- Kubernetes deployment manifests (`docs/kubernetes-deployment.yaml`)
  - Complete deployment with PVCs, services, and ingress
  - Health checks (liveness, readiness, startup probes)
  - Resource limits and security context
  - Horizontal Pod Autoscaler example (commented)
- Comprehensive API documentation (`docs/API.md`)
  - Complete REST API reference with examples
  - Request/response formats for all endpoints
  - Examples in curl, Python, and JavaScript
  - SSE event documentation
- Contributing guide (`CONTRIBUTING.md`)
  - Development setup instructions
  - Code quality standards
  - Testing guidelines
  - Pull request process
- Environment variable validation module (`src/env_validator.py`)
  - Validates required and optional environment variables
  - Checks numeric ranges and directory accessibility
  - Provides helpful error messages
  - Sets default values for optional variables
  - Integrated into container startup
- Changelog (`CHANGELOG.md`) to track version history and changes
- `.pylintrc` configuration for consistent code quality
- Organized historical documentation into `docs/archive/`
- Test suite for environment validator (`test_env_validator.py`)

### Changed
- Reorganized documentation structure
  - Moved 19 historical documentation files to `docs/archive/`
  - Created `docs/archive/README.md` to explain archived documents
  - Updated main README with better organization and documentation links
- Enhanced README.md
  - Added documentation section with links to all guides
  - Added quick start section for Docker Compose
  - Improved readability and organization
- Updated `start.sh` to validate environment variables before starting services

### Fixed
- Fixed Bandit security scanner configuration file format (YAML syntax error)
- Fixed test warnings about return values in pytest
  - Updated `test_job_specific_events.py` - 3 tests fixed
  - Updated `test_progress_callbacks.py` - 2 tests fixed
  - All tests now use assertions instead of return values

### Documentation
- Consolidated 19 historical summary files into organized archive
- Added comprehensive API documentation with examples
- Added Kubernetes deployment guide
- Added development contribution guidelines
- Improved README structure and navigation


## [1.0.1] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.2] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.3] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.4] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.5] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.6] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.7] - 2025-10-23

### Changed
- Automatic version bump on merge to master


## [1.0.8] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.9] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.10] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.11] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.12] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.13] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.14] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.15] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.16] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.17] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.18] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.19] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.20] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.21] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.22] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.23] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.24] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.25] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.26] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.27] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.28] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.29] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.30] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.31] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.32] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.33] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.34] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.35] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.36] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.37] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.38] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.39] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.40] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.41] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.42] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.1] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.2] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.3] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.4] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.5] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.6] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.7] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.8] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.9] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.10] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.11] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.12] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.13] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.14] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.15] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.16] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.17] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.18] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.19] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.20] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.21] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.22] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.23] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.24] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.25] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.26] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.27] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.28] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.29] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.30] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.31] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.32] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.33] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.34] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.35] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.36] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.37] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.38] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.39] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.40] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.41] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.42] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.43] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.44] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.45] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.46] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.47] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.48] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.49] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.50] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.51] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.52] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.53] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.54] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.55] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.56] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.57] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.58] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.59] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.60] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.61] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.62] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.63] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.64] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.65] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.66] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.67] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.68] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.69] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.70] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.71] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.72] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.73] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.74] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.75] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.76] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.77] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.78] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.79] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.80] - 2025-11-10

### Changed
- Automatic version bump on merge to master


## [2.0.81] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.82] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.83] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.84] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.85] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.86] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.87] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.88] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.89] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.90] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.91] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.92] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.93] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.94] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.95] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.96] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.97] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.98] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.99] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.100] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.101] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.102] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.103] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.104] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.105] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.106] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.107] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.108] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.109] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.110] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.111] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.112] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.113] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.114] - 2025-11-14

### Changed
- Automatic version bump on merge to master


## [2.0.115] - 2025-11-17

### Changed
- Automatic version bump on merge to master


## [2.0.116] - 2025-11-18

### Changed
- Automatic version bump on merge to master


## [2.0.117] - 2025-11-19

### Changed
- Automatic version bump on merge to master


## [2.0.118] - 2026-01-10

### Changed
- Automatic version bump on merge to master


## [2.0.119] - 2026-03-25

### Changed
- Automatic version bump on merge to master


## [2.0.120] - 2026-03-25

### Changed
- Automatic version bump on merge to master


## [2.0.121] - 2026-03-26

### Changed
- Automatic version bump on merge to master


## [2.0.122] - 2026-03-26

### Changed
- Automatic version bump on merge to master


## [2.0.123] - 2026-03-28

### Changed
- Automatic version bump on merge to master


## [2.0.124] - 2026-03-28

### Changed
- Automatic version bump on merge to master


## [2.0.125] - 2026-03-29

### Changed
- Automatic version bump on merge to master


## [2.0.126] - 2026-04-08

### Changed
- Automatic version bump on merge to master


## [2.0.127] - 2026-04-08

### Changed
- Automatic version bump on merge to master


## [2.0.128] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.129] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.130] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.131] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.132] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.133] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.134] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.135] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.136] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.137] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.138] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.139] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.140] - 2026-05-13

### Changed
- Automatic version bump on merge to master


## [2.0.141] - 2026-05-13

### Changed
- Automatic version bump on merge to master


## [2.0.142] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.143] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.144] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.145] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.146] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.147] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.148] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.149] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.150] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.151] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.152] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.153] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.154] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.155] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.156] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.157] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.158] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.159] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.160] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.161] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.162] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.163] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.164] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.165] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.166] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.167] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.168] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.169] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.170] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.171] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.172] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.173] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.174] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.175] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.176] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.177] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.178] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.179] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.180] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.181] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.182] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.183] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.184] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.185] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.186] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.187] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.188] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.189] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.190] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.191] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.192] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.193] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.194] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.195] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.196] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.197] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.198] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.199] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.200] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.201] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.202] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.203] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.204] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.205] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.206] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.207] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.208] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.209] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.210] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.211] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.212] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.213] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.214] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.215] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.216] - 2026-05-26

### Changed
- Automatic version bump on merge to master


## [2.0.217] - 2026-05-26

### Changed
- Automatic version bump on merge to master


## [2.0.218] - 2026-05-27

### Changed
- Automatic version bump on merge to master


## [2.0.219] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.220] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.221] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.222] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.223] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.224] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.225] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.226] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.227] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.228] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.229] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.230] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.231] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.232] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.233] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.234] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.235] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.236] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.237] - 2026-05-30

### Changed
- Automatic version bump on merge to master


## [2.0.238] - 2026-06-01

### Changed
- Automatic version bump on merge to master


## [2.0.239] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.240] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.241] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.242] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.243] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.244] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.245] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.246] - 2026-06-03

### Changed
- Automatic version bump on merge to master


## [2.0.247] - 2026-06-03

### Changed
- Automatic version bump on merge to master


## [2.0.248] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.249] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.250] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.251] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.252] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.253] - 2026-06-05

### Changed
- Automatic version bump on merge to master


## [2.0.254] - 2026-06-05

### Changed
- Automatic version bump on merge to master


## [2.0.255] - 2026-06-16

### Changed
- Automatic version bump on merge to master


## [2.0.256] - 2026-06-19

### Changed
- Automatic version bump on merge to master


## [2.0.257] - 2026-06-24

### Changed
- Automatic version bump on merge to master


## [2.0.258] - 2026-06-24

### Changed
- Automatic version bump on merge to master


## [2.0.259] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.260] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.261] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.262] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.263] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.264] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.265] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.266] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.267] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.268] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.269] - 2026-06-29

### Changed
- Automatic version bump on merge to master


## [2.0.270] - 2026-06-30

### Changed
- Automatic version bump on merge to master


## [2.0.271] - 2026-06-30

### Changed
- Automatic version bump on merge to master


## [2.0.272] - 2026-07-11

### Changed
- Automatic version bump on merge to master


## [2.0.273] - 2026-07-11

### Changed
- Automatic version bump on merge to master


## [2.0.274] - 2026-07-12

### Changed
- Automatic version bump on merge to master


## [2.0.275] - 2026-07-15

### Changed
- Automatic version bump on merge to master


## [2.0.276] - 2026-07-21

### Changed
- Automatic version bump on merge to master


## [2.0.277] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.278] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.279] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.280] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.281] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.282] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.283] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.284] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.285] - 2026-07-25

### Changed
- Automatic version bump on merge to master


## [2.0.286] - 2026-09-10

### Changed
- Automatic version bump on merge to master


## [2.0.287] - 2026-09-12

### Changed
- Automatic version bump on merge to master


## [2.0.288] - 2026-09-13

### Changed
- Automatic version bump on merge to master


## [2.0.289] - 2026-09-15

### Changed
- Automatic version bump on merge to master


## [2.0.290] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.291] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.292] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.293] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.294] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.295] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.296] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.297] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.298] - 2026-09-20

### Changed
- Automatic version bump on merge to master


## [2.0.299] - 2026-09-20

### Changed
- Automatic version bump on merge to master


## [2.0.300] - 2026-09-22

### Changed
- Automatic version bump on merge to master


## [2.0.301] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.302] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.303] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.304] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.305] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.306] - 2026-09-25

### Changed
- Automatic version bump on merge to master

## [1.0.0] - 2024

### Added
- Python watcher service for automatic comic file processing
- Web interface for managing comic files
- ComicTagger integration for metadata management
- File processing with automatic renaming and metadata updates
- Duplicate file detection and handling
- SQLite-based unified database for file storage and marker tracking
- Real-time updates via Server-Sent Events (SSE)
- Asynchronous batch processing with job management
- Processing status tracking (processed, unprocessed, duplicate markers)
- Configurable filename format templates
- Dark mode support with user preferences
- Search and filter functionality
- Pagination for large comic libraries
- Docker container support with custom PUID/PGID
- Production-ready Gunicorn server
- Debug logging and error reporting
- GitHub integration for automatic issue creation
- Security scanning with Bandit, pip-audit, and Trivy
- Automated CI/CD with GitHub Actions
- Comprehensive test suite
- Log rotation support
- Event-driven architecture (zero polling)
- Multi-worker support with shared job state

### Performance
- SQLite with WAL mode for fast concurrent access
- Batch operations for efficient marker data fetching
- Search debouncing to reduce API calls
- Server-side filtering and pagination
- Database indexing for optimal query performance

### Security
- Security scanning in CI/CD pipeline
- Input validation
- Safe file operations
- Proper error handling
- PUID/PGID support for file permission management

[Unreleased]: https://github.com/mleenorris/ComicMaintainer/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/mleenorris/ComicMaintainer/releases/tag/v1.0.0
