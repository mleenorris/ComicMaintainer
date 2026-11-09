# ComicMaintainer REST API

This is the reference for people writing scripts and integrations. The web app and the Android app use the same API.

Related documents:

- [POSTMAN_API_COLLECTION.md](POSTMAN_API_COLLECTION.md): a ready-made Postman collection for these routes.
- [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md): every environment variable and what it does.
- [OPDS.md](OPDS.md): setting up comic reader apps. [NOTIFICATIONS.md](NOTIFICATIONS.md): notification targets.
- [AUTHELIA.md](AUTHELIA.md) and [REVERSE_PROXY.md](REVERSE_PROXY.md): running behind a proxy.

## Contents

- [Conventions](#conventions)
- [Authentication](#authentication)
- [Rate limits](#rate-limits)
- [Errors](#errors)
- [Paging](#paging)
- [Caching](#caching)
- [Request timeouts](#request-timeouts)
- [Authentication routes](#authentication-routes)
- [Files](#files)
- [Library browsing](#library-browsing)
- [Folder combine](#folder-combine)
- [Batch jobs](#batch-jobs)
- [Processed status](#processed-status)
- [Comic reader](#comic-reader)
- [Reader progress, preferences and sessions](#reader-progress-preferences-and-sessions)
- [Series metadata](#series-metadata)
- [Series images](#series-images)
- [Email delivery](#email-delivery)
- [Suwayomi](#suwayomi)
- [Scheduled jobs](#scheduled-jobs)
- [Settings](#settings)
- [Notifications](#notifications)
- [Database backups](#database-backups)
- [Error reports](#error-reports)
- [Diagnostics and logs](#diagnostics-and-logs)
- [Other application routes](#other-application-routes)
- [Event stream](#event-stream)
- [OPDS catalog](#opds-catalog)
- [Health, metrics and Swagger](#health-metrics-and-swagger)
- [Deprecated aliases](#deprecated-aliases)

## Conventions

**Base URL.** The default is `http://<host>:5000`. If `BASE_PATH` is set (for example `/comics`), put it in front of every path in this document: `https://example.com/comics/api/files`. The app accepts a request with or without that prefix, so a reverse proxy can either forward it or strip it. See [BASE_PATH](ENVIRONMENT_VARIABLES.md#reverse-proxy-and-base-path).

**JSON.** Bodies are JSON unless a route says otherwise. Property names in a request body are matched without regard to case. Response names are shown as the API sends them. Older routes use `snake_case` and newer ones use `camelCase`; a few send both.

**Dates.** Dates are ISO 8601 in UTC unless a field is described as Unix seconds. `modified`, `latest_modified` and the history `timestamp` are Unix seconds.

**Enums.** Enums are sent as numbers unless a route says otherwise. The reader's `ReaderMode` is `0` SinglePage, `1` Longstrip, `2` DoublePage, `3` DoublePageManga, `4` FitWidth, `5` FitHeight. `ReadingDirection` is `0` LeftToRight, `1` RightToLeft.

**Naming a file.** Routes name a file in one of two ways:

- `filePath` query parameter: the absolute path, URL-encoded. This is the value of `relative_path` in a file listing (despite its name, that field holds the absolute path).
- `{encodedFilePath}` path segment: the same absolute path as URL-safe base64 of its UTF-8 bytes (`+` becomes `-`, `/` becomes `_`, padding may be left out).

```bash
# Make an {encodedFilePath} from a path
printf '%s' '/watched_dir/Batman/Batman 001.cbz' | base64 -w0 | tr '+/' '-_' | tr -d '='
```

Single-file routes accept only paths inside the watched directory; anything else gets `400`. Batch job routes also accept the duplicates directory. Any request with a `filePath` query parameter that contains a `..` segment or points outside the watched, duplicates and config directories is refused with `400 {"error":"Invalid file path"}` before it reaches the route.

**Series identifiers.** Three different values are used:

| Placeholder | Where it comes from |
|---|---|
| `{seriesId}` | `id` from `GET /api/files/series` |
| `{seriesTitle}` | A series title, URL-encoded. Canonical title, folder title or alias all work |
| `{normalizedKey}` | `series_id` from a metadata record, or the key at the end of `external_image_url` |

**Long-running work.** Routes that start background work answer immediately with a job id. Follow it with `GET /api/jobs/{jobId}` or the [event stream](#event-stream).

## Authentication

Every route needs a signed-in caller unless its table says `Public`. There are four ways to sign in:

| Scheme | How to send it | Where it works |
|---|---|---|
| Session token (JWT) | `Authorization: Bearer <token>` from `POST /api/auth/login` | Everywhere except `/opds` |
| API key | `X-API-Key: cm_...` header | Everywhere except `/opds` (there it is sent as the Basic password) |
| Authelia | Forward-auth headers from a trusted proxy, when `AUTHELIA_ENABLED=true` | Everywhere except `/opds` |
| HTTP Basic | `Authorization: Basic ...` | Only under `/opds`. It is refused everywhere else |

A fifth credential, the single-use **stream token**, opens the [event stream](#event-stream) and nothing else. No other route accepts a credential in its URL. Session tokens and API keys in query strings are never read.

### Session tokens

```bash
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"your-password"}' | jq -r .token)

curl -H "Authorization: Bearer $TOKEN" 'http://localhost:5000/api/files?per_page=20'
```

A session token lasts 24 hours by default (`JwtSettings__ExpirationMinutes`). Signing out, changing the password, changing the account's role, or disabling or deleting the account ends it at once.

### API keys

Create a key with `POST /api/auth/api-key` or under **Settings > OPDS Catalog**. It starts with `cm_` and is shown only once; the server stores a hash.

- Send it in the `X-API-Key` header. Query strings and `Authorization: Bearer` are never accepted for keys.
- A key acts as the account that owns it, with that account's current roles. Each account has one key; creating a new one replaces the old one.
- Creating a key needs the current password (`currentPassword`), unless the session comes from Authelia.
- A request authenticated with an API key cannot create a key or change the password (`403`).
- Changing the password or signing out of all sessions revokes the key.

### Authelia

With `AUTHELIA_ENABLED=true`, the app trusts the `Remote-User`, `Remote-Groups`, `Remote-Email` and `Remote-Name` headers, but only on requests from the addresses in `AUTHELIA_TRUSTED_PROXIES`. Accounts are created on first sign-in. Session tokens and API keys keep working alongside it. See [AUTHELIA.md](AUTHELIA.md).

### HTTP Basic

HTTP Basic is accepted only on the routes under `/opds`. Send the username with either the account's password or its API key as the password. With Authelia enabled, only the API key works, because accounts Authelia created have no local password their owner knows. See [OPDS catalog](#opds-catalog).

### Access levels

The route tables below use these levels:

| Access | Who may call it |
|---|---|
| Public | Anyone, with no credentials |
| Signed in | Any signed-in account, including the `ReadOnly` role |
| Library write | Any signed-in account that is not in the `ReadOnly` role |
| Admin | Accounts in the `Admin` role. With Authelia enabled and `AUTHELIA_ADMIN_GROUPS` empty, every account that is not `ReadOnly` counts as an administrator |
| Admin role | Accounts in the `Admin` role, with no Authelia exception |
| OPDS Basic | HTTP Basic, under `/opds` only |

The roles are `Admin`, `User` and `ReadOnly`. A self-registered account gets `User`.

### Account protection

- Five wrong passwords in a row lock the account for 15 minutes. Sign-in, change-password, API-key creation and OPDS Basic passwords all count. The app has no way to unlock an account early.
- Every sign-in failure answers `401 {"error":"Invalid username or password"}`, including for disabled and locked accounts.
- Passwords published as examples with earlier versions are refused at sign-in, setup, registration and password change.

## Rate limits

| Limit | Applies to | Allowance |
|---|---|---|
| General | Every request | 1000 per minute per signed-in user, 100 per minute per anonymous client address |
| Sign-in attempts | `POST /api/auth/login`, `register`, `setup`, `change-password`, `api-key`, and OPDS Basic credential checks | 10 attempts per 5 minutes per client address, shared across all of them |

A refused request gets `429` with a `Retry-After` header (seconds) and `{"error":"Too many requests. Please try again in 3 minutes."}`. Behind a reverse proxy, set `TRUSTED_PROXIES`, or every client behind the proxy shares one allowance.

## Errors

Check the HTTP status first, then read the `error` field. Three body shapes are used.

**Problem document** (`application/problem+json`). Unexpected failures, settings refusals, notification tests and OPDS refusals use this shape, with an `error` string added:

```json
{
  "type": "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
  "title": "An unexpected error occurred.",
  "status": 500,
  "detail": "Error retrieving files",
  "instance": "/api/files",
  "correlationId": "0HN8V2JKQ3M1T:00000002",
  "error": "Error retrieving files"
}
```

Exception text is never returned, except when the app runs with `ASPNETCORE_ENVIRONMENT=Development`.

**Framework problem document.** A failed model bind (`400`), a route's bare `404`, and an unsupported content type (`415`) are problem documents with `title`, `status`, `traceId` and `correlationId`. A `400` from a failed bind also has an `errors` object. These have no `error` string.

**Simple object.** Most expected failures are `{"error":"..."}`. A few routes in `/api/files` and `/api/series-images` answer `400` with a plain text sentence instead. Treat any non-2xx status as a failure, whatever the body.

**Correlation id.** Every response carries an `X-Correlation-Id` header with the id used in the server log for that request. You may send your own `X-Correlation-Id` (letters, digits, `-`, `_` and `:`, up to 64 characters). Unexpected `500` and `504` bodies repeat it as `correlationId`. Quote it when reporting a problem.

| Status | Meaning |
|---|---|
| `200`, `202`, `204` | Success. `202` means the work continues in the background |
| `207` | `POST /api/files/tags` updated some files but not all; the body lists `failed` |
| `304` | Your cached copy is current (see [Caching](#caching)) |
| `400` | The request is invalid or refused; the file could not be processed; a settings value is out of range |
| `401` | No valid credentials: `{"error":"Unauthorized","message":"Authentication required"}` |
| `403` | Not allowed for this account: `{"error":"Forbidden","message":"Insufficient permissions"}`, or a route's own reason |
| `404` | Not found. An unknown path under `/api` answers `{"error":"Not Found","message":"The requested endpoint does not exist"}` |
| `409` | A conflict: a setting the environment pins, a cleanup that needs confirmation, a library that is busy, and similar |
| `412` | `GET /api/series-images/{normalizedKey}` with a failed `If-Match` or `If-Unmodified-Since` |
| `415` | The body has an unsupported content type |
| `422` | An archive is over the read limits; `error` names the entry and the limit |
| `429` | Rate limited |
| `499` | The client went away during a delete, a read-status change or a stale cleanup; nothing changed |
| `500` | An unexpected failure |
| `502` | A test email, test notification or test error report was refused by the remote service. `error` is a fixed reason |
| `503` | External metadata providers could not answer. See [Series metadata](#series-metadata) |
| `504` | The request ran past its [timeout](#request-timeouts): `{"error":"...","correlationId":"..."}` |

**Pinned settings.** A write that would change a setting an environment variable pins gets `409`, and nothing is saved. The body names what was refused:

```json
{
  "title": "The setting is set by the environment.",
  "status": 409,
  "detail": "Set by the environment and cannot be changed here: SMTP_HOST. Remove the variable and restart to edit the setting in the app.",
  "error": "Set by the environment and cannot be changed here: SMTP_HOST. Remove the variable and restart to edit the setting in the app.",
  "env_locked": [{ "field": "smtp_host", "variable": "SMTP_HOST" }]
}
```

Settings `400` responses are problem documents with `title: "The settings were not saved."` and the reason in `detail` and `error`.

## Paging

Paged routes use one of these forms:

| Form | Routes | Behaviour |
|---|---|---|
| `page`, `per_page` | `/api/files`, `/api/files/series`, `/api/files/series/{seriesId}/issues` | Defaults are page 1 and 100 per page. `per_page=-1` returns everything. A page past the end returns the last page |
| `offset`, `limit` | `/api/files/series` (when `limit` is above 0), `/api/files/folders` | Default `limit` is 100 for folders |
| `limit`, `offset` | `/api/processing-history` | Default 50. `limit` must be 1 to 1000, otherwise `400` |
| `limit`, `offset` | `/api/email/deliveries` | Default 50, limited to 1 to 500 |
| `offset`, `limit` | `/api/scheduled-jobs/metadata-audit/findings` | Default 200. A `limit` outside 1 to 1000 becomes 200 |
| `limit` only | `/api/error-reports` (1 to 200), `/api/metadata/search` (1 to 50), `/api/series-images/candidates` (1 to 25), `/api/suwayomi/series` (1 to 100) | Out-of-range values are moved into the range |
| `page` | `/api/suwayomi/sources/{sourceId}/series` (1 to 100), OPDS feeds | OPDS feeds hold 50 entries per page |

`/api/files` and `/api/files/series` report `page`, `total_pages` and `total_files` or `total_series`.

## Caching

Every `/api` and `/opds` response is sent `Cache-Control: no-store, private`, with these exceptions:

| Routes | Headers | Revalidation |
|---|---|---|
| `GET /api/files`, `/counts`, `/folders`, `/folders/files`, `/folders/{encodedPath}/files`, `/series`, `/series/facets`, `/series/{seriesId}/issues` | Weak `ETag`, `Cache-Control: private, no-cache` | `If-None-Match` gets `304`. The tag includes the caller's own read state |
| `GET /api/comicreader/page`, `/api/comicreader/cover`, OPDS pages and covers | Strong `ETag`, `Last-Modified`, `Cache-Control: private, max-age=3600` | `If-None-Match` gets `304`. These must not be stored in shared caches |
| OPDS file download | Strong `ETag`, `Last-Modified`, `Cache-Control: private, max-age=3600` | Range requests are supported |
| `GET /api/series-images/{normalizedKey}` | Strong `ETag`, `Last-Modified`, `Cache-Control: private, max-age=300` | `If-None-Match` or `If-Modified-Since` gets `304` |
| `GET /api/suwayomi/series/{seriesId}/thumbnail` | `Cache-Control: private, max-age=86400` | None |

## Request timeouts

| Limit | Routes |
|---|---|
| 30 seconds | Default |
| 10 minutes | `POST /api/email/send`, `send-series`, `condense-plan`, `condense-builds`; `POST /api/backups`, `POST /api/backups/{name}/restore`; `POST /api/metadata/refresh/{seriesTitle}`; `GET /api/suwayomi/sources/{sourceId}/series`; `POST /api/suwayomi/series/{seriesId}/add` |
| None | Downloads, `POST /api/email/condense-download`, `POST /api/backups/upload-restore`, `POST /api/suwayomi/download-missing`, `GET /api/events/stream`, OPDS file downloads |

A timed-out request gets `504`.

## Authentication routes

| Method | Path | Access | Purpose |
|---|---|---|---|
| `POST` | `/api/auth/login` | Public | Sign in with a password |
| `POST` | `/api/auth/register` | Public | Create an account, when allowed |
| `GET` | `/api/auth/setup-required` | Public | Whether first-run setup is available |
| `POST` | `/api/auth/setup` | Public | Create the first administrator |
| `GET` | `/api/auth/status` | Public | How the caller is signed in |
| `GET` | `/api/auth/user` | Signed in | The current account |
| `POST` | `/api/auth/change-password` | Signed in | Change the password |
| `POST` | `/api/auth/api-key` | Signed in | Create or replace the API key |
| `GET` | `/api/auth/api-key` | Signed in | Whether a key exists |
| `DELETE` | `/api/auth/api-key` | Signed in | Revoke the key |
| `POST` | `/api/auth/stream-token` | Signed in | Get a token for the event stream |
| `POST` | `/api/auth/logout` | Signed in | End this session, or all of them |

**`POST /api/auth/login`** takes `{"username":"...","password":"..."}` and returns `{"token":"..."}`.

**`POST /api/auth/register`** takes `{"username","password","email","fullName"}`. It works for anyone when `allow_registration` is on, and always for an administrator. Otherwise it answers `401`, or `403` for a signed-in non-administrator. Passwords need at least 8 characters with an upper-case letter, a lower-case letter and a digit.

**`GET /api/auth/setup-required`** returns `{"setupRequired":true}`.

**`POST /api/auth/setup`** takes `{"username","password","email","setupToken"}`. It works only while no account exists at all and Authelia is off. `setupToken` is the code printed in the server log at startup, or the value of `SETUP_TOKEN`. It answers `403` when setup is not available or the code is wrong.

**`GET /api/auth/status`** returns `{"autheliaEnabled","isAuthenticated","username","authMethod","requiresLogin"}`. `authMethod` is `jwt`, `authelia` or `api_key`.

**`GET /api/auth/user`** returns `{"username","userId","email","roles","authMethod","canModifyLibrary","canAdminister"}`.

**`POST /api/auth/change-password`** takes `{"currentPassword","newPassword"}` and returns `{"message","token"}`. Every session of the account ends and the API key is revoked; use the returned `token` from now on.

**`POST /api/auth/api-key`** takes `{"currentPassword":"..."}` (not needed under Authelia) and returns `{"apiKey":"cm_...","header":"X-API-Key"}`. A wrong password answers `403`.

**`GET /api/auth/api-key`** returns `{"hasApiKey","createdAt","lastUsedAt","header"}`. Dates are UTC with a `Z` suffix.

**`DELETE /api/auth/api-key`** answers `204`, or `404` if the account has no key.

**`POST /api/auth/stream-token`** returns `{"token":"...","expiresIn":60}`. See [Event stream](#event-stream).

**`POST /api/auth/logout`** takes an optional `{"allSessions":true}` and answers `204`. `allSessions` also revokes the API key.

## Files

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/files` | Signed in | One page of files |
| `GET` | `/api/files/counts` | Signed in | Library totals |
| `GET` | `/api/files/scan-unmarked` | Signed in | Counts of processed and unprocessed files |
| `GET` | `/api/files/metadata` | Signed in | A file's metadata, by `filePath` |
| `PUT` | `/api/files/metadata` | Library write | Update a file's tags, by `filePath` |
| `PATCH` | `/api/files/metadata` | Library write | Save a tag edit to the database and lock fields |
| `GET` | `/api/files/series-resolution` | Signed in | Explain which series name a file resolves to |
| `GET` | `/api/files/{encodedFilePath}/tags` | Signed in | A file's metadata |
| `PUT` | `/api/files/{encodedFilePath}/tags` | Library write | Update a file's tags |
| `POST` | `/api/files/tags` | Library write | Update tags on several files |
| `DELETE` | `/api/files/{encodedFilePath}/metadata` | Library write | Remove `ComicInfo.xml` from a file |
| `POST` | `/api/files/process` | Library write | Process one file now, by `filePath` |
| `POST` | `/api/files/{encodedFilePath}/process` | Library write | Process one file now |
| `POST` | `/api/files/{encodedFilePath}/rename` | Library write | Start a rename job for one file |
| `POST` | `/api/files/process-batch` | Library write | Start a process job for a list of files |
| `DELETE` | `/api/files/{encodedFilePath}` | Library write | Delete a file |
| `POST` | `/api/files/{encodedFilePath}/read` | Signed in | Mark a file read or unread for the caller |
| `POST` | `/api/files/read-batch` | Signed in | Mark several files |
| `POST` | `/api/files/read-all` | Signed in | Mark a whole library, series or folder |
| `POST` | `/api/files/cleanup-stale` | Library write | Remove rows for files no longer on disk |

**`GET /api/files`** takes `filter`, `search`, `page`, `per_page`, `sort` (`name`, `date` or `size`) and `direction` (`asc` or `desc`). It returns:

```json
{
  "files": [{
    "relative_path": "/watched_dir/Batman/Batman 001.cbz",
    "name": "Batman 001.cbz",
    "size": 123456,
    "modified": 1760000000,
    "processed": true,
    "renamed": true,
    "normalized": true,
    "duplicate": false,
    "read": false
  }],
  "page": 1,
  "total_pages": 3,
  "total_files": 250,
  "unmarked_count": 12
}
```

Filter values for the listing routes:

| Value | Meaning |
|---|---|
| (none) | Everything |
| `marked`, `unmarked` | Fully processed, or not |
| `duplicates` | Flagged as duplicates |
| `renamed`, `normalized` | That step is done |
| `read`, `unread` | Read state for the caller |
| `matched`, `unmatched` | Series with or without a provider match (series routes) |
| `missing` | Series with gaps in their issue numbers (series routes) |

**`GET /api/files/counts`** returns `{"total","processed","unprocessed","duplicates","combinableFolders"}`.

**`GET /api/files/scan-unmarked`** returns `{"total_count","unmarked_count","marked_count"}`.

**Metadata.** The metadata object is `{"series","title","issue","volume","publisher","year","summary","authors":[],"tags":[],"isUserEdited","userLockedFieldsMask"}`.

- `GET` metadata routes answer `404` when the file has none.
- `PUT /api/files/metadata?filePath=` and `PUT /api/files/{encodedFilePath}/tags` merge: a field left out or sent as `null` keeps its value. They answer an empty `200`.
- `PATCH /api/files/metadata?filePath=` takes `{"metadata":{...},"lockFields":["series"]}`. It records the edit in the database, asks the Metadata Backfill job to write it into the archive, and returns `202 {"filePath","metadataVersion"}`.
- `DELETE /api/files/{encodedFilePath}/metadata` returns `{"success":true,"cleared":1,"filePath":"..."}`. `filePath` is the file's path afterwards, because a CBR, RAR or ZIP becomes a CBZ. It fails with `400` for those formats while conversion is off.

**`POST /api/files/tags`** takes:

```json
{ "files": ["/watched_dir/a.cbz"], "metadata": { "series": "Batman" }, "fields": ["series"] }
```

`fields` is optional. When present, only the listed fields are applied, and a listed field with no value is removed. When absent, every field that has a value is applied and nothing is removed. Field names are `series`, `title`, `issue` (or `number`), `volume`, `publisher`, `year`, `summary` (or `description`), `authors` (or `writer`, `writers`) and `tags`. It answers `200`, `207 {"failed":[...]}` when only some files failed, and `400` when all failed, when a field name is unknown (`Unknown metadata field: x`), or when no field is given (`No metadata fields specified`).

**`GET /api/files/series-resolution?filePath=`** returns `{"filePath","actualSeries","resolvedSeries","winningStep","candidates","folderSeries","matchedCacheKey","appliedLanguage","explanation","matchesActual"}` and changes nothing.

**Processing a file.** `POST /api/files/process?filePath=` and `POST /api/files/{encodedFilePath}/process?forceReprocess=true` run in the request and answer an empty `200`. They answer `400` when the file could not be fully processed: a duplicate target, an unreadable archive, a failed write or rename, or no series name.

**`POST /api/files/{encodedFilePath}/rename`** takes `?forceReprocess=true` and returns `{"message","jobId"}`.

**`POST /api/files/process-batch`** takes a JSON array of paths and returns `{"jobId":"..."}`.

**`DELETE /api/files/{encodedFilePath}`** answers `200` or `404`, or `499` if the client gave up while waiting for the file's lock.

**Read state.** Read state is per user.

- `POST /api/files/{encodedFilePath}/read` takes `{"read":true}`.
- `POST /api/files/read-batch` takes `{"files":[...],"read":true}`. A failed save answers `500` and saves nothing.
- `POST /api/files/read-all` takes `{"read":true,"scope":"library"}`. `scope` is `library` (the default), `series` or `folder`, and the last two need `id` (series id, or folder key). It returns `{"success":true,"updated":250}`.

**`POST /api/files/cleanup-stale`** and **`POST /api/settings/cleanup-database`** share their behaviour. A row is removed only after two runs at least `stale_cleanup_missing_grace_hours` apart have found its file missing. The response is:

```json
{
  "success": false,
  "status": "ConfirmationRequired",
  "requiresConfirmation": true,
  "message": "...",
  "removedCount": 0,
  "trackedCount": 1000,
  "missingCount": 900,
  "removableCount": 900,
  "uncheckedCount": 0,
  "maxRemovalPercent": 10,
  "error": "..."
}
```

| Status | When |
|---|---|
| `200` | The cleanup ran |
| `409`, `requiresConfirmation: true` | The library holds no comics, the removal is over `maxRemovalPercent`, or files have not been missing long enough. Repeat with `?confirm=true` to remove everything missing now |
| `409`, `requiresConfirmation: false` | The watched directory or sentinel file is missing. Confirmation cannot override this |
| `499`, `500` | The request was cancelled, or the cleanup failed. Nothing was removed |

## Library browsing

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/files/series` | Signed in | One page of series |
| `GET` | `/api/files/series/facets` | Signed in | Genres and statuses in the library |
| `GET` | `/api/files/series/{seriesId}/issues` | Signed in | The issues of a series |
| `GET` | `/api/files/series/{seriesId}/folders` | Signed in | The folders a series is stored in |
| `GET` | `/api/files/folders` | Signed in | Folders with file counts |
| `GET` | `/api/files/folders/files` | Signed in | Files directly in the library root |
| `GET` | `/api/files/folders/{encodedPath}/files` | Signed in | Files in one folder |
| `GET` | `/api/overview` | Signed in | The home page rows |

**`GET /api/files/series`** takes `filter`, `search`, `page`, `per_page`, `sort`, `direction`, `genre`, `status`, `offset` and `limit`. `genre` matches regardless of case, and `genre=none` or `status=none` selects series with no such data yet. Status values are `ongoing`, `completed`, `hiatus`, `cancelled` and `upcoming`. Totals and paging apply to the filtered list. With `limit` above 0, `offset` and `limit` replace `page` and the response also has `offset` and `limit`.

```json
{
  "series": [{
    "id": "batman",
    "title": "Batman",
    "canonical_title": "Batman",
    "aliases": [],
    "metadata_source": "AniList",
    "issue_count": 12,
    "total_size": 123456,
    "latest_modified": 1760000000,
    "cover_file_path": "/watched_dir/Batman/Batman 001.cbz",
    "has_external_image": true,
    "external_image_url": "/api/series-images/batman",
    "lookup_status": "success",
    "last_lookup_utc": "2026-01-01T00:00:00Z",
    "synopsis": null,
    "genres": ["Action"],
    "publication_status": "ongoing",
    "site_url": null
  }],
  "page": 1,
  "total_pages": 1,
  "total_series": 1,
  "unmarked_count": 0
}
```

With `include_issues=true`, each entry also holds its `issues`. Leave it off for large libraries.

**`GET /api/files/series/facets`** returns `{"genres":[{"value","count"}],"statuses":[{"value","count"}],"series_without_genres","series_without_status"}`.

**`GET /api/files/series/{seriesId}/issues`** takes `filter`, `page` and `per_page`. It returns `{"id","title","canonical_title","aliases","metadata_source","cover_file_path","has_external_image","external_image_url","issue_count","total_size","issues","page","per_page","total_pages","synopsis","genres","publication_status","site_url"}`. Each issue is `{"file_path","file_name","title","issue","volume","publisher","year","size","modified","processed","renamed","normalized","duplicate","read"}`.

**`GET /api/files/series/{seriesId}/folders`** takes `filter` and returns `{"id","title","folders":[{"directory","file_count","total_size"}],"combine_group_key","suggested_destination_directory"}`.

**`GET /api/files/folders`** takes `filter`, `search`, `sort`, `direction`, `offset` and `limit`, and returns `{"folders","offset","limit","total_folders","unmarked_count"}`.

**`GET /api/files/folders/{encodedPath}/files`** and **`GET /api/files/folders/files`** take `filter`, `search`, `sort` and `direction`, and return `{"path","files"}`. Use the second form for the library root, whose key is empty.

**`GET /api/overview`** returns `{"continue_reading":[],"series_updates":[],"newly_added_series":[]}`. Cards are series summaries, and `continue_reading` cards add `resume_file_path`, `resume_page` and `last_read_utc`.

## Folder combine

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/files/combinable-folders` | Signed in | Groups of folders that look like one series |
| `POST` | `/api/files/combine-folders/preview` | Library write | Plan a combine and change nothing |
| `POST` | `/api/files/combine-folders` | Library write | Start a combine job |

**`GET /api/files/combinable-folders`** returns `{"groups":[...]}`. Each group has `groupKey`, `seriesName`, `volume`, `totalFileCount`, `suggestedDestinationDirectory`, `suggestionReason` and `folders`; each folder has `directory`, `fileCount`, `totalSize`, `newestFileAddedAt`, `oldestFileAddedAt`, `newestFileModifiedAt`, `sampleFileNames` and `isSuggested`.

Both `POST` routes take:

```json
{ "groupKey": "...", "destinationDirectory": "/watched_dir/Batman", "sourceDirectories": ["/watched_dir/Batman (2016)"] }
```

`groupKey` may also be `series:<seriesId>`, as returned by the series folders route.

The preview returns `{"destination","sources","moves":[{"sourcePath","destinationPath","conflict","skipped","reason"}],"foldersToRemove":[{"directory","disposableFiles","disposableFileCount"}],"foldersToKeep":[{"directory","reason","leftovers","leftoverCount"}]}`.

The combine answers `202` with `{"jobId","job_id","totalItems","total_items","destination","sources","skipped","skippedResults"}`. If there is nothing to move, it answers `202` with `{"destination","sources","moved":0,"skipped","failed":0,"totalItems":0,"results"}` and no job id. Source folders that still hold anything other than disposable files are kept and listed in the job's `reported_problems`.

## Batch jobs

| Method | Path | Access | Purpose |
|---|---|---|---|
| `POST` | `/api/jobs/{operation}-{scope}` | Library write | Start a batch job |
| `GET` | `/api/jobs` | Signed in | List jobs |
| `GET` | `/api/jobs/{jobId}` | Signed in | One job's status and results |
| `POST` | `/api/jobs/{jobId}/cancel` | Library write | Cancel a running job |
| `DELETE` | `/api/jobs/{jobId}` | Library write | Delete a job's record |
| `GET` | `/api/active-job` | Signed in | The running job, if any |

The operations and the scopes each one supports are:

| Operation | `selected` | `all` | `unmarked` |
|---|---|---|---|
| `process` | yes | yes | yes |
| `rename` | yes | yes | yes |
| `normalize` | yes | yes | yes |
| `remove-metadata` | yes | yes | no |
| `update-metadata` | yes | no | no |
| `delete` | yes | no | no |

So the concrete routes are:

- `POST /api/jobs/process-selected`, `POST /api/jobs/process-all`, `POST /api/jobs/process-unmarked`
- `POST /api/jobs/rename-selected`, `POST /api/jobs/rename-all`, `POST /api/jobs/rename-unmarked`
- `POST /api/jobs/normalize-selected`, `POST /api/jobs/normalize-all`, `POST /api/jobs/normalize-unmarked`
- `POST /api/jobs/remove-metadata-selected`, `POST /api/jobs/remove-metadata-all`
- `POST /api/jobs/update-metadata-selected`
- `POST /api/jobs/delete-selected`

Any other combination answers `404`.

The body is:

```json
{
  "files": ["/watched_dir/Batman/Batman 001.cbz"],
  "forceReprocess": false,
  "metadata": { "publisher": "DC" },
  "fields": ["publisher"]
}
```

- `files` is needed for the `selected` scope. Blank entries are dropped, and a list of only blanks is `400 {"error":"No files specified"}`.
- `metadata` and `fields` apply to `update-metadata`. `fields` works as in [`POST /api/files/tags`](#files). Unknown or empty fields are `400`.
- `forceReprocess` can also be sent as `?forceReprocess=true`.
- `selected` paths must be inside the watched or duplicates directory after resolving links, or the whole request is `400 {"error":"One or more file paths are outside the allowed directory"}`. `delete-selected` is stricter: the entry itself and its directory must be inside, with no `..` segment or trailing separator.
- The `all` and `unmarked` scopes leave out files flagged as duplicates. To act on a duplicate, name it with a `selected` route.
- The `all` and `unmarked` routes need no body. A non-JSON body is `415`, and malformed JSON is `400`.

The response carries both spellings:

```json
{ "job_id": "...", "total_items": 25, "jobId": "...", "totalItems": 25 }
```

When nothing qualifies, the id is the empty GUID and `total_items` is 0.

**`GET /api/jobs/{jobId}`** returns:

```json
{
  "job_id": "...",
  "status": "running",
  "total_items": 25,
  "processed_items": 10,
  "failed_items": 1,
  "current_file": "...",
  "start_time": "2026-01-01T00:00:00Z",
  "end_time": null,
  "results": [{ "file": "...", "success": false, "error": "the reason" }],
  "reported_problems": [{ "subject": "/watched_dir/Old", "message": "..." }]
}
```

`status` is `queued`, `running`, `completed`, `failed`, `cancelled` or `interrupted`. `interrupted` means the app stopped while the job was running.

**`GET /api/jobs`** returns `{"jobs":[...],"count":3}`, with the summary fields above. **`POST /api/jobs/{jobId}/cancel`** returns `{"success":true}`, or `404`. **`DELETE /api/jobs/{jobId}`** answers `204`, or `404`.

**`GET /api/active-job`** returns `{"active":false}` when nothing is running. Otherwise it returns the summary fields plus `job_title`. After a restart it also reports the most recent interrupted job, so a client can explain why work stopped.

## Processed status

These routes clear the renamed, normalized and processed flags, so the next run processes the files again.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `POST` | `/api/status/clear-selected` | Library write | Clear a list of files: `{"files":[...]}` |
| `POST` | `/api/status/clear-series/{seriesId}` | Library write | Clear every issue of a series |
| `POST` | `/api/status/clear-folder` | Library write | Clear a folder and everything below it: `{"folder":"..."}` |

They return `{"cleared","requested"}`, plus `seriesId` and `seriesTitle` for a series, or `folder` for a folder.

## Comic reader

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/comicreader/info` | Signed in | `{"fileName","filePath","pageCount"}` |
| `GET` | `/api/comicreader/pages` | Signed in | `{"pages":[...],"count"}`: page names |
| `GET` | `/api/comicreader/page` | Signed in | One page as an image; `page` starts at 1 |
| `GET` | `/api/comicreader/cover` | Signed in | The cover thumbnail |
| `GET` | `/api/comicreader/adjacent` | Signed in | Next or previous issue in the series |
| `GET` | `/api/comicreader/progress` | Signed in | `{"currentPage"}` |
| `POST` | `/api/comicreader/progress` | Signed in | Save the page: `?filePath=&page=` |
| `POST` | `/api/comicreader/mark-read` | Signed in | Mark as read: `?filePath=` |

All take `filePath`. **`adjacent`** also takes `direction` (`next`, the default, or `prev`) and returns `{"hasAdjacent","filePath","fileName"}`; it never leaves the series.

`POST /api/comicreader/progress` saves the resume page and the per-user progress together. It answers `500` if the write fails; retry it.

`info`, `pages`, `page` and `cover` answer `422 {"error":"..."}` for an archive over the read limits. The error names the entry and the limit: more than 20,000 entries, a page over 100 MiB, or a page over 16 MiB that expands more than 100:1.

## Reader progress, preferences and sessions

These hold reading state per user, under a `contentId` you choose.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/reader/progress/{contentId}` | Signed in | Saved progress, or `404` |
| `PUT` | `/api/reader/progress/{contentId}` | Signed in | Save progress |
| `GET` | `/api/reader/preferences` | Signed in | Reader defaults |
| `PUT` | `/api/reader/preferences` | Signed in | Save reader defaults |
| `POST` | `/api/reader/session/start` | Signed in | Start a reading session |
| `POST` | `/api/reader/session/end` | Signed in | End one of the caller's sessions |

`PUT /api/reader/progress/{contentId}` takes `{"currentPage","totalPages","readerMode","readingDirection"}` and answers `204`. The saved record is `{"userId","contentId","currentPage","totalPages","percentComplete","lastReadAt","completedAt","lastReaderMode","readingDirection"}`.

Preferences are `{"defaultReaderMode","readingDirection","tapZonesEnabled","autoHideChrome","fitPreference"}`.

`POST /api/reader/session/start` takes `{"contentId","startPage","incognito"}`. `POST /api/reader/session/end` takes `{"sessionId","endPage"}`. Both return the session. Another user's session answers `404`, the same as an unknown id. An incognito session never changes the durable progress.

## Series metadata

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/metadata/providers` | Signed in | Health of each provider |
| `GET` | `/api/metadata/search` | Signed in | Search providers |
| `POST` | `/api/metadata/refresh-all` | Library write | Queue a refresh of every series |
| `POST` | `/api/metadata/match-unmatched` | Library write | Queue a lookup for unmatched series |
| `POST` | `/api/metadata/refresh-selected` | Library write | Queue a refresh of listed series |
| `POST` | `/api/metadata/refresh/folder` | Library write | Queue a refresh for one series card |
| `POST` | `/api/metadata/refresh/{seriesTitle}` | Library write | Refresh one series |
| `GET` | `/api/metadata/refresh/job/{jobId}` | Signed in | A refresh job's status |
| `GET` | `/api/metadata/series/{seriesTitle}` | Signed in | The cached record |
| `POST` | `/api/metadata/series/{seriesTitle}/apply-match` | Library write | Adopt a search result |
| `DELETE` | `/api/metadata/series/{seriesTitle}/external` | Library write | Clear provider data |
| `PUT` | `/api/metadata/series/{seriesTitle}/aliases` | Library write | Replace user aliases: `{"aliases":[...]}` |
| `DELETE` | `/api/metadata/series/{seriesTitle}/aliases/{alias}` | Library write | Remove one user alias |
| `PUT` | `/api/metadata/series/{seriesTitle}/preferred-language` | Library write | `{"language":"en"}`; `null` clears |
| `PUT` | `/api/metadata/series/{seriesTitle}/name` | Library write | Pin the shown name: `{"name":"..."}`; `null` reverts to automatic |

**`GET /api/metadata/providers`** returns `{"providers":[{"name","enabled","configured","reachable","status_message","last_error","last_success_utc","last_failure_utc","success_count","failure_count","rate_limited","rate_limited_until_utc"}]}`.

**Refresh jobs.** The three queueing routes and `refresh/folder` return `{"jobId","totalSeries"}`. `match-unmatched` returns a null `jobId` when nothing is unmatched. `refresh-selected` takes `{"series":[...]}`. `refresh/folder` takes `{"seriesId":"...","titles":[...]}` (either is enough) and also returns `titles`.

**`POST /api/metadata/refresh/{seriesTitle}`** waits for the lookup and returns the record. With `?queue=true` it answers `202 {"jobId","totalSeries":1}` instead.

**`GET /api/metadata/refresh/job/{jobId}`** returns `{"jobId","status","series","totalSeries","processedSeries","successes","notFound","failures","currentSeries","startTime","endTime","recentResults":[{"seriesTitle","status","source","timestampUtc","error"}]}`. Unlike other enums, `status` is a name such as `Running`. A provider outage counts in `failures`, with the reason in `recentResults[].error`. Refresh jobs only retag series whose match or title changed.

**`GET /api/metadata/search`** takes `query` and `limit`. Results from all providers are interleaved, then sorted by `match_score`:

```json
{
  "query": "batman",
  "results": [{
    "canonical_title": "Batman",
    "aliases": [],
    "source": "AniList",
    "source_id": "12345",
    "image_url": "https://...",
    "thumbnail_url": "https://...",
    "synopsis": "...",
    "site_url": "https://...",
    "genres": ["Action"],
    "publication_status": "ongoing",
    "total_chapters": null,
    "origin": "US",
    "match_score": 100,
    "localized_titles": [{ "title": "Batman", "language": "en" }]
  }]
}
```

**`POST .../apply-match`** takes `canonicalTitle` (required), `aliases`, `source`, `sourceId`, `imageUrl`, `thumbnailUrl`, `synopsis`, `siteUrl`, `genres`, `publicationStatus`, `totalChapters`, `origin` and `localizedTitles`. Send back what search returned. Oversized values are cut or skipped rather than refused.

**The cached record** has `series_id`, `canonical_title`, `aliases`, `user_aliases`, `series_name`, `is_user_selected_name`, `resolved_series_name`, `source`, `source_id`, `last_lookup_utc`, `lookup_status`, `remote_image_url`, `local_image_file`, `image_content_type`, `image_downloaded_utc`, `image_status`, `preferred_language`, `localized_titles`, `synopsis`, `site_url`, `genres`, `publication_status`, `total_chapters`, `origin`, `metadata_version`, `has_image` and `is_user_image`.

**Provider outages.** When nothing was found and a provider could not answer, `POST /api/metadata/refresh/{seriesTitle}` and `GET /api/metadata/search` answer:

```json
{ "error": "MangaDex did not answer in time", "providers": ["MangaDex"], "retry_after_seconds": 30 }
```

with status `503`. The text is fixed wording built from the provider name and the kind of failure. Records that were already matched are left alone.

## Series images

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/series-images/candidates` | Signed in | Search providers for cover images |
| `GET` | `/api/series-images/{normalizedKey}` | Signed in | The cached image |
| `DELETE` | `/api/series-images/{normalizedKey}` | Library write | Clear the cached image |
| `PUT` | `/api/series-images/{seriesTitle}` | Library write | Upload an image |
| `POST` | `/api/series-images/{seriesTitle}/from-provider` | Library write | Download and use a candidate |
| `POST` | `/api/series-images/apply-current/all` | Library write | Start a job that re-applies every cover |
| `POST` | `/api/series-images/apply-current/selected` | Library write | The same for `{"series":[...]}` |

- **`candidates`** takes `query` and `limit` and returns `{"query","candidates":[{"source","canonical_title","image_url","thumbnail_url"}]}`. A provider outage is `503` with the fixed reason as plain text.
- **Upload** is `multipart/form-data` with one field named `file`, up to 20 MiB. It returns the metadata record.
- **`from-provider`** takes `{"imageUrl","source"}`. The URL must be public; private addresses are refused with `400`. It answers `409` when a user-uploaded image is in place.
- **`apply-current`** returns `{"job_id","total_items","totalSeries","skippedSeries"}`. The id is the empty GUID when no series has a cover to apply.

## Email delivery

Devices, subscriptions and deliveries are private to the user who owns them. Another user's item answers `404`, the same as one that does not exist. An administrator can act on everyone's by id, and listings include everyone's with `?all=true`.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/email/status` | Signed in | `{"configured","from_address","max_attachment_mb"}` |
| `GET` | `/api/email/devices` | Signed in | `{"devices":[...]}` |
| `POST` | `/api/email/devices` | Library write | Add a device |
| `PUT` | `/api/email/devices/{deviceId}` | Library write | Update a device |
| `DELETE` | `/api/email/devices/{deviceId}` | Library write | Delete a device |
| `POST` | `/api/email/devices/{deviceId}/test` | Library write | Send a test email |
| `POST` | `/api/email/send` | Library write | Queue files for delivery |
| `POST` | `/api/email/send-series` | Library write | Queue every issue of a series |
| `POST` | `/api/email/condense-plan` | Library write | Describe the books a condense would make |
| `POST` | `/api/email/condense-download` | Library write | Build one book and stream it |
| `POST` | `/api/email/condense-builds` | Library write | Build a book in the background |
| `GET` | `/api/email/condense-builds` | Signed in | The caller's builds |
| `GET` | `/api/email/condense-builds/{buildId}` | Signed in | One build's progress |
| `GET` | `/api/email/condense-builds/{buildId}/download` | Signed in | Download a finished book |
| `POST` | `/api/email/condense-builds/{buildId}/cancel` | Library write | Stop a build |
| `DELETE` | `/api/email/condense-builds/{buildId}` | Library write | Forget a build and its file |
| `GET` | `/api/email/subscriptions` | Signed in | `{"subscriptions":[...]}`; optional `series` |
| `PUT` | `/api/email/subscriptions` | Library write | Create or update a subscription |
| `DELETE` | `/api/email/subscriptions/{subscriptionId}` | Library write | Remove a subscription |
| `GET` | `/api/email/deliveries` | Signed in | One page of delivery history |
| `POST` | `/api/email/deliveries/{deliveryId}/cancel` | Library write | Cancel one delivery |
| `POST` | `/api/email/deliveries/cancel-pending` | Library write | Cancel everything not yet sent |

**Devices** take `{"name","emailAddress","deliveryFormat"}`, where `deliveryFormat` is `original`, `epub` or `azw3`. A device is `{"id","name","emailAddress","deliveryFormat","createdAt","updatedAt","ownerUserId"}`. Saving an address the same user already has answers `409`.

**`POST /api/email/devices/{deviceId}/test`** answers `200 {"message":"Test email sent"}`. It answers `400` when email delivery cannot be used yet (`error` says why), and `502 {"error":"..."}` when the mail server refuses. The `error` is a fixed reason, such as "The mail server rejected the login. Check the SMTP username and password in Settings."

**`POST /api/email/send`** takes:

```json
{
  "files": ["/watched_dir/a.cbz"],
  "deviceId": 1,
  "deliveryFormat": "epub",
  "skipAlreadyDelivered": true,
  "condenseMode": "none",
  "issuesPerBook": null
}
```

`condenseMode` is `none` (default), `count` or `all`. `count` needs `issuesPerBook` of at least 2. A book can hold up to 500 issues and about 2 GiB. It returns `{"queued":[...],"skipped":{}}`.

**`POST /api/email/send-series`** takes the same with `seriesId` in place of `files`, and returns `{"series","queued","skipped"}`. A series over 1000 issues is `400`.

**Condensing.** `condense-plan`, `condense-download` and `condense-builds` take `{"files","seriesId","condenseMode","issuesPerBook","bookIndex","deliveryFormat","deviceId","skipAlreadyDelivered"}`. `seriesId` wins over `files`, and `deliveryFormat` is `epub` or `azw3`.

`condense-plan` returns `{"mode","total_issues","issues_per_book","max_attachment_bytes","max_book_bytes","can_email","oversized_book_count","books":[{"display_name","issue_count","estimated_bytes","exceeds_attachment_limit","exceeds_book_size_limit"}],"skipped"}`.

`condense-builds` answers `202` with a build object: `{"build_id","status","display_name","issue_count","created_at","started_at","completed_at","error","file_size_bytes","expires_at","delivery_format","progress":{"phase","completed_pages","total_pages","completed_issues","total_issues","current_issue","pass","total_passes","percentage"}}`. `status` is `queued`, `running`, `completed`, `failed` or `cancelled`. Builds are private to the user who started them. A download that is not ready, or a cancel of a finished build, is `409`.

**`PUT /api/email/subscriptions`** takes `{"seriesTitle","deviceId","deliveryFormat","enabled"}`. `deliveryFormat` may also be `device`, to inherit the device's format.

**`GET /api/email/deliveries`** takes `limit`, `offset` and `all`, and returns `{"deliveries","total","offset","limit","pending_count","rate_limited_until"}`. A delivery has `id`, `filePath`, `fileName`, `deviceId`, `deviceName`, `deviceEmail`, `deliveryFormat`, `status` (`pending`, `sent`, `failed` or `cancelled`), `source` (`manual` or `auto`), `errorMessage`, `createdAt`, `sentAt`, `issueCount`, `attemptCount`, `nextAttemptAt` and `ownerUserId`.

**Cancelling a delivery** answers `200 {"cancelled":true,"delivery"}`. It answers `202 {"cancelled":false,"stopping":true,"delivery"}` while it is being prepared, and `409` when it is already sending or finished. **`cancel-pending`** takes `all` and returns `{"cancelled","stopping","sending"}`.

## Suwayomi

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/suwayomi/status` | Signed in | `{"enabled","configured","connected","serverName","version","error"}` |
| `GET` | `/api/suwayomi/series` | Signed in | Search the Suwayomi library: `query`, `limit` |
| `GET` | `/api/suwayomi/series/{seriesId}/chapters` | Signed in | `{"chapters":[{"id","name","chapterNumber","isDownloaded"}]}` |
| `GET` | `/api/suwayomi/series/{seriesId}/thumbnail` | Signed in | The series' cover |
| `GET` | `/api/suwayomi/sources` | Signed in | `{"sources":[{"id","name","lang","displayName"}]}` |
| `GET` | `/api/suwayomi/sources/{sourceId}/series` | Signed in | Search a source: `query`, `page` |
| `POST` | `/api/suwayomi/series/{seriesId}/add` | Library write | Add a series and queue what the library lacks |
| `POST` | `/api/suwayomi/download-missing` | Library write | Queue missing issues |

Most failures are `400 {"error":"..."}` with a fixed reason. A thumbnail that cannot be read is `404`.

**`GET /api/suwayomi/series`** returns `{"series":[{"id","title","inLibrary","source","thumbnailUrl"}]}`. **`GET /api/suwayomi/sources/{sourceId}/series`** returns `{"series","hasNextPage"}`; the search term is limited to 200 characters.

**`POST /api/suwayomi/download-missing`** takes `{"seriesTitle","alternateTitles":[],"suwayomiSeriesId":null,"issueNumbers":[1,2,3]}`. It returns `{"series","queuedChapters","unmatchedIssueNumbers","alreadyDownloadedIssueNumbers","discoveryFailures":[{"target","error"}],"cutShortSearches":[{"target","reason"}]}`. `cutShortSearches` lists searches that stopped at a limit; they are not failures.

## Scheduled jobs

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/scheduled-jobs` | Admin | List jobs |
| `GET` | `/api/scheduled-jobs/{jobKey}` | Admin | One job |
| `PUT` | `/api/scheduled-jobs/{jobKey}` | Admin | Change its schedule and options |
| `POST` | `/api/scheduled-jobs/{jobKey}/run` | Admin | Run it now |
| `GET` | `/api/scheduled-jobs/metadata-audit/findings` | Admin | Findings from the metadata audit |

Job keys are `database-backup`, `file-naming-audit`, `library-scan`, `metadata-audit`, `metadata-backfill`, `missing-issues-check`, `series-language-audit` and `suwayomi-download-import`.

A job is `{"jobKey","displayName","description","enabled","intervalMinutes","lastRunUtc","nextRunUtc","lastDurationMs","lastStatus","lastMessage","optionsJson"}`. `PUT` takes `{"enabled","intervalMinutes","optionsJson"}`; `intervalMinutes` must be positive when enabled. `run` answers `202 {"jobKey","queued"}`.

The findings route takes `type`, `offset` and `limit`, and returns `{"total","offset","limit","findings":[{"id","filePath","findingType","expectedSeries","actualSeries","actualIssue","details","detectedAtUtc"}]}`.

## Settings

Any signed-in account can read settings. Every write needs Admin. Writes use `PUT`.

| Method | Path | Access | Body or response |
|---|---|---|---|
| `GET` | `/api/settings` | Signed in | Everything, see below |
| `PUT` | `/api/settings` | Admin | Save any groups in one write, see below |
| `GET` | `/api/settings/filename-format` | Signed in | `{"format","default"}` |
| `PUT` | `/api/settings/filename-format` | Admin | `{"format":"{series} - Chapter {issue}"}` |
| `GET` | `/api/settings/issue-number-padding` | Signed in | `{"padding"}` |
| `PUT` | `/api/settings/issue-number-padding` | Admin | `{"padding":4}` (0 to 10) |
| `GET` | `/api/settings/log-max-bytes` | Signed in | `{"maxMB"}` |
| `PUT` | `/api/settings/log-max-bytes` | Admin | `{"maxMB":10}` (up to 2047). Needs a restart |
| `GET` | `/api/settings/watcher-enable-rename` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/watcher-enable-rename` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/watcher-enable-normalize` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/watcher-enable-normalize` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/convert-rar-to-cbz-on-write` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/convert-rar-to-cbz-on-write` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/write-cover-to-first-archive` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/write-cover-to-first-archive` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/write-cover-to-series-folder` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/write-cover-to-series-folder` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/download-external-series-images` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/download-external-series-images` | Admin | `{"enabled":true}` |
| `GET` | `/api/settings/series-image-limits` | Signed in | `{"maxMB","maxDownloadMB","maxDimension"}` |
| `PUT` | `/api/settings/series-image-limits` | Admin | Same fields, all above 0 |
| `GET` | `/api/settings/database-cleanup-interval-hours` | Signed in | `{"hours"}` |
| `PUT` | `/api/settings/database-cleanup-interval-hours` | Admin | `{"hours":12}`; 0 runs only after a start |
| `GET` | `/api/settings/database-cleanup` | Signed in | All cleanup settings |
| `PUT` | `/api/settings/database-cleanup` | Admin | See below |
| `GET` | `/api/settings/default-library-view` | Signed in | `{"view"}` |
| `PUT` | `/api/settings/default-library-view` | Admin | `{"view":"series"}` (`files` or `series`) |
| `GET` | `/api/settings/allow-registration` | Signed in | `{"enabled"}` |
| `PUT` | `/api/settings/allow-registration` | Admin | `{"enabled":false}` |
| `GET` | `/api/settings/default-preferred-language` | Signed in | `{"language"}` |
| `PUT` | `/api/settings/default-preferred-language` | Admin | `{"language":"en"}` (`en`, `ja`, `ko`, `zh`, or `null`) |
| `PUT` | `/api/settings/external-series-metadata` | Admin | See below |
| `PUT` | `/api/settings/email` | Admin | See below |
| `PUT` | `/api/settings/email-rate-limit` | Admin | See below |
| `PUT` | `/api/settings/suwayomi` | Admin | See below |
| `PUT` | `/api/settings/error-reporting` | Admin | See below |
| `POST` | `/api/settings/cleanup-database` | Admin | See [`POST /api/files/cleanup-stale`](#files) |
| `POST` | `/api/settings/reset` | Admin | Delete all tracked files and history |
| `POST` | `/api/settings/restart` | Admin role | Stop the app so its supervisor restarts it |

A successful write returns `{"message":"..."}`, and the new value is in effect when it returns. `POST /api/settings/reset` cancels active jobs and returns `{"success":true,"message"}`.

**`GET /api/settings`** returns these fields, plus `env_locked`. Secrets are never returned; the `*_set` fields say whether one is stored.

- Files: `filename_format`, `issue_number_padding`, `watcher_enable_rename`, `watcher_enable_normalize`, `convert_rar_to_cbz_on_write`, `log_max_bytes`.
- Cleanup: `database_cleanup_interval_hours`, `stale_cleanup_max_removal_percent`, `stale_cleanup_missing_grace_hours`, `stale_cleanup_sentinel_file`, `processing_history_retention_days`, `processing_history_max_entries`.
- Metadata: `enable_external_series_metadata`, `comicvine_api_key_set`, `comicvine_base_url`, `enable_mangadex_metadata`, `mangadex_base_url`, `enable_anilist_metadata`, `anilist_base_url`, `fill_comicinfo_from_providers`.
- Suwayomi: `enable_suwayomi`, `suwayomi_base_url`, `suwayomi_username`, `suwayomi_password_set`, `suwayomi_downloads_directory`, `suwayomi_import_directory`.
- Library: `default_library_view`, `allow_registration`, `default_preferred_language`, `write_cover_to_first_archive`, `write_cover_to_series_folder`, `download_external_series_images`, `series_image_max_bytes`, `series_image_max_download_bytes`, `series_image_max_dimension`.
- Email: `smtp_host`, `smtp_port`, `smtp_username`, `smtp_password_set`, `smtp_use_ssl`, `smtp_allow_insecure`, `email_from_address`, `email_from_name`, `email_max_attachment_mb`, `email_max_sends_per_hour`, `email_max_sends_per_day`, `email_min_seconds_between_sends`.
- Error reporting: `error_reporting_enabled`, `error_reporting_github_owner`, `error_reporting_github_repo`, `error_reporting_github_token_set`, `error_reporting_assignee`, `error_reporting_max_issues_per_day`, `error_reporting_dedupe_window_hours`, `error_reporting_comment_on_recurrence`.
- Notifications: for each of `ntfy`, `gotify`, `discord`, `webhook`, `komga` and `kavita`, `notify_<target>_enabled` and `notify_<target>_events`, plus the target's `notify_ntfy_url`, `notify_ntfy_topic`, `notify_ntfy_token_set`, `notify_gotify_url`, `notify_gotify_token_set`, `notify_discord_webhook_url_set`, `notify_webhook_url_set`, `notify_komga_url`, `notify_komga_api_key_set`, `notify_komga_library_id`, `notify_kavita_url`, `notify_kavita_api_key_set`, `notify_kavita_library_id`. `notify_ntfy_topic` is an empty string for anyone who is not an administrator.
- OPDS: `opds_enabled`.
- `env_locked`: one `{"field","variable"}` entry for each field an environment variable pins.

**`PUT /api/settings`** saves everything in one write. On a `400` or `409`, nothing changes. Every group and field is optional.

```json
{
  "filenameFormat": "{series} - Chapter {issue}",
  "logMaxMB": 10,
  "issueNumberPadding": 4,
  "databaseCleanup": {
    "intervalHours": 12,
    "staleCleanupMaxRemovalPercent": 10,
    "staleCleanupMissingGraceHours": 6,
    "staleCleanupSentinelFile": "",
    "processingHistoryRetentionDays": 90,
    "processingHistoryMaxEntries": 50000
  },
  "externalSeriesMetadata": {
    "enabled": true,
    "comicVineApiKey": null,
    "comicVineBaseUrl": null,
    "enableMangaDex": true,
    "mangaDexBaseUrl": null,
    "enableAniList": true,
    "aniListBaseUrl": null,
    "fillComicInfoFromProviders": true
  },
  "seriesImageLimits": { "maxMB": 5, "maxDownloadMB": 25, "maxDimension": 2048 },
  "email": {
    "smtpHost": "smtp.example.com",
    "smtpPort": 587,
    "smtpUsername": "user",
    "smtpPassword": null,
    "smtpUseSsl": false,
    "smtpAllowInsecure": false,
    "emailFromAddress": "comics@example.com",
    "emailFromName": "ComicMaintainer",
    "emailMaxAttachmentMb": 25
  },
  "emailRateLimit": { "maxSendsPerHour": 0, "maxSendsPerDay": 0, "minSecondsBetweenSends": 0 },
  "suwayomi": {
    "enabled": false,
    "baseUrl": "http://suwayomi:4567",
    "username": "",
    "password": null,
    "downloadsDirectory": "",
    "importDirectory": ""
  },
  "errorReporting": {
    "enabled": false,
    "gitHubToken": null,
    "assignee": "",
    "maxIssuesPerDay": 10,
    "dedupeWindowHours": 24,
    "commentOnRecurrence": true
  },
  "notifications": {
    "ntfy": { "enabled": true, "url": "https://ntfy.sh", "token": null, "topic": "my-topic", "events": "new-chapters,job-failed" },
    "komga": { "enabled": false, "url": "http://komga:25600", "token": null, "libraryId": "", "events": "new-chapters,library-changed" }
  },
  "opds": { "enabled": false }
}
```

The rules are:

- `null` or a left-out field keeps the current value. An empty string clears a text field.
- A blank base URL keeps the stored URL. Base URLs can't be cleared.
- Ranges: `logMaxMB` 1 to 2047; `issueNumberPadding` 0 to 10; `staleCleanupMaxRemovalPercent` 0 to 100; `staleCleanupMissingGraceHours` 0 to 720; `processingHistoryRetentionDays` 0 to 3650; `processingHistoryMaxEntries` 0 to 10,000,000; `smtpPort` 1 to 65535; `emailMaxAttachmentMb` 1 to 200; `maxSendsPerHour` 0 to 10,000; `maxSendsPerDay` 0 to 100,000; `minSecondsBetweenSends` 0 to 86,400; `maxIssuesPerDay` 0 to 100; `dedupeWindowHours` 1 to 720. `maxDownloadMB` must not be below `maxMB`.

**Group routes.** `PUT /api/settings/database-cleanup` takes the fields of the `databaseCleanup` group, and a field left out keeps its value. `PUT /api/settings/external-series-metadata`, `email`, `email-rate-limit`, `suwayomi` and `error-reporting` take the fields of the matching group, with one difference: a field left out is read as blank, off or its default, and saved. These are kept when left out instead: secrets (`comicVineApiKey`, `smtpPassword`, `password`, `gitHubToken`), base URLs, and the Suwayomi `downloadsDirectory` and `importDirectory`. An empty string clears a secret or a directory. `fillComicInfoFromProviders` is only available through `PUT /api/settings`.

**Pinned settings.** Environment variables can pin a setting. See [Errors](#errors) and [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md).

- A value equal to the pinned one is accepted, whatever the field's limits.
- Any other value gets `409`.
- A field left out never touches a pinned setting.
- A pinned secret is never compared: any write to it gets `409`.

## Notifications

| Method | Path | Access | Purpose |
|---|---|---|---|
| `POST` | `/api/notifications/{target}/test` | Admin | Send a test using the saved settings |

`{target}` is `ntfy`, `gotify`, `discord`, `webhook`, `komga` or `kavita`. On Komga and Kavita, this requests a real library scan.

| Status | Meaning |
|---|---|
| `200` | `{"message":"Test sent to ntfy."}` |
| `400` | The target's settings are incomplete |
| `404` | Unknown target |
| `409` | A scan was requested while the library is busy |
| `502` | The target refused or could not be reached; `error` holds its reason |

Failures are problem documents with an `error` string. Targets are configured with the `notifications` group of `PUT /api/settings`. Each target takes `enabled`, `url`, `token`, `topic`, `libraryId` and `events`; the fields a target has no use for are ignored. For Discord and the webhook, `url` is the secret webhook URL. `null` keeps a value, and an empty string clears it. Events are:

- ntfy, Gotify, Discord and webhook: `new-chapters`, `suwayomi-import-failed`, `job-failed`, `job-findings`.
- Komga and Kavita: `new-chapters`, `library-changed`.

See [NOTIFICATIONS.md](NOTIFICATIONS.md) for delivery behaviour.

## Database backups

All backup routes are Admin only, because a backup holds password hashes and stored credentials.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/backups` | Admin | `{"backups":[{"name","size_bytes","created_utc"}],"restore_pending"}` |
| `POST` | `/api/backups` | Admin | Create a backup now |
| `GET` | `/api/backups/{name}/download` | Admin | Download a backup |
| `DELETE` | `/api/backups/{name}` | Admin | Delete a backup: `{"deleted":"..."}` |
| `POST` | `/api/backups/{name}/restore` | Admin | Validate a backup and stage it |
| `POST` | `/api/backups/upload-restore` | Admin | Upload a database (field `file`, up to 2 GiB) and stage it |
| `DELETE` | `/api/backups/restore-pending` | Admin | Discard a staged restore: `{"cancelled":true}` |

A restore takes a safety backup first and applies at the next restart. It returns `{"source","safety_backup":{"name","size_bytes","created_utc"},"restart_required":true,"message"}`. It answers `400` for a file that is not a valid backup, and `409` when it cannot be staged.

## Error reports

These routes show the local record of [automated error reporting](ENVIRONMENT_VARIABLES.md#error-reporting). All need Admin, except `POST /api/client-errors`.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/error-reports` | Admin | Latest failures: `limit`, `state` |
| `GET` | `/api/error-reports/{fingerprint}` | Admin | One failure with its stack trace |
| `POST` | `/api/error-reports/{fingerprint}/mute` | Admin | Count it but never file it |
| `POST` | `/api/error-reports/{fingerprint}/unmute` | Admin | Stop muting it |
| `POST` | `/api/error-reports/test` | Admin | File a harmless test report |
| `POST` | `/api/client-errors` | Signed in | Report a browser error |

`state` is `new`, `reported`, `muted` or `failed`. **`POST /api/error-reports/test`** answers `200 {"message","outcome","fingerprint"}`. It answers `400` when reporting is off, no token is set, the daily cap is reached or the report is muted, and `502` when GitHub could not be reached or refused the token.

**`POST /api/client-errors`** takes `{"name","message","stack","source","url","kind"}`, where `kind` is `Error` or `UnhandledRejection`. It always answers `202` for a well-formed body.

## Diagnostics and logs

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/diagnostics` | Admin | A snapshot of the running instance |
| `GET` | `/api/logs` | Admin | A log file's last lines |
| `GET` | `/api/logs/files` | Admin | Log files of one type |

**`GET /api/diagnostics`** returns `{"generatedAtUtc","correlationId","application","runtime","health","watcher","library","jobs","storage","logs"}`. A section that cannot be read reports its own `error`.

**`GET /api/logs`** takes `type` (`app`, `watcher`, `metadata`, `auth` or `debug`, the default), `lines` (default 500, at most 10,000, 0 means the maximum) and `filename`. It returns `{"content","total_lines","shown_lines","filename"}`, newest entries first.

**`GET /api/logs/files`** takes `type` and returns `{"files":[{"filename","last_modified","size_mb"}],"count"}`.

## Other application routes

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/version` | Public | `{"version","platform":".NET"}` |
| `GET` | `/api/watcher` | Signed in | `{"running","enabled"}` |
| `GET` | `/api/processing-history` | Signed in | Processing history: `limit`, `offset` |
| `GET` | `/api/preferences` | Signed in | The caller's preferences |
| `PUT` | `/api/preferences` | Signed in | Save the caller's preferences |
| `GET` | `/sw.js` | Public | The service worker |
| `GET` | `/manifest.json` | Public | The PWA manifest |

**`GET /api/watcher`**: the watcher runs while rename or normalize is enabled in settings; this route only reports it.

**`GET /api/processing-history`** returns `{"history":[...],"total"}`. An entry has `timestamp` (Unix seconds), `filepath`, `operation_type`, `success`, `error_message`, and `before_`/`after_` values for `filename`, `title`, `series`, `issue`, `publisher`, `year` and `volume`.

**Preferences** are `{"theme","perPage","readingMode","libraryViewMode","filterMode","sortMode","sortDirection"}`, all optional. `theme` is `light` or `dark`, `readingMode` is `manga` or `webcomic`, `libraryViewMode` is `files` or `series`, `sortMode` is `name`, `date` or `size`, and `perPage` is 1 to 1000. `filterMode` takes the filter values above, plus `all`. The response also has `filenameFormat`, `issueNumberPadding` and `watcherEnabled`. `theme` stays `null` until the user chooses one.

## Event stream

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/api/events/stream` | Signed in | Server-sent events |

Browsers' `EventSource` cannot send headers, so fetch a stream token first:

```bash
ST=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/auth/stream-token | jq -r .token)
curl -N "http://localhost:5000/api/events/stream?access_token=$ST"
```

A stream token is valid for 60 seconds, once, and only on this route. Fetch a new one for every connection. The route also accepts a session token in the `Authorization` header, an API key and Authelia headers.

Each message is `data: {"type":"...","data":{...}}`:

| `type` | `data` |
|---|---|
| `job_updated` | `{"job_id","status","progress":{"processed","total","success","errors","percentage"}}` |
| `file_processed` | `{"filename","success","error"}` |
| `watcher_status` | `{"running","enabled"}` |
| `file_list_updated` | `{}` |

Every 30 seconds the server sends a named heartbeat, `event: heartbeat` with `data: {}`. A stream opened with a token or API key ends at the first heartbeat after the session or key ends.

## OPDS catalog

The catalog is off until `opds_enabled` is on. While it is off, every route under `/opds` answers `404`, even with no credentials. All routes use HTTP Basic. See [OPDS.md](OPDS.md) for app setup.

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/opds/v1.2` | OPDS Basic | The root feed |
| `GET` | `/opds/v1.2/catalog` | OPDS Basic | The root feed |
| `GET` | `/opds/v1.2/continue-reading` | OPDS Basic | The caller's Continue Reading row |
| `GET` | `/opds/v1.2/series-updates` | OPDS Basic | Series with new issues |
| `GET` | `/opds/v1.2/newly-added` | OPDS Basic | Series added recently |
| `GET` | `/opds/v1.2/series` | OPDS Basic | All series: `page` |
| `GET` | `/opds/v1.2/series/{seriesId}` | OPDS Basic | One series' issues: `page` |
| `GET` | `/opds/v1.2/search` | OPDS Basic | Search: `q` (up to 200 characters), `page` |
| `GET` | `/opds/v1.2/opensearch.xml` | OPDS Basic | OpenSearch description |
| `GET` | `/opds/v1.2/issues/{id}/cover` | OPDS Basic | A cover image |
| `GET` | `/opds/v1.2/issues/{id}/pages/{pageNumber}` | OPDS Basic | One page for page streaming; `pageNumber` starts at 0 |
| `GET` | `/opds/v1.2/issues/{id}/file` | OPDS Basic | The original file, with range support |
| `HEAD` | `/opds/v1.2/issues/{id}/file` | OPDS Basic | The file's headers |

- Links in the feeds are path-absolute and include `BASE_PATH`.
- `{id}` is an opaque number from the feed. Paths are never exposed.
- Only page requests record reading progress. Reaching the last page marks the issue read.
- Refusals are problem documents. A missing or wrong credential is `401` with `WWW-Authenticate: Basic realm="ComicMaintainer OPDS", charset="UTF-8"`. Too many attempts is `429` with `Retry-After`. An archive over the read limits is `422`.

## Health, metrics and Swagger

| Method | Path | Access | Purpose |
|---|---|---|---|
| `GET` | `/health` | Public | Liveness: `200 Healthy` while the process serves requests |
| `GET` | `/health/ready` | Public | Readiness: also checks the database; `503 Unhealthy` when it cannot be reached |
| `GET` | `/metrics` | Public, or token | Prometheus metrics, only when `TELEMETRY__PROMETHEUSENABLED=true`. If `TELEMETRY__METRICSTOKEN` is set, send `Authorization: Bearer <token>` |
| `GET` | `/swagger` | Public | Swagger UI, only in Development or with `API__ENABLESWAGGER=true`. The document is at `/swagger/v1/swagger.json` |

The health routes answer plain text.

## Deprecated aliases

These routes still work. New scripts should use the replacement.

| Method | Path | Access | Replacement |
|---|---|---|---|
| `POST` | `/api/rename-file` | Library write | `POST /api/files/{encodedFilePath}/rename`. Takes `?filePath=&forceReprocess=` |
| `DELETE` | `/api/delete-file` | Library write | `DELETE /api/files/{encodedFilePath}`. Takes `?filePath=` |
| `GET` | `/api/watcher/status` | Signed in | `GET /api/watcher` |
| `POST` | `/api/preferences` | Signed in | `PUT /api/preferences` |
