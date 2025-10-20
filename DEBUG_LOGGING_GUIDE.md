# Debug Logging and Error Reporting Guide

## Overview

This guide explains the extensive debug logging and automatic error reporting features that have been added to the ComicMaintainer application.

## Features

### 1. Debug Logging

The application now supports comprehensive debug logging that can be enabled via the `DEBUG_MODE` environment variable.

**Enable Debug Mode:**
```bash
docker run -e DEBUG_MODE=true ...
```

**What gets logged in debug mode:**
- Function entry and exit with parameters and return values
- Detailed operation tracking (file checks, cache operations, metadata processing)
- Variable values and state at key decision points
- Performance insights for troubleshooting
- Context information for every operation

**Example debug output:**
```
DEBUG: ENTER process_file with params: {"filepath": "/path/to/file.cbz", "fixtitle": true, "fixseries": true, "fixfilename": true}
DEBUG: Opening comic archive | Context: {"filepath": "/path/to/file.cbz"}
DEBUG: Read tags from archive | Context: {"filepath": "/path/to/file.cbz", "has_tags": true}
DEBUG: Checking title normalization | Context: {"filepath": "/path/to/file.cbz"}
DEBUG: EXIT process_file -> /path/to/renamed_file.cbz
```

### 2. Automatic GitHub Issue Creation

When errors occur, the application can automatically create GitHub issues with full context and stack traces.

**Setup:**
1. Create a GitHub Personal Access Token with `repo` scope
2. Set environment variables:
   ```bash
   -e GITHUB_TOKEN=ghp_your_token_here
   -e GITHUB_ISSUE_ASSIGNEE=your_username  # Optional, defaults to "copilot"
   ```

**What gets reported:**
- Full exception type and message
- Complete stack trace
- Operation context (what was being done when error occurred)
- Additional diagnostic information (file paths, parameters, etc.)
- Timestamp and unique error ID
- Automatic assignment to configured user
- Tagged with `bug` and `auto-generated` labels

**Example auto-generated issue:**
```markdown
## Automated Error Report
**Error ID:** `ValueError:1234`
**Error Type:** `ValueError`
**Timestamp:** 2025-10-20T08:24:23.624Z

### Error Message
```
Invalid file format: expected .cbz or .cbr
```

### Context
Processing file: /watched_dir/comics/test.txt

### Additional Information
```json
{
  "filepath": "/watched_dir/comics/test.txt",
  "operation": "process_file"
}
```

### Traceback
```python
Traceback (most recent call last):
  File "/app/process_file.py", line 298, in process_file
    ca = ComicArchive(filepath)
ValueError: Invalid file format: expected .cbz or .cbr
```

---
*This issue was automatically created by the error handling system.*
```

**Duplicate Prevention:**
- The system caches error IDs to prevent creating multiple issues for the same error
- Cache holds up to 100 unique errors
- Error ID is generated from error type and message hash

## Implementation Details

### Core Module: `error_handler.py`

The centralized error handling module provides:

#### Functions:

**`setup_debug_logging(logger_name=None)`**
- Configures debug logging for a specific logger or root logger
- Respects DEBUG_MODE environment variable
- Returns configured logger instance

**`log_debug(message, **kwargs)`**
- Logs debug message with optional context
- Only logs if DEBUG_MODE is enabled
- Context is serialized as JSON

**`log_error_with_context(error, context="", additional_info=None, create_github_issue=True)`**
- Logs error with full context and traceback
- Optionally creates GitHub issue
- Generates unique error ID for tracking
- Prevents duplicate issue creation

**`log_function_entry(func_name, **kwargs)`**
- Logs function entry with parameters (debug only)
- Useful for tracing execution flow

**`log_function_exit(func_name, result=None)`**
- Logs function exit with result (debug only)
- Useful for tracking return values

**`safe_execute(func, *args, context="", create_issue=True, **kwargs)`**
- Wrapper that executes function with error handling
- Automatically logs errors with context
- Returns None on error instead of raising

### Modified Modules

