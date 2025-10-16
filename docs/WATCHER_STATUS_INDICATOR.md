# Watcher Status Indicator

## Overview

The Watcher Status Indicator is a real-time visual indicator displayed in the web interface header that shows whether the file watcher service is currently running.

## Features

### Visual States

The indicator displays three possible states:

1. **✅ Running** (Green)
   - The watcher service is active and monitoring files
   - Background: Semi-transparent green
   - Tooltip: "File watcher is running and monitoring for changes"

2. **⛔ Stopped** (Red)
   - The watcher service is not running
   - Background: Semi-transparent red
   - Tooltip: "File watcher is enabled but not running" or "File watcher is disabled"

3. **❓ Unknown** (Gray)
   - Unable to determine the watcher status (e.g., API error)
   - Background: Default semi-transparent
   - Tooltip: "Unable to determine watcher status"

### Automatic Updates

- The status is checked every **10 seconds** automatically
- Updates happen in the background without user interaction
- Status is checked immediately when the page loads

## Technical Implementation

### Backend API

**Endpoint**: `GET /api/watcher/status`

**Response Format**:
```json
{
  "running": true,
  "enabled": true
}
```

- `running`: Boolean indicating if the watcher.py process is currently running
- `enabled`: Boolean indicating if the watcher is enabled in configuration

**Detection Method**: Uses `pgrep -f 'python.*watcher.py'` to check if the process is running

### Frontend

**Location**: Displayed in the header, next to the settings menu

**Polling**: JavaScript polls the API every 10 seconds using `setInterval`

**Error Handling**: If the API call fails, the indicator shows "Unknown" state

### Mobile Responsive

On mobile devices (screen width < 768px):
- Only the icon is displayed to save space
- Text label is hidden
- Tooltip remains available for status information

## Use Cases

### Monitoring Service Health
Users can quickly verify that the file watcher service is running and actively monitoring their comic library.

### Troubleshooting
If files aren't being automatically processed, users can check the indicator to see if the watcher service has stopped.

### Configuration Validation
After changing watcher settings, users can verify the service status without checking logs or running commands.

## Configuration

The watcher status indicator works with the existing watcher configuration:
- Uses the same `get_watcher_enabled()` function from `config.py`
- No additional configuration required
- Works immediately after deployment

## Performance Impact

- **Minimal**: API calls every 10 seconds are lightweight
- **Non-blocking**: Status checks run asynchronously
- **Efficient**: Uses `pgrep` which is fast and lightweight
- **Clean**: Properly cleans up polling on page unload

## Browser Compatibility

The feature uses standard Web APIs and should work in all modern browsers:
- Chrome/Edge (Chromium-based)
- Firefox
- Safari
- Opera

## Future Enhancements

Potential improvements for future versions:
- Click indicator to restart watcher service
- Show last activity timestamp
- Display watcher statistics (files processed today, etc.)
- WebSocket support for real-time updates instead of polling
