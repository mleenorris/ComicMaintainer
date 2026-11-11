# Environment Variables

Every environment variable ComicMaintainer reads, grouped by purpose. For Docker, set these in `docker-compose.dotnet.yml`, a `.env` file (see `.env.example`), or with `docker run -e`. For the API that sets the same values, see [API.md](API.md#settings).

**Secret** marks a value that grants access to something. Keep it out of source control. `_FILE` variants exist only for `JWT_SECRET`, `ADMIN_PASSWORD` and `SETUP_TOKEN`.

## How settings are resolved

A setting takes the first value found, highest priority first:

1. **A named variable from the tables below**, such as `SMTP_HOST`. It always wins and pins the setting.
2. **A value saved in the Settings page**, stored in `<CONFIG_DIR>/user-settings.json`.
3. **An `AppSettings__<Name>` variable** (or another `Section__Key` variable), such as `AppSettings__MaxWorkers=2`. It only sets a starting value. Once the setting is saved in the Settings page, the saved value wins.
4. `appsettings.json`, then the built-in default.

Changing any environment variable needs a restart, or for Docker, recreating the container.

### Pinning

A named variable with a usable value *pins* its setting:

- The Settings page shows the field as read-only and names the variable.
- `GET /api/settings` lists it in `env_locked` as `{"field","variable"}`.
- A write that would change it answers `409`, and nothing is saved. A write of the same value is accepted, except for secrets, where any write is refused.

To edit a pinned setting in the app, remove the variable and restart. In the tables, the **Pins** column gives the field name in `env_locked`, or "No field" when the Settings page has no control for it. `AppSettings__*` variables never pin.

### How values are read

| Type | Accepted | If not usable |
|---|---|---|
| Flag | Only `true` (any case) turns it on. Any other non-empty value turns it off, so `1` and `yes` mean **off** | An empty variable is ignored |
| Number | A whole number within the range shown | Ignored, as if not set, and the setting is not pinned |
| Text | Any non-empty value | An empty variable is ignored |

Some variables outside the settings table are read differently; their rows say so.

## Container and paths

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `WATCHED_DIR` | `/watched_dir` | Absolute path | The library folder the watcher scans | No field |
| `DUPLICATE_DIR` | `/duplicates` | Absolute path | Where duplicates are tracked | No field |
| `CONFIG_DIR` | `/Config` | Path | Holds the database, logs, `user-settings.json`, encryption keys, backups and caches. Wins over `AppSettings__ConfigDirectory` | No field |
| `PUID` | `99` | Number | The user the container runs as. Container entrypoint only | No field |
| `PGID` | `100` | Number | The group the container runs as. Container entrypoint only | No field |
| `FIX_LIBRARY_PERMISSIONS` | `false` | `true`, `1`, `yes`, `false`, `0`, `no` | When true, the entrypoint gives everything in `WATCHED_DIR` and `DUPLICATE_DIR` to `PUID:PGID` at every start. Run it once, then remove it. Container entrypoint only | No field |
| `ASPNETCORE_URLS` | `http://+:5000` (image) | ASP.NET Core URL list | Where the app listens. The image's health check assumes port 5000 | No field |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Production`, `Development` | `Development` turns on Swagger and puts exception text in `500` responses. Do not use it on a reachable server | No field |

## Reverse proxy and base path

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `BASE_PATH` | Empty (domain root) | A URL path such as `/comics` | The subfolder the app is served under. See below | No field |
| `TRUSTED_PROXIES` | Empty | Comma-separated IP addresses or CIDR ranges | Proxies whose `X-Forwarded-For` is believed for rate limiting and sign-in logs. Without it, all clients behind the proxy share one limit. No hostnames; an invalid entry stops startup | No field |
| `TRUSTED_PROXY_HOPS` | `1` | Whole number, 1 or more | How many right-most `X-Forwarded-For` entries to follow. Raise it only if every extra proxy appends its peer address. An invalid value stops startup | No field |
| `CORS_ALLOWED_ORIGINS` | `http://localhost:5000,https://localhost:5000` | Comma-separated origins | Origins allowed to make browser requests with credentials. Set it to your public origin | No field |

### BASE_PATH and your proxy

Set `BASE_PATH` only when the app is served in a subfolder, such as `https://example.com/comics`. If you set it while the app is served at the domain root, every link the app generates points at a folder that does not exist, and the app breaks.

- **Normalized.** `comics`, `/comics` and `/comics/` all mean `/comics`. It can have several segments, such as `/apps/comics`.
- **Allowed characters.** Each segment may use letters, digits, `.`, `_`, `~` and `-`. A segment of only `.` or `..` is refused. An invalid value is logged as an error and ignored, and the app is served at the root.
- **Your proxy can do either.** The app accepts `/comics/api/files` (prefix forwarded) and `/api/files` (prefix stripped). No extra proxy header is needed.
- **Links.** The web pages, `sw.js`, `manifest.json` and the OPDS feeds all use the prefix in their links.

See [REVERSE_PROXY.md](REVERSE_PROXY.md) for proxy examples.

## Accounts and security

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `JWT_SECRET` | Generated | Random text, at least 32 characters | **Secret.** Signs session tokens. If unset, the app uses `JwtSettings__Secret`, or generates a key and keeps it in `<CONFIG_DIR>/jwt-secret.key`. Placeholder values published with earlier versions are ignored | No field |
| `JWT_SECRET_FILE` | Unset | Path to a file | **Secret.** A file holding the secret, such as a Docker secret. Setting this and `JWT_SECRET` together stops startup | No field |
| `ADMIN_USERNAME` | `admin` (image) | Text | With `ADMIN_PASSWORD`, the administrator account created at startup | No field |
| `ADMIN_PASSWORD` | Unset | At least 8 characters, with an upper-case letter, a lower-case letter and a digit | **Secret.** The administrator's password. If it is unset, first-run setup in the web UI creates the first administrator. An existing account is left alone, unless it still has a published example password, which this replaces. Published example passwords are refused | No field |
| `ADMIN_PASSWORD_FILE` | Unset | Path to a file | **Secret.** A file holding the password. Setting this and `ADMIN_PASSWORD` together stops startup | No field |
| `ADMIN_EMAIL` | `admin@comicmaintainer.local` (image) | Email address | The administrator's address. Without the image's value, it is `<username>@comicmaintainer.local` | No field |
| `SETUP_TOKEN` | A random code per run, printed in the log while setup is available | Text | **Secret.** The one-time code first-run setup asks for | No field |
| `SETUP_TOKEN_FILE` | Unset | Path to a file | **Secret.** A file holding the code. Setting this and `SETUP_TOKEN` together stops startup | No field |

## Authelia

These are read at startup. An empty variable keeps the value from the `AutheliaSettings` section. See [AUTHELIA.md](AUTHELIA.md).

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `AUTHELIA_ENABLED` | `false` | `true` turns it on; anything else off | Trusts forward-auth headers | No field |
| `AUTHELIA_TRUSTED_PROXIES` | Empty | Comma-separated IP addresses or CIDR ranges | Proxies allowed to send the headers. Required when enabled; startup stops without it. No hostnames | No field |
| `AUTHELIA_URL` | Empty | URL | Where the app can reach Authelia. When set, Authelia's health is checked every 30 seconds and outages are logged. Works even if `AUTHELIA_ENABLED` is off | No field |
| `AUTHELIA_USER_HEADER` | `Remote-User` | Header name | The username header | No field |
| `AUTHELIA_EMAIL_HEADER` | `Remote-Email` | Header name | The email header | No field |
| `AUTHELIA_NAME_HEADER` | `Remote-Name` | Header name | The display name header | No field |
| `AUTHELIA_GROUPS_HEADER` | `Remote-Groups` | Header name | The groups header | No field |
| `AUTHELIA_DEFAULT_ROLE` | `User` | `Admin`, `User`, `ReadOnly` | The role of users Authelia signs in | No field |
| `AUTHELIA_ADMIN_GROUPS` | Empty | Comma-separated group names | Groups that get `Admin`. While empty, every user who is not `ReadOnly` counts as an administrator | No field |

## Stale-entry cleanup and history

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `STALE_CLEANUP_MAX_REMOVAL_PERCENT` | `10` | 0 to 100 | The largest share of tracked files one cleanup may remove without confirmation. 100 turns off the guard | `stale_cleanup_max_removal_percent` |
| `STALE_CLEANUP_MISSING_GRACE_HOURS` | `6` | 0 to 720 | A row is removed only after two cleanups at least this far apart found its file missing. Even 0 takes a second run | `stale_cleanup_missing_grace_hours` |
| `STALE_CLEANUP_SENTINEL_FILE` | Empty | Path, relative to `WATCHED_DIR` or absolute | A file that must exist before any cleanup runs. Create it on a network or removable library, so the cleanup can tell a mounted library from an empty mount point | `stale_cleanup_sentinel_file` |
| `PROCESSING_HISTORY_RETENTION_DAYS` | `90` | 0 to 3650 | History older than this is removed. 0 keeps it regardless of age | `processing_history_retention_days` |
| `PROCESSING_HISTORY_MAX_ENTRIES` | `50000` | 0 to 10000000 | Only the newest this many history rows are kept. 0 means no cap | `processing_history_max_entries` |

## External metadata

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `ENABLE_EXTERNAL_SERIES_METADATA` | `false` | Flag | Turns on ComicVine lookups, which also need `COMICVINE_API_KEY`. MangaDex and AniList have their own switches | `enable_external_series_metadata` |
| `COMICVINE_API_KEY` | Empty | Text | **Secret.** ComicVine API key | `comicvine_api_key_set` |
| `COMICVINE_BASE_URL` | `https://comicvine.gamespot.com/api` | URL | ComicVine API address | `comicvine_base_url` |
| `ENABLE_MANGADEX_METADATA` | `false` | Flag | Turns on MangaDex lookups | `enable_mangadex_metadata` |
| `MANGADEX_BASE_URL` | `https://api.mangadex.org` | URL | MangaDex API address | `mangadex_base_url` |
| `ENABLE_ANILIST_METADATA` | `false` | Flag | Turns on AniList lookups | `enable_anilist_metadata` |
| `ANILIST_BASE_URL` | `https://graphql.anilist.co` | URL | AniList API address | `anilist_base_url` |
| `FILL_COMICINFO_FROM_PROVIDERS` | `true` | Flag | When a file is normalized, fills its empty `Summary`, `Genre`, `Web`, `Count` and `Manga` fields from the series' provider match. See below | `fill_comicinfo_from_providers` |
| `MANGADEX_REQUESTS_PER_SECOND` | `4` | 1 or more | Client-side pace for MangaDex | No field |
| `ANILIST_REQUESTS_PER_MINUTE` | `28` | 1 or more | Client-side pace for AniList | No field |
| `COMICVINE_REQUESTS_PER_HOUR` | `180` | 1 or more | Hourly budget for ComicVine. Requests are also spaced at least 1 second apart. Once spent, ComicVine is skipped until the hour frees up | No field |
| `METADATA_PROVIDER_TIMEOUT_SECONDS` | `15` | 1 or more; treated as 1 to 30 | Time allowed for one provider request | No field |

**About the ComicInfo fill.** It runs as part of the normalize step, so it does nothing while normalize is off. A value already in a file, or a `Summary` locked by a user, is never changed. Existing files change only when they are next processed. Because filling writes to the file, a CBR, RAR or ZIP is converted to CBZ at that point, unless [conversion](#settings-without-a-named-variable) is off.

## Series images

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `DOWNLOAD_EXTERNAL_SERIES_IMAGES` | `true` | Flag | Downloads a series cover when metadata is refreshed | `download_external_series_images` |
| `SERIES_IMAGE_CACHE_DIR` | `<CONFIG_DIR>/series-images` | Path | Where series covers are stored | No field |
| `SERIES_IMAGE_MAX_BYTES` | `5242880` | 1 or more | Largest stored cover. A larger download is scaled down to fit | `series_image_max_bytes` |
| `SERIES_IMAGE_MAX_DOWNLOAD_BYTES` | `26214400` | 1 or more | Largest image that may be downloaded | `series_image_max_download_bytes` |
| `SERIES_IMAGE_MAX_DIMENSION` | `2048` | 1 or more | Largest width or height, in pixels, of a scaled-down cover | `series_image_max_dimension` |

## Suwayomi

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `ENABLE_SUWAYOMI` | `false` | Flag | Turns on the Suwayomi integration | `enable_suwayomi` |
| `SUWAYOMI_BASE_URL` | `http://localhost:4567` | URL | The Suwayomi server | `suwayomi_base_url` |
| `SUWAYOMI_USERNAME` | Empty | Text | Username, when the server uses basic auth | `suwayomi_username` |
| `SUWAYOMI_PASSWORD` | Empty | Text | **Secret.** Password for that username | `suwayomi_password_set` |
| `SUWAYOMI_DOWNLOADS_DIR` | Empty | Absolute path outside the watched, duplicates, config and temp folders | Where Suwayomi saves chapters, as this container sees it. Empty turns off the import | `suwayomi_downloads_directory` |
| `SUWAYOMI_IMPORT_DIR` | Empty (the watched folder) | Path relative to `WATCHED_DIR`, or absolute inside it | Where imported chapters go, one folder per series. A series that already has a folder uses that | `suwayomi_import_directory` |

## Email delivery

Email stays off until `SMTP_HOST` and a valid `EMAIL_FROM_ADDRESS` are both set. See [EMAIL_DELIVERY.md](EMAIL_DELIVERY.md).

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `SMTP_HOST` | Empty | Hostname | The mail server | `smtp_host` |
| `SMTP_PORT` | `587` | 1 to 65535 | The mail server's port | `smtp_port` |
| `SMTP_USERNAME` | Empty | Text | Username; empty sends without signing in | `smtp_username` |
| `SMTP_PASSWORD` | Empty | Text | **Secret.** Password or app password | `smtp_password_set` |
| `SMTP_USE_SSL` | `false` | Flag | Implicit TLS, usually port 465. When off, STARTTLS is required | `smtp_use_ssl` |
| `SMTP_ALLOW_INSECURE` | `false` | Flag | Allows plaintext when the server offers no STARTTLS. Only for a trusted local relay | `smtp_allow_insecure` |
| `EMAIL_FROM_ADDRESS` | Empty | Email address | The sender | `email_from_address` |
| `EMAIL_FROM_NAME` | `ComicMaintainer` | Text | The sender's name | `email_from_name` |
| `EMAIL_MAX_ATTACHMENT_MB` | `25` | 1 or more (the Settings page accepts up to 200) | Largest whole email, encoded. At 25, a book can be about 18 MB | `email_max_attachment_mb` |
| `EMAIL_MAX_SENDS_PER_HOUR` | `0` | 0 to 10000 | Most comic emails in any 60 minutes. 0 means no limit | `email_max_sends_per_hour` |
| `EMAIL_MAX_SENDS_PER_DAY` | `0` | 0 to 100000 | Most comic emails in any 24 hours. 0 means no limit | `email_max_sends_per_day` |
| `EMAIL_MIN_SECONDS_BETWEEN_SENDS` | `0` | 0 to 86400 | Minimum gap between comic emails | `email_min_seconds_between_sends` |

## Notifications

Each target does nothing until it is enabled. Event lists are comma-separated; unknown names are ignored. See [NOTIFICATIONS.md](NOTIFICATIONS.md) for delivery and retry behaviour.

- Default events for ntfy, Gotify, Discord and the webhook: `new-chapters,suwayomi-import-failed,job-failed,job-findings`.
- Default events for Komga and Kavita: `new-chapters,library-changed`. These two can only choose from those.

Secrets are never returned by the API, never logged, and masked in error reports.

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `NOTIFY_NTFY_ENABLED` | `false` | Flag | Sends to ntfy | `notify_ntfy_enabled` |
| `NOTIFY_NTFY_URL` | Empty | URL | The ntfy server | `notify_ntfy_url` |
| `NOTIFY_NTFY_TOPIC` | Empty | Up to 128 characters, no spaces | The topic. Treat it as sensitive: on a public server, knowing it is enough to subscribe | `notify_ntfy_topic` |
| `NOTIFY_NTFY_TOKEN` | Empty | Text | **Secret.** Optional access token | `notify_ntfy_token_set` |
| `NOTIFY_NTFY_EVENTS` | See above | Event list | Events sent to ntfy | `notify_ntfy_events` |
| `NOTIFY_GOTIFY_ENABLED` | `false` | Flag | Sends to Gotify | `notify_gotify_enabled` |
| `NOTIFY_GOTIFY_URL` | Empty | URL | The Gotify server | `notify_gotify_url` |
| `NOTIFY_GOTIFY_TOKEN` | Empty | Text | **Secret.** Application token | `notify_gotify_token_set` |
| `NOTIFY_GOTIFY_EVENTS` | See above | Event list | Events sent to Gotify | `notify_gotify_events` |
| `NOTIFY_DISCORD_ENABLED` | `false` | Flag | Sends to a Discord webhook | `notify_discord_enabled` |
| `NOTIFY_DISCORD_WEBHOOK_URL` | Empty | URL | **Secret.** The webhook URL holds a token | `notify_discord_webhook_url_set` |
| `NOTIFY_DISCORD_EVENTS` | See above | Event list | Events sent to Discord | `notify_discord_events` |
| `NOTIFY_WEBHOOK_ENABLED` | `false` | Flag | POSTs JSON to a webhook | `notify_webhook_enabled` |
| `NOTIFY_WEBHOOK_URL` | Empty | URL | **Secret.** Such URLs often hold a token | `notify_webhook_url_set` |
| `NOTIFY_WEBHOOK_EVENTS` | See above | Event list | Events sent to the webhook | `notify_webhook_events` |
| `NOTIFY_KOMGA_ENABLED` | `false` | Flag | Asks Komga to rescan | `notify_komga_enabled` |
| `NOTIFY_KOMGA_URL` | Empty | URL | The Komga server | `notify_komga_url` |
| `NOTIFY_KOMGA_API_KEY` | Empty | Text | **Secret.** API key of a Komga administrator | `notify_komga_api_key_set` |
| `NOTIFY_KOMGA_LIBRARY_ID` | Empty (every library) | Letters, digits, `-`, `_`, `.` | The library to scan | `notify_komga_library_id` |
| `NOTIFY_KOMGA_EVENTS` | See above | Event list | What makes Komga scan | `notify_komga_events` |
| `NOTIFY_KAVITA_ENABLED` | `false` | Flag | Asks Kavita to rescan | `notify_kavita_enabled` |
| `NOTIFY_KAVITA_URL` | Empty | URL | The Kavita server | `notify_kavita_url` |
| `NOTIFY_KAVITA_API_KEY` | Empty | Text | **Secret.** Kavita Auth Key | `notify_kavita_api_key_set` |
| `NOTIFY_KAVITA_LIBRARY_ID` | Empty (every library) | A number | The library to scan | `notify_kavita_library_id` |
| `NOTIFY_KAVITA_EVENTS` | See above | Event list | What makes Kavita scan | `notify_kavita_events` |

The limits on the URL, topic and library id are checked when saved through the Settings page or API. A value set by an environment variable is used as it is.

## OPDS catalog

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `OPDS_ENABLED` | `false` | Flag | Serves the OPDS catalog at `/opds/v1.2/` for comic reader apps. It adds HTTP Basic sign-in on those routes only. While off, every `/opds` route answers `404` | `opds_enabled` |

See [OPDS.md](OPDS.md).

## Error reporting

ComicMaintainer can file the errors it logs as GitHub issues, so its developers can fix them. It is off by default. Nothing leaves your server until you turn it on and supply a token.

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `ERROR_REPORTING_ENABLED` | `false` | Flag | Turns reporting on | `error_reporting_enabled` |
| `ERROR_REPORTING_GITHUB_TOKEN` | Empty | Text | **Secret.** A token that may create issues. Use a fine-grained token with `issues: write` on `mleenorris/ComicMaintainer` only. Without one, errors are recorded locally and nothing is sent | `error_reporting_github_token_set` |
| `ERROR_REPORTING_ASSIGNEE` | Empty | A GitHub login | Assigns new issues to this login. If GitHub rejects it, the issue is created unassigned | `error_reporting_assignee` |
| `ERROR_REPORTING_MAX_ISSUES_PER_DAY` | `10` | 0 or more (the Settings page accepts up to 100) | The most issues created in any 24 hours. 0 creates none. Errors past the cap are still counted locally | `error_reporting_max_issues_per_day` |
| `ERROR_REPORTING_DEDUPE_WINDOW_HOURS` | `24` | 1 or more (the Settings page accepts up to 720) | How long before a repeat of a filed error may comment on its issue again | `error_reporting_dedupe_window_hours` |
| `ERROR_REPORTING_COMMENT_ON_RECURRENCE` | `true` | Flag | Comments on the existing issue when an error repeats | `error_reporting_comment_on_recurrence` |

### Where reports go

Reports go to the issue tracker of [mleenorris/ComicMaintainer](https://github.com/mleenorris/ComicMaintainer/issues), which is **public**. This cannot be changed. Anyone can read what is filed there.

### What is reported

Any log entry at `Error` or `Fatal` level (`AppSettings__ErrorReportingMinimumLevel` can raise the minimum to `Fatal`), and uncaught errors in the web pages, except:

- Work cancelled because a client went away or the app is stopping.
- Failures marked as expected conditions, such as an unusable admin password setting.
- Failures of the reporting itself.

Each distinct error is filed once. A fingerprint of its type, top stack frames, message template and source identifies it, and a repeat only updates the local count. After the dedupe window, a repeat may add a comment.

### Exactly what is sent

**An issue contains:**

- Title: `[field error] <exception type> in <source class>`.
- Labels: `field-error`, `automated`, and `severity:error` or `severity:fatal`.
- A table with: level, exception type, source class, app version, .NET runtime, operating system description, first-seen time, occurrence count and correlation id.
- The log message and, if different, its template. Up to 4,000 and 2,048 characters.
- The exception message, up to 2,048 characters, and the stack trace, up to 40 frames and 8,000 characters.
- The fingerprint.

**A repeat comment contains:** the occurrence count, the time, the app version, the correlation id and the fingerprint.

**A browser error also contains:** the error's name and message, the page and script paths (no host or query string), its stack, and the browser's user agent.

### What is removed first

- Values of settings: the SMTP password, ComicVine key, GitHub token, and the notification tokens, API keys and webhook URLs. Values under 6 characters are not matched.
- Anything labelled as a secret, such as `password=...`, `token: ...` or `api_key=...`.
- `Bearer`, `Basic` and `Token` credentials, GitHub tokens, and user names and passwords in URLs.
- Email addresses.
- The folders of absolute paths. `/home/alice/My Comics/Series/Issue 1.cbz` becomes `<path>/Issue 1.cbz`.

### What is not removed

- **File names.** The last part of a path stays, so a comic's file name can appear.
- **Other text in a log message.** Series titles, usernames and similar text are not recognised as sensitive.
- The app version, runtime and operating system description.

To see what would be sent before sending anything, set `ERROR_REPORTING_ENABLED=true` with no token. Errors are then recorded locally only, and `GET /api/error-reports` shows each one with its redacted text. Once you add a token, `POST /api/error-reports/test` files one harmless issue to check it. See [API.md](API.md#error-reports).

## Monitoring and diagnostics

`TELEMETRY__*` and `API__*` are configuration keys, whose names ignore case.

| Variable | Default | Valid values | What it does | Pins |
|---|---|---|---|---|
| `TELEMETRY__PROMETHEUSENABLED` | `false` | `true`, `false` | Serves Prometheus metrics at `/metrics` | No field |
| `TELEMETRY__METRICSTOKEN` | Empty | Text | **Secret.** When set, `/metrics` needs `Authorization: Bearer <token>`. When empty it is open, so restrict it at your proxy | No field |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Unset | URL | When set, sends metrics and traces to an OpenTelemetry collector | No field |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | The exporter's default | `grpc`, `http/protobuf` | Standard OpenTelemetry setting, read by the exporter. The other standard `OTEL_EXPORTER_OTLP_*` variables work too | No field |
| `OTEL_SERVICE_NAME` | `ComicMaintainer` | Text | The service name on exported data | No field |
| `API__ENABLESWAGGER` | `false` | `true`, `false` | Serves Swagger UI at `/swagger` outside Development | No field |
| `EFCORE_QUERY_LOGGING` | Off | `1`, `true`, `yes` or `on` | Logs every SQL statement. Very noisy; for debugging only | No field |

## Settings without a named variable

These can be set with an `AppSettings__<Name>` variable. It only sets the starting value and never pins. Where the **Settings** column says yes, a value saved there wins over the variable.

| Variable | Default | Valid values | What it does | Settings |
|---|---|---|---|---|
| `AppSettings__ConvertRarToCbzOnWrite` | `true` | `true`, `false` | Writing tags to a CBR, RAR or ZIP saves it as `.cbz` and deletes the original. The library record, read status and progress move with the file. See below | Yes |
| `AppSettings__FilenameFormat` | `{series} - Chapter {issue}` | Non-blank text | The rename pattern | Yes |
| `AppSettings__IssueNumberPadding` | `4` | 0 to 10 | Digits the issue number is padded to | Yes |
| `AppSettings__WatcherEnableRename` | `true` | `true`, `false` | The watcher renames files | Yes |
| `AppSettings__WatcherEnableNormalize` | `true` | `true`, `false` | The watcher writes metadata to files | Yes |
| `AppSettings__WriteCoverToSeriesFolder` | `true` | `true`, `false` | Writes a `cover.<ext>` next to a series' files | Yes |
| `AppSettings__WriteCoverToFirstArchive` | `true` | `true`, `false` | Embeds the series cover in the first archive | Yes |
| `AppSettings__DatabaseCleanupIntervalHours` | `12` | 0 or more | Hours between stale-entry cleanups. 0 runs one only after start | Yes |
| `AppSettings__AllowRegistration` | `false` | `true`, `false` | Lets visitors create their own accounts | Yes |
| `AppSettings__DefaultPreferredLanguage` | Empty | `en`, `ja`, `ko`, `zh` | The language used for series names when a series has none | Yes |
| `AppSettings__LogMaxBytes` | `10485760` | Above 0 | Largest size of a log file; it stops growing at this size until the next day's file starts. Read at startup | Yes |
| `AppSettings__MaxWorkers` | `4` | 1 or more | Archives processed at once, across the watcher, API edits and every job. Read at startup | No |
| `AppSettings__TempFileDirectory` | `/Config/temp` | Path | Scratch space for generated books. Archive rewrites use a temporary file next to the archive instead | No |
| `AppSettings__WatcherFileStabilityDelaySeconds` | `30` | 0 or more | How long a file must stay unchanged before it is processed | No |
| `AppSettings__WatcherDirectoryScanDelaySeconds` | `2` | 0 or more | The wait before scanning a new folder | No |
| `AppSettings__WatcherInternalBufferSizeKB` | `64` | 4 to 64 | The file watcher's event buffer | No |
| `AppSettings__FileCoverCacheEnabled` | `true` | `true`, `false` | Caches file covers on disk | No |
| `AppSettings__FileCoverCacheDirectory` | `<CONFIG_DIR>/file-covers` | Path | Where file covers are cached | No |
| `AppSettings__FileCoverCacheMaxMegabytes` | `512` | 1 or more | The cover cache's soft size limit | No |
| `AppSettings__FileCoverThumbnailMaxWidth` | `480` | 0 or more | Cover width in pixels. 0 keeps the original bytes | No |
| `AppSettings__FileCoverThumbnailFormat` | `webp` | `webp`, `jpeg` | Cover encoding. Unknown values use `webp` | No |
| `AppSettings__FileCoverThumbnailQuality` | `80` | 1 to 100 | Cover encoder quality | No |
| `AppSettings__DatabaseBackupDirectory` | `<CONFIG_DIR>/backups` | Path | Where backups are written. Backups hold password hashes and stored credentials | No |
| `AppSettings__DatabaseBackupRetention` | `7` | 1 or more; below 1 keeps everything | How many backups to keep | No |
| `AppSettings__ErrorReportingMinimumLevel` | `Error` | `Error`, `Fatal` | The lowest log level that is reported | No |

**About CBR conversion.** The setting is under **Settings > Watcher** (*Convert CBR/RAR to CBZ when writing tags*), or `GET`/`PUT /api/settings/convert-rar-to-cbz-on-write`.

- **On (the default).** Writing tags to a CBR, RAR or ZIP replaces it with a new CBZ. A file symlink is never converted.
- **Off.** Those files are never rewritten. Tag edits are kept in the database and shown in the app, but other readers such as Komga or Kavita will not see them. A file that normalize would change stays unprocessed until the setting is on and the file is processed again.

## Other configuration sections

The same `Section__Key` form works for these. They never pin.

| Variable | Default | What it does |
|---|---|---|
| `JwtSettings__Secret` | Empty | **Secret.** The signing secret, used when `JWT_SECRET` is unset |
| `JwtSettings__Issuer` | `ComicMaintainer` | The issuer of session tokens |
| `JwtSettings__Audience` | `ComicMaintainerAPI` | The audience of session tokens |
| `JwtSettings__ExpirationMinutes` | `1440` | How long a session token lasts |
| `ReverseProxySettings__TrustedProxies` | Empty | Used when `TRUSTED_PROXIES` is unset |
| `ReverseProxySettings__ForwardedHopLimit` | `1` | Used when `TRUSTED_PROXY_HOPS` is unset |
| `CorsSettings__AllowedOrigins__0`, `__1`, ... | The two localhost origins | Used when `CORS_ALLOWED_ORIGINS` is unset |
| `AutheliaSettings__<Name>` | See [Authelia](#authelia) | Used when the matching `AUTHELIA_*` variable is unset or empty |