The following modules have been enhanced with debug logging:

#### `watcher.py`
- File event logging (created, modified, deleted, moved)
- Stability check details
- Processing decision logging
- Cache update tracking

#### `process_file.py`
- File normalization checks
- Tag reading and writing
- Chapter number parsing
- Filename formatting
- Duplicate detection
- All exception handlers wrapped with context

#### `job_manager.py`
- Job creation and lifecycle
- Worker pool operations
- Item processing progress
- Success/failure tracking
- Error handling in concurrent operations

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DEBUG_MODE` | `false` | Enable extensive debug logging |
| `GITHUB_TOKEN` | - | GitHub Personal Access Token for issue creation |
| `GITHUB_REPOSITORY` | `mleenorris/ComicMaintainer` | Repository for issue creation |
| `GITHUB_ISSUE_ASSIGNEE` | `copilot` | Username to assign auto-generated issues |
| `GITHUB_API_URL` | `https://api.github.com` | GitHub API endpoint |

## Usage Examples

### Example 1: Enable Debug Mode Only
```bash
docker run -d \
  -v /host/comics:/watched_dir \
  -v /host/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DEBUG_MODE=true \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

View logs:
```bash
docker logs -f <container_id>
```

### Example 2: Enable Debug Mode + GitHub Issues
```bash
docker run -d \
  -v /host/comics:/watched_dir \
  -v /host/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e DEBUG_MODE=true \
  -e GITHUB_TOKEN=ghp_xxxxxxxxxxxx \
  -e GITHUB_ISSUE_ASSIGNEE=myusername \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

### Example 3: GitHub Issues Only (No Debug Logging)
```bash
docker run -d \
  -v /host/comics:/watched_dir \
  -v /host/config:/Config \
  -e WATCHED_DIR=/watched_dir \
  -e GITHUB_TOKEN=ghp_xxxxxxxxxxxx \
  -p 5000:5000 \
  iceburn1/comictagger-watcher:latest
```

## Troubleshooting

### Debug logs not appearing
- Verify `DEBUG_MODE=true` is set (case-insensitive)
- Check log level is set to DEBUG
- View container logs: `docker logs <container_id>`

### GitHub issues not being created
- Verify `GITHUB_TOKEN` is set and valid
- Token must have `repo` scope
- Check container logs for "Failed to create GitHub issue" warnings
- Verify repository name is correct in `GITHUB_REPOSITORY`

### Too many debug logs
- Debug mode is very verbose - use only for troubleshooting
- Consider using log rotation settings (`LOG_MAX_BYTES`)
- Filter logs: `docker logs <container_id> | grep ERROR`

### Duplicate issues being created
- System caches up to 100 unique error IDs
- If cache fills, oldest entries are removed
- Restart container to clear cache
- Different error messages generate different error IDs

## Best Practices

1. **Use debug mode selectively**: Only enable when troubleshooting issues
2. **Monitor log file size**: Debug mode generates significantly more logs
3. **Secure your GitHub token**: Store in Docker secrets or environment files
4. **Review auto-generated issues**: They provide valuable diagnostic information
5. **Disable GitHub issues in development**: Set `GITHUB_TOKEN` only in production
6. **Use error IDs for tracking**: Reference the error ID when investigating issues

## Performance Impact

- **Debug logging**: Minimal CPU overhead, increased I/O for log writes
- **GitHub issue creation**: Network request on error (rate-limited internally)
- **Normal operation**: No performance impact when DEBUG_MODE is disabled

## Security Considerations

- **GitHub Token**: Keep secure, use environment variables, never commit to code
- **Sensitive data**: Error logs may contain file paths and parameters
- **Issue visibility**: Auto-generated issues are public in public repositories
- **Rate limiting**: GitHub API has rate limits - error cache helps prevent abuse

## Future Enhancements

Potential future improvements:
- Configurable error cache size
- Error severity levels for issue creation
- Email notifications in addition to GitHub issues
- Metrics and error rate tracking
- Integration with monitoring services (Sentry, DataDog, etc.)
