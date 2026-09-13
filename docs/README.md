# Documentation

Detailed documentation for ComicMaintainer. The repository root keeps only the five files
people look for first — `README.md`, `QUICKSTART.md`, `CONTRIBUTING.md`, `SECURITY.md` and
`CHANGELOG.md` — and everything else lives here.

Superseded material (one-off fix write-ups, PR summaries and review guides from past changes)
is kept in [archive/](archive/README.md) rather than deleted, so this index stays focused on
documentation that still describes how the project works today.

## Getting started

- [README.DOTNET.md](README.DOTNET.md) - The .NET version (v2.0), which is the primary implementation
- [RELEASE_NOTES_V2.0.md](RELEASE_NOTES_V2.0.md) - What changed in v2.0
- [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md) - Upgrading from the Python version
- [DOCKER_DEPLOYMENT_DOTNET.md](DOCKER_DEPLOYMENT_DOTNET.md) - Deploying with Docker
- [MAUI_ANDROID_SETUP.md](MAUI_ANDROID_SETUP.md) - Building and running the Android app

## API

- [API.md](API.md) - Complete REST API reference
- [POSTMAN_API_COLLECTION.md](POSTMAN_API_COLLECTION.md) - Postman collection for trying the API

## Architecture and design

- [ARCHITECTURE_CHANGE.md](ARCHITECTURE_CHANGE.md) - Major architectural changes
- [UNIFIED_DATABASE_ARCHITECTURE.md](UNIFIED_DATABASE_ARCHITECTURE.md) - The unified database design
- [ASYNC_PROCESSING.md](ASYNC_PROCESSING.md) - Asynchronous processing design
- [IOC_DESIGN_PRINCIPLES.md](IOC_DESIGN_PRINCIPLES.md) - Dependency injection conventions
- [EVENT_BROADCASTING_SYSTEM.md](EVENT_BROADCASTING_SYSTEM.md) - Server-sent event system
- [EVENT_SYSTEM_QUICK_REFERENCE.md](EVENT_SYSTEM_QUICK_REFERENCE.md) - Event system cheat sheet
- [EVENT_SYSTEM_FLOW_DIAGRAMS.md](EVENT_SYSTEM_FLOW_DIAGRAMS.md) - Event system flow diagrams
- [PROGRESS_CALLBACKS.md](PROGRESS_CALLBACKS.md) - How job progress is reported
- [FLOW_DIAGRAM.md](FLOW_DIAGRAM.md) - System flow diagrams
- [RETRY_FLOW_DIAGRAM.md](RETRY_FLOW_DIAGRAM.md) - Retry logic flow
- [CANCEL_BUTTON_FLOW.md](CANCEL_BUTTON_FLOW.md) - Job cancellation flow
- [WORKER_TIMEOUT_FLOW.md](WORKER_TIMEOUT_FLOW.md) - Worker timeout handling
- [WATCHER_STATUS_INDICATOR.md](WATCHER_STATUS_INDICATOR.md) - Watcher status reporting
- [WEBCOMIC_IMPLEMENTATION_KAVITA.md](WEBCOMIC_IMPLEMENTATION_KAVITA.md) - Web reader design notes

## Deployment and operations

- [REVERSE_PROXY.md](REVERSE_PROXY.md) - Running behind a reverse proxy
- [SWAG_PROXY_GUIDE.md](SWAG_PROXY_GUIDE.md) - SWAG-specific setup, with ready-made configs in [swag-configs/](swag-configs/README.md)
- [HTTPS_SETUP.md](HTTPS_SETUP.md) - Enabling HTTPS
- [AUTHELIA.md](AUTHELIA.md) - Authelia single sign-on integration
- [PERFORMANCE_TUNING.md](PERFORMANCE_TUNING.md) - Tuning for large libraries
- [SERVER_SIDE_PREFERENCES.md](SERVER_SIDE_PREFERENCES.md) - Where user preferences are stored
- [DEBUG_LOGGING_GUIDE.md](DEBUG_LOGGING_GUIDE.md) - Turning on and reading debug logs
- [SETTINGS_MODAL_TROUBLESHOOTING.md](SETTINGS_MODAL_TROUBLESHOOTING.md) - Diagnosing settings UI problems

## Data and migrations

- [SQLITE_MIGRATION.md](SQLITE_MIGRATION.md) - Move to SQLite storage
- [MARKER_MIGRATION.md](MARKER_MIGRATION.md) - Marker system migration
- [MARKER_SQLITE_SUMMARY.md](MARKER_SQLITE_SUMMARY.md) - SQLite marker implementation
- [UNIFIED_DATABASE_MIGRATION.md](UNIFIED_DATABASE_MIGRATION.md) - Consolidating the databases
- [MIGRATION_SUMMARY.md](MIGRATION_SUMMARY.md) - Overview of past migrations

## Development

- [TESTING_POLICY.md](TESTING_POLICY.md) - Testing requirements for all changes
- [GITHUB_ACTIONS_DOTNET.md](GITHUB_ACTIONS_DOTNET.md) - CI workflows
- [AUTOMATED_VERSIONING.md](AUTOMATED_VERSIONING.md) - How versions are bumped
- [STABLE_BRANCH_CREATION.md](STABLE_BRANCH_CREATION.md) - The stable branch process

## Security

- [SECURITY_SCANNING.md](SECURITY_SCANNING.md) - Automated security scanning
- [CODEQL_CONFIGURATION.md](CODEQL_CONFIGURATION.md) - CodeQL setup
- [SECURITY_TEST_PLAN.md](SECURITY_TEST_PLAN.md) - Security test plan

For the main project documentation, see [README.md](../README.md) in the root directory. For
reporting vulnerabilities, see [SECURITY.md](../SECURITY.md).
