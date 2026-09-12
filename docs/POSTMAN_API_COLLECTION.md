# ComicMaintainer API - Postman Collection

This directory contains a comprehensive Postman collection for testing and validating the ComicMaintainer Web API locally.

## Files Included

1. **ComicMaintainer.postman_collection.json** - Complete API collection with all endpoints
2. **ComicMaintainer.postman_environment.json** - Local environment configuration

## Quick Start

### 1. Import into Postman

#### Import Collection
1. Open Postman
2. Click **Import** button (top left)
3. Select `ComicMaintainer.postman_collection.json`
4. Click **Import**

#### Import Environment
1. Click **Import** button again
2. Select `ComicMaintainer.postman_environment.json`
3. Click **Import**
4. Select **ComicMaintainer - Local** from the environment dropdown (top right)

### 2. Start the API Server

Before using the collection, make sure the ComicMaintainer API is running:

#### Using Docker:
```bash
docker-compose -f docker-compose.dotnet.yml up -d
```

#### Using .NET CLI:
```bash
cd src/ComicMaintainer.WebApi
dotnet run
```

The API should be running at `http://localhost:5000`

### 3. Authentication Flow

Most endpoints require authentication. Follow these steps:

1. **Check Setup Required** (Auth folder)
   - Run this first to see if initial setup is needed
   - If `setupRequired: true`, proceed to step 2

2. **Setup Admin User** (if needed)
   - Create the first admin user
   - Modify the request body with your desired credentials
   - Default: username=`admin`, password=`Admin123!`

3. **Login**
   - Use your admin credentials
   - The JWT token will be automatically saved to the environment
   - All subsequent requests will use this token

4. **Test Other Endpoints**
   - Now you can test any other endpoint
   - The Bearer token is automatically applied to all requests

## Collection Structure

The collection is organized into logical folders:

### Auth
- Check Setup Required
- Setup Admin User
- Login (auto-saves JWT token)
- Register User
- Change Password
- Generate API Key

### Files
- List Files (with filtering, pagination, sorting)
- Get File Counts
- Scan Unmarked Files
- Get/Update File Metadata
- Process Single File
- Process Batch
- Update Tags (Batch)
- Cleanup Stale Entries
- Delete File

### Jobs
- Get Active Job
- List All Jobs
- Get Job Details
- Cancel Job
- Delete Job
- Process All/Unmarked/Selected Files
- Rename All/Unmarked/Selected Files
- Normalize All/Unmarked/Selected Files
- Update Metadata for Selected Files

### Settings
- Get All Settings
- Filename Format (Get/Update)
- Issue Number Padding (Get/Update)
- Log Max Bytes (Get/Update)
- Watcher Enable Rename (Get/Update)
- Watcher Enable Normalize (Get/Update)
- Database Cleanup Interval (Get/Update)
- Cleanup Database Now

### Watcher
- Get Watcher Status

### Logs
- List Log Files
- Get Log Content

### Processing History
- Get Processing History (with pagination)

### Version
- Get Version (no auth required)

### Preferences
- Get User Preferences
- Update User Preferences

## Environment Variables

The environment includes two variables:

- **base_url**: `http://localhost:5000` (change if using different port)
- **jwt_token**: Automatically populated after login

### Customizing the Base URL

If your API is running on a different port or host:

1. Click the environment dropdown (top right)
2. Click the eye icon next to **ComicMaintainer - Local**
3. Click **Edit**
4. Update the `base_url` value
5. Click **Save**

## Common Use Cases

### Testing File Processing

1. **Login** to get authenticated
2. **List Files** to see available files
3. **Get File Metadata** for a specific file
4. **Process Single File** or **Process All Files**
5. **Get Active Job** to monitor progress
6. **Get Job Details** with the job ID to see results

### Batch Operations

1. **Scan Unmarked Files** to count unprocessed files
2. **Process Unmarked Files** to process only new files
3. **Get Active Job** to monitor progress
4. **Get Processing History** to see what was processed

### Settings Management

1. **Get All Settings** to see current configuration
2. **Update Filename Format** to change naming pattern
3. **Update Issue Number Padding** for issue numbering
4. **Update Watcher Settings** to enable/disable auto-processing

## Tips

1. **Auto-Save Tokens**: The Login request includes a script that automatically saves the JWT token to the environment
2. **Request Descriptions**: Each request includes a description explaining its purpose
3. **Example Bodies**: POST/PUT requests include example JSON bodies
4. **Query Parameters**: GET requests show all available query parameters with descriptions
5. **Variables**: Use `{{base_url}}` and `{{jwt_token}}` in your own requests

## Troubleshooting

### "Connection Refused" Error
- Ensure the API is running: `docker ps` or check your terminal
- Verify the base_url matches your API port

### "401 Unauthorized" Error
- Run the Login request to get a fresh token
- Check that jwt_token is set in the environment

### "Cannot find module" or Build Errors
- Make sure you're running from the correct directory
- For .NET: `cd src/ComicMaintainer.WebApi && dotnet run`

### API Returns 404
- Check the endpoint path in the request
- Verify you're using the correct API version
- Look at the API logs for more details

## Advanced Usage

### Running Tests
You can add test scripts to requests. Example (already included in Login):

```javascript
// Extract and save token
if (pm.response.code === 200) {
    var jsonData = pm.response.json();
    if (jsonData.token) {
        pm.environment.set('jwt_token', jsonData.token);
    }
}
```

### Collection Variables
The collection includes default variables that can be overridden:
- `base_url`: Default is `http://localhost:5000`
- `jwt_token`: Populated after login

### Folder-Level Auth
The entire collection uses Bearer token authentication by default. Individual requests can override this (like Login and Version).

## API Documentation

For detailed API documentation, including:
- Request/response schemas
- Error codes
- Business logic

See the source code documentation in:
- `src/ComicMaintainer.WebApi/Controllers/`
- `README.DOTNET.md`

## Support

For issues or questions:
- Check the logs: Use the **Logs** folder endpoints
- Review the API logs in `/Config/app.log`, `/Config/debug.log`
- Open an issue on GitHub

## License

This Postman collection is part of the ComicMaintainer project and follows the same license.
