# Documentation

Detailed documentation for ComicMaintainer. [README.md](../README.md) is the place to start.

Earlier write-ups and Python-era documents are kept in [archive/](archive/README.md). They describe the past, not how the
app works today.

## Getting started

- [README](../README.md): features, installation and configuration
- [QUICKSTART](../QUICKSTART.md): running it in a few minutes
- [USER_GUIDE](USER_GUIDE.md): how the library, metadata, duplicates and reader behave
- [RELEASE_NOTES_V3.0](RELEASE_NOTES_V3.0.md): what changed in 3.0
- [MIGRATION_GUIDE](MIGRATION_GUIDE.md): upgrading from an earlier version
- [CHANGELOG](../CHANGELOG.md): version history

## Configuration

- [ENVIRONMENT_VARIABLES](ENVIRONMENT_VARIABLES.md): every environment variable and setting

## Features

- [OPDS](OPDS.md): browsing the library from comic apps
- [NOTIFICATIONS](NOTIFICATIONS.md): ntfy, Gotify, Discord, webhooks, and Komga or Kavita rescans
- [EMAIL_DELIVERY](EMAIL_DELIVERY.md): email to e-readers
- [MAUI_ANDROID_SETUP](MAUI_ANDROID_SETUP.md): the Android app

## Deployment and operations

- [DOCKER_DEPLOYMENT_DOTNET](DOCKER_DEPLOYMENT_DOTNET.md): running with Docker
- [kubernetes-deployment.yaml](kubernetes-deployment.yaml): example Kubernetes manifest
- [REVERSE_PROXY](REVERSE_PROXY.md): running behind a reverse proxy
- [SWAG_PROXY_GUIDE](SWAG_PROXY_GUIDE.md) and [swag-configs/](swag-configs/README.md): SWAG setup and ready-made configs
- [AUTHELIA](AUTHELIA.md): Authelia single sign-on
- [HTTPS_SETUP](HTTPS_SETUP.md): enabling HTTPS
- [PERFORMANCE_TUNING](PERFORMANCE_TUNING.md): tuning for large libraries
- [DATABASE](DATABASE.md): the database, backups and maintenance
- [DEBUG_LOGGING_GUIDE](DEBUG_LOGGING_GUIDE.md): logs, diagnostics and automated error reports

## API

- [API](API.md): REST API reference
- [POSTMAN_API_COLLECTION](POSTMAN_API_COLLECTION.md): Postman collection for trying the API

## Architecture and development

- [CONTRIBUTING](../CONTRIBUTING.md): setting up and submitting changes
- [TESTING_POLICY](TESTING_POLICY.md): what needs tests and what CI enforces
- [GITHUB_ACTIONS_DOTNET](GITHUB_ACTIONS_DOTNET.md): CI and the release pipeline
- [IOC_DESIGN_PRINCIPLES](IOC_DESIGN_PRINCIPLES.md): dependency injection rules
- [ASYNC_PROCESSING](ASYNC_PROCESSING.md): the watcher and batch jobs
- [EVENT_BROADCASTING_SYSTEM](EVENT_BROADCASTING_SYSTEM.md): real-time events
- [WATCHER_STATUS_INDICATOR](WATCHER_STATUS_INDICATOR.md): the watcher status shown in the interface
- [SERVER_SIDE_PREFERENCES](SERVER_SIDE_PREFERENCES.md): where user preferences are stored
- [WEBCOMIC_IMPLEMENTATION_KAVITA](WEBCOMIC_IMPLEMENTATION_KAVITA.md): the webcomic reader's design
- [COPILOT_PR_REVIEW](COPILOT_PR_REVIEW.md): automatic Copilot reviews

## Security

- [SECURITY](../SECURITY.md): security policy and reporting a vulnerability
- [SECURITY_SCANNING](SECURITY_SCANNING.md): automated security scanning
- [CODEQL_CONFIGURATION](CODEQL_CONFIGURATION.md): CodeQL setup
- [SECURITY_TEST_PLAN](SECURITY_TEST_PLAN.md): security test plan
