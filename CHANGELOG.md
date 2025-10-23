# Changelog

All notable changes to ComicTagger Watcher Service will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Health check endpoint at `/health` and `/api/health` for Docker and Kubernetes orchestration
- Docker Compose configuration file for easier setup and deployment
- Changelog to track version history and changes

### Fixed
- Fixed Bandit security scanner configuration file format (YAML syntax)

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
