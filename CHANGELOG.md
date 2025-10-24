# Changelog

All notable changes to ComicTagger Watcher Service will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
