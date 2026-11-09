# Postman Collection

The repository includes a Postman collection with a request for every route of the ComicMaintainer API. Use it to try the API by hand. [API.md](API.md) is the reference for what each route takes and returns.

| File | What it is |
|---|---|
| `ComicMaintainer.postman_collection.json` | The requests, in 22 folders |
| `ComicMaintainer.postman_environment.json` | The variables the requests use |

## Set up

1. In Postman, choose **Import** and select both files.
2. Select the **ComicMaintainer - Local** environment.
3. Start the server. With Docker: `docker compose -f docker-compose.dotnet.yml up -d`. It listens on `http://localhost:5000`, the default `base_url`. With `dotnet run` from `src/ComicMaintainer.WebApi`, the address is `http://localhost:5072`, so change `base_url`. If the app is served under `BASE_PATH`, include it in `base_url`.
4. Set `admin_username` and `admin_password` in the environment to an account of yours. There is no default password.

## Sign in

Run **Auth > Login**. It saves the session token to `jwt_token`, and every other request sends that as `Authorization: Bearer`. The token lasts 24 hours by default.

For a new instance with no accounts:

1. Run **Auth > Check Setup Required**. It should return `setupRequired: true`.
2. Set `setup_token` to the code printed in the server log at startup (`docker logs comicmaintainer-dotnet`), or to `SETUP_TOKEN` if you set it.
3. Run **Auth > Setup Admin User**, then **Auth > Login**. `admin_password` must have at least 8 characters with an upper-case letter, a lower-case letter and a digit.

### The other credentials

| Credential | Used by | How to get it |
|---|---|---|
| `jwt_token` | Almost every request | **Auth > Login**. **Auth > Change Password** replaces it |
| `api_key` | Your own scripts; see below | **Auth > Create API Key**, which saves it. It needs `admin_password` |
| `stream_token` | **Events > Event Stream** | **Auth > Get Stream Token**. It is valid for 60 seconds and works once, so fetch a new one for each connection |
| `opds_username`, `opds_password` | The **OPDS** folder | Your username, with the account's password or its API key as the password. Under Authelia, only the API key works |

To send an API key instead of a session token, set the request's **Authorization** to **No Auth** and add the header `X-API-Key: {{api_key}}`. API keys never go in a URL.

The **OPDS** folder answers `404` until the catalog is on. Turn it on under **Settings > OPDS Catalog**, or with `OPDS_ENABLED=true`.

Requests that change the library need an account that is not `ReadOnly`, and the administration folders need an administrator. [API.md](API.md#access-levels) lists the access level of every route.

## Variables

Some requests need an id from an earlier response. Set these in the environment as you go.

| Variable | Used for | Where to get it |
|---|---|---|
| `base_url` | Every request | Your server's address, with `BASE_PATH` if you use one |
| `admin_username`, `admin_password` | Sign-in and setup | Your account |
| `setup_token` | First-run setup | The server log, or `SETUP_TOKEN` |
| `jwt_token`, `api_key`, `stream_token` | Credentials | Saved by the requests above |
| `opds_username`, `opds_password` | OPDS | Your account |
| `file_path` | Routes that take `filePath` or a list of files | `relative_path` from **Files > List Files** |
| `encoded_file_path` | Routes with `{encodedFilePath}` | `file_path` as URL-safe base64; see [API.md](API.md#conventions) |
| `encoded_folder_path` | **Library Browsing > Get Folder Files** | A folder path, encoded the same way |
| `series_id` | Series routes and OPDS series | `id` from **Library Browsing > Get Series** |
| `series_title` | Metadata and image routes, Suwayomi and OPDS search | A series title |
| `series_key` | **Series Images > Get** and **Clear** | `series_id` from **Metadata > Get Series Record** |
| `series_alias` | **Metadata > Remove Alias** | An alias you added |
| `job_id` | **Batch Jobs** | `job_id` from a job you started |
| `scheduled_job_key` | **Scheduled Jobs** | `jobKey` from **List Scheduled Jobs** |
| `backup_name` | **Backups** | `name` from **List Backups** |
| `device_id`, `subscription_id`, `delivery_id`, `build_id` | **Email** | `id` or `build_id` from the matching list |
| `error_fingerprint` | **Error Reports** | `fingerprint` from **List Reports** |
| `content_id` | **Reader State** | An identifier you choose |
| `notification_target` | **Notifications** | `ntfy`, `gotify`, `discord`, `webhook`, `komga` or `kavita` |
| `suwayomi_series_id`, `suwayomi_source_id` | **Suwayomi** | `id` from **Search Library** and **Get Sources** |
| `opds_issue_id`, `opds_page_number` | **OPDS** | An id from a feed; page numbers start at 0 |

## Folders

| Folder | Contents |
|---|---|
| Auth | Sign-in, setup, password change, API keys, stream tokens |
| Files | One page of files, counts, tags, processing a file, read state, stale cleanup |
| Library Browsing | Series, facets, folders, the overview rows |
| Folder Combine | Finding, previewing and combining folders |
| Batch Jobs | Starting, following and cancelling jobs |
| Processed Status | Clearing processed flags so files run again |
| Comic Reader | Pages, covers and progress |
| Reader State | Per-user progress, preferences and sessions |
| Metadata | Provider search, refresh and series records |
| Series Images | Cached covers, uploads and provider images |
| Email | E-reader devices, sending, condensed books and history |
| Suwayomi | Searching, adding and downloading |
| Scheduled Jobs | Listing, changing and running scheduled jobs |
| Settings | Every setting, and the save-everything `PUT /api/settings` |
| Notifications | Test notifications |
| Backups | Creating, restoring and downloading database backups |
| Error Reports | The local error record and test reports |
| Diagnostics and Logs | The diagnostics snapshot and log files |
| Application | Version, watcher, processing history, preferences, health probes, PWA files |
| Events | The server-sent event stream |
| OPDS | The OPDS catalog, with HTTP Basic |
| Deprecated Aliases | Older routes that still work |

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Connection refused | Check that the server is running and `base_url` has the right port |
| `401` | `jwt_token` is empty or expired, or the session ended. Run **Auth > Login** again |
| `403` | The account's role does not allow this. See [access levels](API.md#access-levels) |
| `404` on an OPDS request | The catalog is off |
| `409` from a settings request | An environment variable pins that setting. The response names it in `env_locked` |
| `429` | Too many sign-in attempts or requests. Wait for the time in `Retry-After` |

Every response carries an `X-Correlation-Id` header. To find a request in the server log, search for that value.
