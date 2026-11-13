# Changelog

All notable changes to ComicMaintainer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Changes are written here by hand under [Unreleased], in the pull request that makes them. Releases
no longer add entries of their own: each [GitHub
release](https://github.com/mleenorris/ComicMaintainer/releases) lists the pull requests merged
for it.

## [Unreleased]

### Changed

- The server and the Android app now run on .NET 11. Building from source needs the .NET 11 SDK.
- The Android app now needs Android 7.0 (API 24) or later, the oldest version .NET 11 supports. A phone
  on Android 6.0 can keep using the 3.0.0 app.
- With Authelia, each request no longer looks up the account in the database. The account is remembered for
  30 seconds, as signed-in sessions are, and a change made on the Users page applies at once. The per-request
  "Authenticating user via Authelia" lines in `auth.log` are now written to `debug.log` only.
- Log files are written in the background, at most a second after each line, instead of on every line.
  EF Core's debug messages, one for each database connection and transaction, are no longer written to
  `debug.log`.
- Library covers that are not cached yet are generated at most one per processor core at a time. JPEG pages
  are decoded at a reduced size for the thumbnail, which takes a fraction of the time and memory.
- In a series, a run of more than 12 missing issues is shown as one card for the whole range.
- When files change or a job finishes, the library refreshes the series already loaded in place: no
  spinner, no jump back to the top, and the cards are only redrawn if something they show changed. While
  another page is open, the library refreshes when you return to it.
- Back from a series returns to the same place in the library.
- Reader pages are cached on the device separately from library covers (150 pages), so reading a comic no
  longer pushes covers out of the cache. Series art is cached on the device too. A cover or page cached in
  the last 10 minutes is shown without asking the server again.

### Fixed

- On a phone, the library loaded every series one page after another as soon as it opened, instead of
  as you scrolled.
- A series with an issue numbered like a year or a date, such as #2024, no longer creates thousands of
  missing-issue cards. Numbers above 100,000 are not counted when looking for missing issues, the same
  limit the Missing Issues Check job uses.
- Renaming or moving a file no longer reads every user's whole reading progress table to move the
  progress with it.

## [3.0.0] - 2026-11-09

Upgrading an existing install? Start with the [3.0 release notes](docs/RELEASE_NOTES_V3.0.md).
They put the steps in order and explain what the upgrade does to your database and files.

### Breaking changes

Each item ends with what you need to do. Coming from a release before 2.0.413? Also read the
[2.0.413 breaking changes](#20413---2026-11-06): Authelia mode refuses to start without
`AUTHELIA_TRUSTED_PROXIES`, and `JWT_SECRET_FILE` and `ADMIN_PASSWORD_FILE` are now read.

#### Signing in and accounts

- **Everyone must sign in again.** Session tokens now carry claims that let them be revoked, and
  tokens issued by 2.x lack them, so they are refused. Logout, a password change, a role change
  and disabling or deleting an account now end sessions. **What to do:** sign in again after
  upgrading.
- **The published example passwords no longer work.** `Admin123!`, `YourStrongPassword123!` and
  `YourSecurePassword123!` are refused at startup seeding, setup, registration, password change
  and sign-in, even on an existing account. **What to do:** set `ADMIN_USERNAME` to the affected
  account and `ADMIN_PASSWORD` (or `ADMIN_PASSWORD_FILE`) to a password of your own, then restart;
  the password is replaced at startup. For the other affected accounts, repeat this, or sign in as
  an administrator and set new passwords on the Users page.
- **`ADMIN_PASSWORD` has no default any more.** `docker-compose.dotnet.yml` and `.env.example` no
  longer set it, so a new install without it goes through first-run setup instead of creating
  `admin` / `Admin123!`. **What to do:** to create an administrator from the environment, set
  `ADMIN_PASSWORD` yourself.
- **First-run setup needs a setup code.** `POST /api/auth/setup` requires the value of
  `SETUP_TOKEN` or `SETUP_TOKEN_FILE`. Without them a random code is written to the log at
  startup. Setup works only while no account exists at all, and never with Authelia. **What to
  do:** find the code with `docker logs`. Authelia deployments that used setup to create a local
  administrator must set `ADMIN_PASSWORD` instead.
- **Wrong passwords now lock accounts, and sign-in requests are rate limited.** Five wrong
  passwords in a row lock an account for 15 minutes, whether at sign-in, password change, API-key
  creation or when an administrator confirms a change on the Users page. The lockout ends on its
  own, or when an administrator sets a new password for the account. Sign-in, registration, setup,
  password change and API-key creation allow 10 requests per 5 minutes per client address, then
  answer 429 with a JSON error and `Retry-After`. Every sign-in failure now reads "Invalid username
  or password", including for disabled accounts. **What to do:** behind a reverse proxy, set
  `TRUSTED_PROXIES`, or all clients share one limit.
- **Old API keys are gone and new ones work differently.** The upgrade drops keys stored by
  earlier versions; they never authenticated anyone. Keys now go in the `X-API-Key` header only
  and act as the owning account with its current roles. Any role can create one. Creating one
  needs the account's current password (Authelia sessions are exempt), is rate limited and cannot
  be done with an API key. Changing the password or signing out of all sessions revokes the key,
  as do an administrator resetting the password and disabling the account. **What to do:** create
  a new key at Settings > OPDS Catalog or with `POST /api/auth/api-key`, and again after a
  password change.
- **`POST /api/auth/change-password` refuses API keys (403).** **What to do:** sign in to change a
  password.
- **Session tokens are no longer accepted in URLs, and `GET /api/auth/sse-token` is removed.**
  **What to do:** clients of `/api/events/stream` must call `POST /api/auth/stream-token` and pass
  the single-use, 60-second token as `?access_token=`. Stream tokens work nowhere else.
- **The SignalR hub `/hubs/progress` is removed**, and the Content Security Policy now allows
  same-origin connections only (`connect-src 'self'`). **What to do:** move integrations to the
  event stream `/api/events/stream`.
- **Logs are for administrators only.** `GET /api/logs` and `GET /api/logs/files` answer 403 to
  User and ReadOnly accounts, and the app hides View Logs from them. **What to do:** read logs
  with an administrator account.
- **Only administrators can create accounts, and "Allow visitors to register" is gone.** The web
  app has no sign-up page, so the setting at Settings > Access only opened `POST
  /api/auth/register` to anonymous callers. That route now requires an administrator, whatever was
  saved before: it answers 401 without credentials and 403 to a signed-in User or ReadOnly
  account. `GET` and `PUT /api/settings/allow-registration` are removed, `GET /api/settings` no
  longer returns `allow_registration`, and a stored `AllowRegistration` setting and
  `AppSettings__AllowRegistration` are ignored. The Access card, left holding only the default
  series-name language, is now called Series Language. **What to do:** create accounts as an
  administrator on the new Users page, or with `POST /api/users` (see Added); `POST
  /api/auth/register` with an administrator's session token or API key still works for scripts.
  Stop calling the removed routes, and remove the variable if you set it.

#### Deployment and the Docker image

- **A failed backup now stops the upgrade.** Before applying database migrations the app backs up
  the database. If that fails (full disk, unwritable folder), startup stops without migrating.
  **What to do:** fix the disk or permissions and restart.
- **The container no longer changes ownership of your library.** It only prepares `/Config` (and a
  custom `CONFIG_DIR`). An empty root-owned top folder is still given to `PUID`, not recursively,
  and a folder `PUID` cannot write to gets a warning in the log. **What to do:** set `PUID` and
  `PGID` to the folder owner, grant access on the host, or set `FIX_LIBRARY_PERMISSIONS=true` once
  and then remove it.
- **Library folders must allow creating files.** Archive rewrites now build a temporary file in
  the archive's own folder and rename it into place. A library with writable files in read-only
  folders now fails metadata writes. `TempFileDirectory` is no longer used for archive writes, and
  the `<file>.backup` files and cross-device copy are gone. **What to do:** make the library
  folders writable by `PUID`.
- **`/app` in the image stays owned by root, and the image no longer contains `unrar-free`,
  `p7zip-full` or apt's recommended packages.** **What to do:** write under `/Config` instead of
  `/app`, and add any tools you used inside the container to your own image. The app reads
  archives itself.
- **Series cover downloads ignore `HTTP_PROXY` and `HTTPS_PROXY`.** The address guard must make
  the connection itself to check where it connects. **What to do:** allow direct outbound HTTP(S)
  from the container, or those downloads fail.
- **`BASE_PATH` now takes effect.** It used to be accepted and ignored. An install that sets it
  but is served at the domain root now generates every URL under that prefix and breaks. A value
  that cannot be a URL path is logged as an error and ignored. **What to do:** remove `BASE_PATH`
  unless the app really is served under that subfolder.
- **`/manifest.json` must be reachable through your proxy.** `index.html` now links it with
  credentials instead of using an inline copy. **What to do:** let the browser fetch
  `/manifest.json`; a session cookie is sent with the request.
- **The Kubernetes example changed.** `docs/kubernetes-deployment.yaml` now needs a
  `comicmaintainer-secrets` Secret (`jwt-secret`, `admin-password`) and adds a duplicates volume
  claim. The old manifest ran the Python image. **What to do:** create both before applying it.

#### Comic files

- **Writing tags converts CBR, RAR and ZIP files to CBZ.** This is on by default. Any tag write
  (normalize, tag edit, batch update, remove metadata, backfill) to such a file saves it as
  `<name>.cbz` and deletes the original. The library record, read status and progress move with
  it. A file that is itself a symbolic link is never converted. **What to do:** to keep these
  formats, turn off Settings > Watcher > "Convert CBR/RAR to CBZ when writing tags" or set
  `AppSettings__ConvertRarToCbzOnWrite=false`.
- **With that setting off, those archives are never rewritten.** Tag edits stay in the database
  and appear in the app only. `DELETE /api/files/{path}/metadata` answers 400 for them. A file
  normalize would change stays unprocessed: normalize jobs count it as failed, and processing
  still renames it but reports failure. External readers such as Komga or Kavita do not see these
  edits, and the Metadata Backfill job keeps such files queued and reports them as kept in the
  database. **What to do:** after turning the setting on, process those files again.
- **Files already processed can be rewritten and converted.** New and on by default: the next time
  a file is processed or normalized, its empty Summary, Genre, Web, Count and Manga fields are
  filled from the series' provider match. That rewrites the archive once and, under the setting
  above, converts a CBR, RAR or ZIP to CBZ. Files that are already processed are only processed
  again by a forced reprocess or normalize, a retag after a metadata refresh, or a scheduled job
  you turned on. Nothing happens to them at the upgrade itself. **What to do:** to keep files as
  they are, set `FILL_COMICINFO_FROM_PROVIDERS=false` or untick Settings > External Series
  Metadata > "Fill empty ComicInfo fields from the matched provider". To keep the fill but never
  change a file's format, turn the conversion setting off; such files then stay unfilled.
- **Metadata writes now merge into `ComicInfo.xml`.** Every write keeps all existing elements.
  Tags are written as one comma-separated `<Tags>` (legacy `<Tag>` elements are folded into it)
  and authors as one comma-separated `<Writer>`. Normalize keeps a real chapter `<Title>`, and
  files are no longer counted as un-normalized because the title differs from `Chapter N`. The
  rename step no longer writes a recovered chapter number when normalize is off. A nested
  `ComicInfo.xml` is now honoured by tagging as well: a write replaces it with one at the archive
  root, and removing metadata drops every `ComicInfo.xml` at any depth. **What to do:** nothing,
  unless a tool of yours reads legacy `<Tag>` elements.
- **Startup cleans up after interrupted writes.** Every start deletes leftover `.tmp_<32
  hex>.comicinfo-write` files in the library and duplicates folders. Until the cleanup has
  completed once after upgrading, it also deletes `<archive>.cover.tmp` files and temporary
  `.tmp_<guid>.cbz` files in `TempFileDirectory`, and restores `<archive>.backup` over a missing
  or unreadable archive. A `.backup` next to an intact archive is only reported. Completion is
  recorded in `<ConfigDirectory>/archive-write-recovery-v3.done`. **What to do:** nothing; delete
  that file to run the cleanup again, and remove reported `.backup` files once you no longer need
  them.
- **File name parsing changed.** A marked number outside brackets wins. Otherwise a ` - ` part
  that is only a number is the issue, else the last standalone number of the first numbered part,
  ignoring brackets and volume tokens. For example, `Spider-Man 2099 012` gives 12 and `Series
  (2023)` gives none. **What to do:** expect a forced reprocess to rename files named by the old
  rules.
- **Archives over the safety limits are refused.** Reading refuses more than 20,000 entries, a
  page over 100 MiB uncompressed, or an entry over 16 MiB that expands more than 100:1, and the
  reader endpoints answer 422 with `{ error }`. Rewrites refuse an entry over 1 GiB, the same
  100:1 ratio, and output past 20 times the archive size (at least 2 GiB, at most 64 GiB),
  counting every volume of a set. RAR 5 archives that need a dictionary over 256 MiB or declare a
  comment over 1 MiB are refused. A refused file is left unchanged. A genuine near-blank
  uncompressed page, such as a BMP over 16 MiB, can trip the ratio check. **What to do:** repack
  such files as CBZ, with pages as JPEG, PNG or WebP.
- **Some archives that used to open no longer do.** This covers files named `.cbz`, `.cbr`, `.zip`
  or `.rar` that are really 7z, tar or gzip; ZIP entries compressed with anything other than
  Store, Deflate or Deflate64; encrypted ZIP entries; and a following part with no RAR signature
  (a raw split). Real multi-volume RAR sets still work. **What to do:** repack as CBZ with
  Deflate, remove the password, or rejoin raw-split files before adding them.
- **The tag editor no longer shows Month and Notes, which were never saved.** Its Writer field now
  shows and edits the file's authors. **What to do:** nothing.

#### Library and processing

- **Files that land while the app is stopped are now processed at the next start**: renamed,
  normalized and queued for automatic email. That happens only once the directory has been scanned
  to the end, so the first 3.0 start only adds files that arrived during the upgrade. Changing the
  watched directory, or stopping and starting the watcher, now rescans it, and a newly chosen
  directory is only added. If none of the tracked files are left under it, what is found is only
  added. **What to do:** to avoid catch-up, turn off watcher rename and normalize. Process
  anything that was only added from the file list.
- **The recovery rescan no longer retries every unprocessed file.** After a watcher error or
  buffer overflow it queues only untracked files and files still waiting. **What to do:** use
  Process Unmarked to retry failed files.
- **The first start re-checks unprocessed duplicates once.** If watcher processing is on, every
  unprocessed file flagged as a duplicate gets one automatic pass. Files wrongly flagged by the
  case-only and library-root bugs are renamed and unflagged; real duplicates are flagged again.
  That pass can write and convert those files. **What to do:** review the duplicate list before
  using Delete All Duplicates.
- **Case-only name differences are no longer duplicates.** On case-insensitive file systems
  (Windows, macOS, Docker Desktop, SMB) such files are renamed to the resolved casing. Comics
  directly in the watched or duplicates root no longer take the root folder's name as their
  series, and a loose file that names no series is neither tagged nor renamed. **What to do:**
  give loose files a series in `ComicInfo.xml` or in the file name.
- **Folder changes made outside the app are followed immediately.** Renaming, moving or deleting a
  folder updates or removes its tracked rows at once. **What to do:** move folders within the
  library if you want to keep read state; moving one out and back in loses it.
- **Processing reports more failures.** Duplicates, unreadable archives, failed metadata writes or
  renames, and files with no series are now failures. Process jobs count them in `FailedFiles`
  with a reason in `Errors`, and the Prometheus processed-file metric counts them as failures. Job
  errors and history show the specific reason instead of generic text such as "the archive could
  not be read". **What to do:** expect failures where jobs used to report success, and update
  anything that matches the old text.
- **All work shares one pool of `MaxWorkers` archive slots**, across the watcher, API metadata
  edits and every running job, and one archive is never processed by two of them at once. **What
  to do:** nothing; jobs running together may finish later.
- **Combine Folders no longer deletes a source folder that still holds anything else.** Only a
  top-level `ComicInfo.xml`, `cover.*` or `folder.*` images, OS metadata files (`.DS_Store`,
  `Thumbs.db`, `desktop.ini`) and empty subfolders are removed. Other folders and links are kept
  and listed in the preview, the confirmation and the end of the job. The watched, duplicates and
  Suwayomi folders are always kept. **What to do:** review the kept folders and move or delete the
  leftovers by hand.
- **Stale-entry cleanup now takes two runs and asks before removing much.** A run only stamps a
  missing file; a later run at least `STALE_CLEANUP_MISSING_GRACE_HOURS` (default 6) after that
  stamp deletes the row, and 0 still takes a second run. `POST /api/settings/cleanup-database` and
  `POST /api/files/cleanup-stale` answer 409 instead of deleting when the watched directory or
  sentinel file is missing. They answer 409 with `requiresConfirmation` when the watched directory
  holds no comics, more than `STALE_CLEANUP_MAX_REMOVAL_PERCENT` (default 10) of rows would go, a
  file has not been missing for the grace period, or a duplicate directory holds no comics while
  files are tracked under it. The cleanup at startup now runs two minutes after start, and a
  cleanup requested during a scan waits for it. **What to do:** scripts must handle 409 and repeat
  with `?confirm=true` to delete everything missing right now.
- **Metadata refreshes rewrite fewer archives.** A refresh retags only series whose match,
  canonical title or metadata version changed, queues only files whose cached series differs from
  the resolved name, and queues nothing when normalize is off. **What to do:** to force a rewrite
  of files that already carry the right series, run Normalize on them.
- **The processing history keeps less.** No-op outcomes ("File already normalized", "Normalize
  disabled" and the like) and the summary of a run that changed nothing are not recorded. Entries
  older than 90 days or beyond the newest 50,000 are pruned. **What to do:** to keep everything,
  set `PROCESSING_HISTORY_RETENTION_DAYS=0` and `PROCESSING_HISTORY_MAX_ENTRIES=0`.
- **Folder cover sidecars (`cover.<ext>`) are written by a background queue** shortly after a
  metadata change, not before the API call returns. **What to do:** scripts that read them right
  after a change may need to retry.
- **Half-finished offline downloads from 2.x are deleted** the first time a 3.0 page lists saved
  comics. Complete ones are kept. **What to do:** save those comics again.
- **Series cards and list rows have new markup.** Each is a `role=listitem` container with a
  select button and an open button, not one `<button>`. **What to do:** update custom CSS or
  userscripts that target the old structure.

#### Metadata providers

- **Automatic matching accepts only an exact title or alias match.** A provider's top fuzzy hit is
  no longer written into `ComicInfo.xml` or file names, so series that only had fuzzy hits stay
  unmatched. Existing matches, including wrong ones from 2.x, are kept until refreshed; a 2.x
  fuzzy match with no stored id becomes `not_found` if no exact match exists. **What to do:**
  match those series by hand (Manage Series Names, search, Use This Match).
- **Every enabled provider is asked at once and the best score wins**, where 2.x stopped at the
  first provider in a fixed order. Manual-match and cover-picker results now alternate between
  providers. **What to do:** nothing; new lookups can come from a different provider than before.
- **A refresh stays with the series' match.** A manually matched series is refreshed only from the
  provider that supplied it, by stored id when known; if that provider is disabled or the entry is
  gone, the match is kept. An automatically matched series refreshed through another of its names
  re-fetches its own match. **What to do:** to change a match, apply a new one by hand.
- **Providers are paced and time out sooner.** ComicVine is limited to 1 request per second and
  180 per hour (`COMICVINE_REQUESTS_PER_HOUR`), and is skipped once the hour's budget is spent.
  Requests time out after 15 seconds per attempt (`METADATA_PROVIDER_TIMEOUT_SECONDS`, 1-30). A
  lookup waits for pacing rather than failing (up to about 65 seconds for AniList), but a 429
  asking to wait more than 10 seconds fails at once. Large Refresh All or Match Unmatched jobs run
  at about 14 series per minute while AniList is on. **What to do:** raise the ComicVine variable
  if you have a higher allowance, or disable AniList for faster bulk runs.
- **Provider failures are no longer reported as "not found".** `POST
  /api/metadata/refresh/{seriesTitle}` and `GET /api/metadata/search` answer 503 with `{error,
  providers, retry_after_seconds}` when providers could not answer and nothing was found, and `GET
  /api/series-images/candidates` answers 503 with a text reason. Refresh jobs count outages as
  failures with the reason in `recentResults[].error`. Matched records are left unchanged, and
  unmatched series get `lookup_status` `error`. The messages use fixed wording built from the
  provider and kind of failure. **What to do:** API clients must handle 503 and must not parse
  provider text.
- **The series-language audit matches `<Series>` tags by series key** and includes the record's
  folder key, so it can report and retag more. **What to do:** review its findings.

#### E-reader delivery and Suwayomi

- **E-reader devices, subscriptions and delivery history are private per user.** Existing rows go
  to the first administrator, or stay administrator-only when there is none, and the same address
  can now be saved by different users. `GET /api/email/devices`, `/subscriptions`, `/deliveries`
  and `POST /api/email/deliveries/cancel-pending` cover only the caller's rows; administrators add
  `?all=true`. Another user's id answers 404. **What to do:** other users must add their own
  devices. With Authelia and an empty `AUTHELIA_ADMIN_GROUPS`, every non-ReadOnly user counts as
  an administrator here, so set it to keep these rows private.
- **Pending deliveries are now resumed.** On first start, every pending row left by earlier
  versions is sent, including old stranded ones. A message interrupted mid-send by a crash may be
  sent twice. A delivery interrupted three times with no result is marked Failed and must be sent
  again by hand. A temporary failure stays pending through retries for about 2 hours 35 minutes
  before it shows as Failed. **What to do:** use Cancel All Pending before upgrading if you no
  longer want the old ones.
- **`EMAIL_MAX_ATTACHMENT_MB` now limits the whole encoded email, not the raw file.** At the
  default 25, a book can be about 18 MB. **What to do:** if your mail server accepts larger
  messages, raise it; about 34 keeps the old 25 MB file allowance.
- **Very large condensed books are refused.** That means more than 500 issues in one book in "all"
  mode, or any condensed book estimated over 2 GiB. **What to do:** condense by count to split it.
- **Suwayomi source discovery is bounded:** 3 result pages per title, 4 titles per source, 2
  minutes per source and 10 minutes per series. A series beyond those limits may not be found;
  truncation is reported in `cutShortSearches`, apart from `discoveryFailures`. **What to do:**
  search for such a series directly.

#### HTTP API

- **Legacy routes are gone. What to do:** use the replacements.
  - `POST /api/process-all`, `/api/process-selected`, `/api/rename-all`, `/api/rename-selected`,
    `/api/normalize-all` and `/api/normalize-selected`: use the same names under `/api/jobs/`,
    which return `job_id` and `total_items`.
  - `POST /api/process-file`: use `POST /api/files/process` or `POST
    /api/files/{encodedFilePath}/process`.
  - `POST /api/scan-unmarked`: use `GET /api/files/scan-unmarked`, with no request body. The JSON
    is unchanged.
  - `PUT /api/watcher` and `POST /api/watcher/enable`, which did nothing: use `PUT
    /api/settings/watcher-enable-rename` and `PUT /api/settings/watcher-enable-normalize`.
  - The `POST` aliases of the settings under `/api/settings/`: use `PUT` on the same path with the
    same body. The actions `cleanup-database`, `reset` and `restart` stay `POST`.
- **Batch job selection is stricter.** `POST /api/jobs/process-all`, `rename-all`, `normalize-all`
  and `remove-metadata-all` no longer include files marked as duplicates. The `-selected` routes
  reject the whole request with 400 if any path, after resolving links, is outside the watched and
  duplicate directories (`delete-selected` keeps its stricter rule). Blank entries are dropped,
  and a list of only blanks is a 400 `No files specified`. **What to do:** name duplicates through
  a `-selected` route and send only library paths.
- **Bodies on the `-all` and `-unmarked` job routes are now read.** A non-JSON body answers 415,
  and malformed JSON or an empty chunked JSON body answers 400. `ForceReprocess` is now honoured
  as `?forceReprocess=true` on `process-unmarked`, `rename-unmarked` and the `-selected` routes,
  and in the body on the `-all` and `-unmarked` routes. **What to do:** send no body or valid
  JSON, and stop sending `ForceReprocess` if you relied on it being ignored.
- **Single-file processing reports failure.** `POST /api/files/process` and `POST
  /api/files/{encodedFilePath}/process` answer 400 when the file could not be fully processed.
  **What to do:** handle 400.
- **Metadata updates merge.** `PUT /api/files/{encodedFilePath}/tags` and `PUT
  /api/files/metadata` keep fields that are omitted or null. `POST
  /api/jobs/update-metadata-selected` and `POST /api/files/tags` accept an optional `Fields` list
  and answer 400 for an unknown field name, or when no field is given or carries a value (`No
  metadata fields specified`). `DELETE /api/files/{encodedFilePath}/metadata` also returns
  `filePath`. **What to do:** to clear a field, use one of the two `POST` routes with that field
  listed in `Fields` and an empty value.
- **Failure and cancellation codes changed.** `POST /api/files/read-batch` answers 500 and saves
  nothing when the bulk save fails, instead of writing one row at a time. The read endpoints
  answer 499 when the request is cancelled, and the single-file `DELETE` endpoints answer 499 if
  the client aborts while waiting for the file's lock. Requests that time out answer 504 instead
  of 500, and a client abort no longer produces a 500. **What to do:** handle these codes.
- **A setting pinned by an environment variable cannot be changed through the API.** A write that
  would change one is refused with 409 and saves nothing; it used to report success and store a
  value that never took effect. A field left out is not a write. **What to do:** remove or change
  the variable and restart.
- **Error bodies changed.** Unexpected failures are now `application/problem+json` with `status`,
  `title`, `detail`, `instance`, `correlationId` and `error`, and contain no exception text.
  Settings 400 and 409 answers are problem documents with an `error` string. `POST
  /api/settings/reset` no longer returns `{success:false,error}`. `GET /api/logs` and `GET
  /api/logs/files` no longer include their data fields on a 500. `POST
  /api/email/devices/{id}/test` returns a fixed reason on 502 with no server text, and 500 for
  unrecognised failures. **What to do:** check the HTTP status and read `error`; do not parse
  plain text or `success: false`.
- **Reader requests.** `POST /api/reader/session/end` answers 404 for another user's session.
  `POST /api/comicreader/progress` answers 500 when the database write fails, where it used to log
  and answer 200. Pages and covers are sent as `Cache-Control: private, max-age=3600`, and the
  cover ETag format changed, so browsers refetch each cover once. **What to do:** end sessions as
  the user who started them, retry on 500, and do not cache these responses in shared proxies or
  CDNs.
- **The default library view is gone, server-wide and per user.** The web app has no Files view
  left, so nothing read it, and Settings > Appearance no longer offers "Default library view".
  `GET` and `PUT /api/settings/default-library-view` are removed, `GET /api/settings` no longer
  returns `default_library_view`, and `GET /api/preferences` no longer returns `libraryViewMode`.
  `PUT /api/preferences` now ignores `libraryViewMode`, where it used to answer 400 for a value
  other than `files` or `series`, and the upgrade drops the `UserPreferences.LibraryViewMode`
  column. A stored `DefaultLibraryView` setting and `AppSettings__DefaultLibraryView` are ignored.
  **What to do:** stop calling those routes and reading those fields, and remove the variable if
  you set it.

#### Android app

- **The app needs Android 6.0 or later**, because its secure storage depends on the keystore.
  **What to do:** update to Android 6.0 or later; Android 5 devices cannot install it.
- **Sign-in is required.** **What to do:** enter the full server address, including `http://` or
  `https://`, and a username and password or an API key. There is no default server URL.
- **The app's data is excluded from Android backups.** **What to do:** sign in again after
  restoring to a new device.

#### Building and contributing

- **`ComicMaintainer.MauiApp` is no longer in `ComicMaintainer.sln`.** **What to do:** open or
  build `src/ComicMaintainer.Android.sln` to work on the app.
- **The "Automatic Version Bump" workflow is now "Release"**, and `test-dotnet` and the E2E
  workflow no longer have their own push trigger. Version bumps no longer add entries to this
  file. **What to do:** update branch protection or automation that names the old runs.
- **Pull requests into `dotnet` can now fail** on missing tests (add the `no-tests-needed` label
  with a reason), on .NET line coverage below 97 or JS coverage below 99/97/99, on compiler
  warnings in `src`, and on fixable critical or high image vulnerabilities. **What to do:** keep
  to these gates; a new base-image CVE can hold up a merge until the image is updated.
- **Internal interfaces changed.** Examples are `IFileStoreService.CleanupStaleEntriesAsync`,
  `IReadingSessionService.EndSessionAsync`, `IComicProcessorService` (job methods moved to
  `IBatchJobRegistry`), `IExternalSeriesMetadataService` (failures must throw
  `ExternalMetadataUnavailableException`), and the static `SuwayomiImportSynchronization`,
  replaced by `ILibraryMutationLock`. **What to do:** only code that implements or constructs
  these must change.

### Added

- **OPDS 1.2 catalog with OPDS-PSE page streaming**, so comic apps can browse and read the
  library. It is off by default (`OPDS_ENABLED`, or Settings > OPDS Catalog). It is served under
  `/opds/v1.2/` with its own HTTP Basic sign-in, using a password or an API key as the password.
  See [OPDS](docs/OPDS.md).
- **Notifications.** ntfy, Gotify, Discord, a JSON webhook, and Komga and Kavita library rescans,
  each with its own switch and event list. Messages go through a durable queue with retries, and
  Settings has a Send Test for each target. `POST /api/notifications/{target}/test` does the same.
  See [Notifications](docs/NOTIFICATIONS.md).
- **Richer series details.** AniList, MangaDex and ComicVine now supply a site link, genres,
  publication status, chapter count and origin. The library has Genre and Status filters (`GET
  /api/files/series` takes `genre` and `status`; `GET /api/files/series/facets` lists the values).
  Series cached before 3.0.0 show none of this until their metadata is next refreshed, and a
  refresh now also updates the cached synopsis.
- **Empty ComicInfo fields can be filled from the series' provider match**
  (`FILL_COMICINFO_FROM_PROVIDERS`, on by default). A value already in the file, or a locked
  Summary, is never changed. LanguageISO is never written from provider data.
- **API keys that work.** They are stored hashed, with created and last-used times, and managed
  with `GET`, `POST` and `DELETE /api/auth/api-key` or Settings > OPDS Catalog.
- **Signing out can end every session.** `POST /api/auth/logout` with `allSessions` also revokes
  the API key.
- **A Users page for administrators** (`#/users`, `g` `u`). It lists every account with its role,
  status, last sign-in and creation date, and adds accounts with a role, changes roles, resets
  passwords, and disables, enables or deletes accounts. The same is available as `GET` and `POST
  /api/users`, `PUT /api/users/{id}/role`, `POST /api/users/{id}/password`, `PUT
  /api/users/{id}/active` and `DELETE /api/users/{id}`. Adding an account, changing a role,
  resetting a password and enabling an account need the administrator's own password (Authelia
  sessions are exempt), so a stolen session token alone cannot create or take over an account.
  Role changes, resets and disabling end the account's sessions; resets and disabling also revoke
  its API key, and a reset lifts a lockout. Deleting removes the account's reading progress, read
  status, preferences, e-reader devices, subscriptions and delivery history. Nobody can change
  their own role, password, status or account there, and the last active administrator cannot be
  demoted, disabled or deleted. With Authelia enabled, adding accounts, changing roles and
  resetting passwords are refused, because Authelia does those. See [Users](docs/USER_GUIDE.md#users)
  and [User accounts](docs/API.md#user-accounts).
- **New settings:** `ConvertRarToCbzOnWrite`, `SETUP_TOKEN`, `FIX_LIBRARY_PERMISSIONS`,
  `STALE_CLEANUP_MAX_REMOVAL_PERCENT`, `STALE_CLEANUP_MISSING_GRACE_HOURS`,
  `STALE_CLEANUP_SENTINEL_FILE`, `PROCESSING_HISTORY_RETENTION_DAYS`,
  `PROCESSING_HISTORY_MAX_ENTRIES`, `COMICVINE_REQUESTS_PER_HOUR`,
  `METADATA_PROVIDER_TIMEOUT_SECONDS`, `FILL_COMICINFO_FROM_PROVIDERS`, `OPDS_ENABLED` and the
  `NOTIFY_*` variables. See [Environment variables](docs/ENVIRONMENT_VARIABLES.md).
- **Database Cleanup settings** for the removal limit, grace period, sentinel file and history
  retention, with `GET` and `PUT /api/settings/database-cleanup`.
- **One request saves every setting.** `PUT /api/settings` applies all groups in one validated
  write, and `GET /api/settings` lists pinned settings in `env_locked`. Pinned inputs are
  read-only in Settings, with a hint naming the variable.
- **An automatic database backup before migrations**, `comicmaintainer-<timestamp>-premigrate.db`,
  the newest three of which are kept apart from the scheduled backups.
- **Watcher catch-up at startup**, and live following of folder renames, moves and deletions made
  outside the app. Rows, read status, reader progress, metadata edits and field locks move with
  the folder, and files moved or renamed while the app was down keep their state when they can be
  matched.
- **Mark All Read and Unread now work on the library, a series or a folder** through `POST
  /api/files/read-all`.
- **Combine Folders shows what it will do.** Preview Merge and the confirmation list the folders
  to be removed and kept. `POST /api/files/combine-folders/preview` returns the same, and `GET
  /api/jobs/{job_id}` adds `reported_problems`.
- **Resumable offline downloads.** A comic counts as Saved only when its download finished. An
  interrupted one shows "Resume N/M" and fetches only the missing pages; one not resumed within 7
  days is removed.
- **Reader controls on touch devices.** A tap in the middle of the page, or anywhere on the stream
  in webcomic mode, shows or hides the controls. Page turns and scrolling leave them as they are,
  and webcomic mode shows the position in the top bar.
- **Delivery History for administrators.** An all-users view, with attempt count and next attempt
  time on each row. The condense plan names books too large to build.
- **The Android app can sign in**, with a password or an API key kept in secure storage, and has
  Test Connection on the sign-in page. It filters by all, unmarked, marked and duplicates, and can
  process or delete a selection.
- **API request logging in the browser**: `localStorage.setItem('debug_api', 'true')`.
- **A `LICENSE` file** (MIT).

### Security

- Removed the published `Admin123!` default and refused every published example password,
  including at sign-in.
- First-run setup needs a one-time code and is possible only on an instance with no accounts,
  never under Authelia.
- Account lockout, a per-client limit on authentication requests, failed attempts logged to
  `auth.log`, one uniform error for sign-in failures, and a dummy password check for unknown and
  locked accounts so response time does not give them away.
- Session tokens can be revoked, are checked against the account on each request (with a short
  cache), and are never read from a URL. Event streams opened with an API key end once the key is
  revoked.
- API keys are stored as SHA-256 hashes. A stolen session token alone cannot create one.
- An account disabled in ComicMaintainer is refused when it signs in through Authelia, as it is with
  a password or an API key.
- Series image downloads are checked at connection time against private, loopback, link-local,
  metadata, IPv4-mapped and NAT64 ranges, follow redirects only after re-checking each hop, and
  refuse an HTTPS-to-HTTP downgrade.
- Archive reads and rewrites are bounded, which limits what a crafted archive can do to memory and
  disk. Solid RAR files no longer use a reader that could be steered past the header checks.
- Logs are administrator-only.
- E-reader devices, subscriptions and deliveries are private per user.
- Comic pages and covers are never marked `public` for shared caches.
- A reader session can only be ended by its owner.
- The unauthenticated SignalR hub is gone, and CSP no longer allows WebSockets to any host.
- Unexpected failures no longer return exception text to clients.
- CI scans NuGet packages, the Docker image (Trivy) and code (CodeQL) for the .NET app, on pull
  requests, pushes to `dotnet` and weekly.
- `.dockerignore` secret patterns match at any depth, and the image no longer installs
  `unrar-free`, `p7zip-full` or recommended packages.

### Fixed

- **Library safety.** A missing or empty mount can no longer empty the library: cleanup changes
  nothing when the root is missing or the sentinel is absent. Case-only renames and loose files in
  the library root are no longer flagged as duplicates, which could lead Delete All Duplicates to
  remove the only copy. Combine Folders no longer deletes what it did not move.
- **Archive writes are atomic.** The old in-place overwrite, and the restore that could replace an
  intact original with a partial backup, are gone. A write that changes nothing leaves the archive
  untouched, and a malformed `ComicInfo.xml` is never replaced. The first start after upgrading
  restores a lost archive from a leftover `.backup` when it can.
- **`ComicInfo.xml` keeps what it had.** Normalize and batch updates no longer drop credits,
  Genre, Web, LanguageISO, Manga, Pages and Tags, and Batch Update no longer wipes blank fields.
- **CBR and RAR files are no longer written as ZIP data under their old extension**, which broke
  the reader and EPUB or AZW3 conversion. `.zip`, `.rar` and misnamed archives now work in the
  reader, covers, tagging and conversion, because the format is detected from content.
- **Concurrent work on one archive.** The watcher, batch jobs and metadata edits no longer rewrite
  or rename the same archive at once. A cancelled job no longer leaves a row at the old path.
- **Files and read state are not lost when things move.** Folder moves no longer leave rows stale
  or lose read status and metadata edits. Email delivery history follows renamed files and
  folders, and the file list refreshes after a catch-up scan follows moved files, even if the last
  move failed or the scan was stopped.
- **Processing results are honest.** A file left unprocessed, flagged as a duplicate or missing a
  series is no longer reported as a success.
- **Metadata lookups.** Fuzzy hits no longer rename series. Outages are no longer recorded as
  `not_found`, and failing providers back off. Each provider request has its own timeout.
  ComicVine has rate limiting, a User-Agent and status checks, and the manual-match picker shows
  every provider.
- **Email delivery.** Deliveries still pending at shutdown are resumed and no longer block later
  sends. Temporary SMTP failures are retried (1, 5 and 30 minutes, then 2 hours; five attempts in
  all). AZW3 conversion no longer holds the whole book in memory, and size checks use the encoded
  message size.
- **GitHub error reporting** looks up the fingerprint before repeating a request and honours
  `Retry-After` and rate limits.
- **Settings take effect at once**, instead of after an asynchronous file reload. A failed Save
  says what happened and shows the failure reference.
- **Reader.** Webcomic mode no longer keeps every page in memory or adds a scroll listener on each
  mode switch. The open series page no longer refetches its whole list on every processed file.
- **Offline.** Saved comics open offline after an app update. A failed update no longer replaces
  the release in control. An interrupted download is no longer shown as Saved. The first install
  no longer reloads the page.
- **The web app manifest loads.** It was an inline `blob:` URL that the app's own Content Security
  Policy blocked, so installing the app failed.
- **The action menu can no longer act on a different file.** Two files could share one menu
  because their ids collided.
- **Job progress survives a lost connection.** The event stream reconnects with backoff and a
  fresh token, has a 75-second watchdog, and polls the active job while it is down. Under Authelia
  it returns to sign-in only on a definite signed-out answer.
- **Series issue numbers** no longer pick up the year or volume, or markers inside brackets such
  as `(c2c)`.
- **Windows.** Startup no longer crashes when a staged restore can be neither read nor moved
  aside, and the current log can be read while it is being written.
- **Accessibility.** Series cards no longer nest a focusable control inside a button.

### Changed

- **Performance.** Page turns use a cached page list, so a page count needs only a stat. Pages and
  covers revalidate with a strong ETag and answer 304 without opening the archive, and each page
  turn writes both progress tables in one transaction. Series views read cached metadata and never
  open archives, and a cached cover's access time is refreshed at most once a day. Read state
  changes invalidate only that user's data. Series name lookups use an in-memory index. Database
  indexes now match the queries, and `PRAGMA optimize` runs after each scheduled cleanup.
- **Batch jobs have one API and one registry.** Every `/api/jobs/{operation}-{scope}` route is one
  action with one selection rule, and job state, persistence and retention live in one place.
- **Series names are compared the same way everywhere**, and "which files belong to a series" is
  answered in one place, used by refreshes, retags and audits.
- **Folder combine moved into a Core service.** Routes and response shapes are unchanged.
- **Unexpected errors have one shape**, produced in one handler, and carry a correlation id that
  matches the log.
- **The web app makes every request through one function**, which handles credentials, 401 and
  403, and errors.
- **Release pipeline.** One "Release" workflow tests the commit (unit, integration, Windows and
  container tests) before the version bump, tag, GitHub release, Docker image and Windows and
  Android builds. Images are pushed by digest and tagged only after every platform passes its
  smoke test. Pull requests build one amd64 image.
- **The Kubernetes example** runs `iceburn1/comicmaintainer:dotnet` with one replica and the
  Recreate strategy.

### Removed

- The endpoints listed under [Breaking changes](#http-api), and `GET /api/auth/sse-token`.
- The SignalR hub and its package reference.
- The server-side output cache, which never ran. Any `[OutputCache]` attribute now has no effect.
- The `SeriesIssuesMaxArchiveUpgrades` setting. A stale value in `appsettings` is ignored.
- The `DefaultLibraryView` setting and the `UserPreferences.LibraryViewMode` column, which the
  removed Files view used. See [Breaking changes](#http-api).
- The `AllowRegistration` setting. See [Breaking changes](#signing-in-and-accounts).
- The legacy `FileReadStatuses` table, whose data was copied to `UserFileReadStatuses` earlier.
  Going back to a pre-3.0 release needs a database backup.
- The plain-text `AspNetUsers.ApiKey` column.
- Several database indexes that no query used. External SQL tooling that relied on them may be
  slower.
- The Python-era files `start.sh`, `test_https_integration.sh`, `scripts/create-stable-branch.sh`
  and `.github/workflows/create-stable-branch.yml`, and the unreachable Files view of the web app.

## [2.0.413] - 2026-11-06

The changes below were collected under Unreleased while the 2.0 releases went out. They shipped in
2.0.x releases up to and including 2.0.413. Which release carried each one was never recorded
here; the GitHub release notes and the git history have that detail.

### Breaking changes
- **⚠️ Upgrade note: Authelia mode now requires `AUTHELIA_TRUSTED_PROXIES`.** With
  `AUTHELIA_ENABLED=true`, the app used to accept the `Remote-User`, `Remote-Groups`,
  `Remote-Email` and `Remote-Name` headers from any client, so anyone who could reach the
  container without going through the reverse proxy could sign in as any user (creating the
  account if needed) and, without `AUTHELIA_ADMIN_GROUPS`, administer the instance. The
  headers are now only accepted on connections that come directly from an address listed in
  `AUTHELIA_TRUSTED_PROXIES`: comma-separated IPv4/IPv6 addresses and CIDR ranges, such as
  `172.18.0.0/16, 10.0.0.5, fd00::/8`. From any other address they are ignored (the request
  is anonymous unless it carries a JWT) and a warning names the address. **Before upgrading a
  deployment that uses Authelia, set `AUTHELIA_TRUSTED_PROXIES`** to your reverse proxy's IP
  address or its Docker network's subnet: without a valid value the container refuses to
  start with `AUTHELIA_TRUSTED_PROXIES is required when Authelia is enabled` or
  `Invalid AUTHELIA_TRUSTED_PROXIES entry '…'`. Hostnames are not supported. See
  [Choosing `AUTHELIA_TRUSTED_PROXIES`](docs/AUTHELIA.md#choosing-authelia_trusted_proxies).
  Deployments without Authelia are not affected.
- **`GET /api/settings` no longer returns the ComicVine API key**, which any signed-in account,
  read-only ones included, could read. Like the other secrets it is reported only as
  `comicvine_api_key_set`. `PUT /api/settings/external-series-metadata` keeps the stored key
  when `comicVineApiKey` is null or omitted (an empty string still clears it), and the settings
  page gains a "Clear stored API key on save" box. API clients that read `comicvine_api_key`,
  or that cleared the key by sending the settings back without it, must be updated.
- **⚠️ Upgrade note: JWTs are no longer signed with a published secret, and `JWT_SECRET_FILE` is
  read.** Unless `JWT_SECRET` was set, tokens were signed and validated with the secret shipped
  in `appsettings.json`, and `docker-compose.dotnet.yml` and `.env.example` defaulted
  `JWT_SECRET` to another published value, so anyone could sign a token for any user,
  administrators included. `JWT_SECRET_FILE` and `ADMIN_PASSWORD_FILE`, which the Docker secrets
  instructions recommend, were ignored. They now name files, such as Docker secrets, that hold
  `JWT_SECRET` and `ADMIN_PASSWORD` (the line break at the end of the file is ignored). When no
  secret is set, or it is one of the published values (now ignored, with a warning on the
  startup log), a random key is generated on first start and kept in `jwt-secret.key` in the
  config directory (`/Config`); the startup log says which secret is in use. **After upgrading,
  users of such deployments must sign in again**, as their tokens were signed with the
  published secret. The container now refuses to start when a variable and its `_FILE` variant
  are both set, or when the file cannot be read or is empty: if you added a `_FILE` variable to
  a compose file that still sets `JWT_SECRET` or `ADMIN_PASSWORD`, remove that line. A blank
  `JWT_SECRET` now counts as not set. See
  [Use Docker Secrets](docs/DOCKER_DEPLOYMENT_DOTNET.md#2-use-docker-secrets-recommended-for-production).

### Added
- **Versioned Docker images.** Each release is now also published as
  `iceburn1/comicmaintainer:X.Y.Z`, the version the web interface shows as `X.Y.Z.0`, so a
  deployment can pin a release or roll back to one. `:dotnet` still points at the newest
  release. The version tag is pushed only for the commit the version bump tagged `vX.Y.Z`, so a
  later build that still carries the previous version number cannot replace it. The release is
  built from that commit even when another merge lands while its version bump is finishing.
  Publishes now wait for each other instead of cancelling, so that a release's version tag is
  not lost when the next release is merged during its build. To backfill an earlier release, run
  "Build and Push .NET Docker Image" by hand with **version** set. It rebuilds the release from
  its git tag and pushes only its version tag. See
  [Docker Image Tags](docs/GITHUB_ACTIONS_DOTNET.md#docker-image-tags) and
  [Pin or Roll Back to a Version](docs/DOCKER_DEPLOYMENT_DOTNET.md#pin-or-roll-back-to-a-version).
- **Find a series on Suwayomi and add it.** When the Suwayomi integration is enabled, a library
  search that finds no series offers **Search Suwayomi for "…"**, and **Find on Suwayomi** in the
  sidebar and the ⋮ menu opens the same dialog. It looks the title up in the Suwayomi library and
  on the installed sources for one language (the browser's language by default, plus
  multi-language sources; **All languages** searches every source; the choice is remembered per
  browser), and lists the matches with their covers. **Add & download** puts a series in the
  Suwayomi library and queues downloads for the chapters this library does not have: it leaves
  out chapters whose number matches an issue of a local series with the same title, canonical
  title or alias, chapters Suwayomi has already downloaded, other groups' releases of the same
  chapter, and unnumbered extras. The chapters reach the library through the Suwayomi download
  import. Hidden from read-only accounts. API: `GET /api/suwayomi/sources`,
  `GET /api/suwayomi/sources/{sourceId}/series`, `GET /api/suwayomi/series/{id}/thumbnail` and
  `POST /api/suwayomi/series/{id}/add`; see [Suwayomi Downloads](docs/API.md#suwayomi-downloads).
- **Authelia outages are logged.** While Authelia is down, the reverse proxy rejects every
  request, usually with HTTP 500 or 502, before it reaches ComicMaintainer, so the
  ComicMaintainer logs showed nothing wrong. Set the new `AUTHELIA_URL` to an address at which
  the container can reach Authelia, such as `http://authelia:9091`, and Authelia's health
  endpoint is checked every 30 seconds. The container log and `app.log` get a warning with the
  reason, such as `Connection refused`, when two checks in a row have failed, a reminder every
  5 minutes while it is down, and the length of the outage when it recovers. The check works
  whether or not `AUTHELIA_ENABLED` is set; without `AUTHELIA_URL`, nothing is checked. See
  [Logging Authelia Outages](docs/AUTHELIA.md#logging-authelia-outages).
- **Pending email deliveries can be cancelled.** Email Delivery History gives every pending
  delivery a **Cancel** button and adds **Cancel All Pending**, which covers every page. A
  delivery still in the queue is cancelled at once; one being prepared (converted to EPUB/AZW3
  or waiting for the rate limit) is stopped and its temporary file removed; one already being
  handed to the mail server is reported as such and finishes normally. Cancelled deliveries get
  the new `cancelled` status and do not count as delivered, so they can be sent again. API:
  `POST /api/email/deliveries/{id}/cancel` and `POST /api/email/deliveries/cancel-pending`.
  This is also the way to clear deliveries left `pending` by a restart, which are not resumed.
- **Email Delivery History is paginated.** It used to show only the 50 most recent deliveries;
  it now pages through the whole history, 50 at a time, and shows the total.
  `GET /api/email/deliveries` accepts `offset` alongside `limit` and returns `total`, `offset`,
  `limit`, `pending_count` and `rate_limited_until` next to `deliveries`.
- **Email rate limiting.** **Settings → Email Delivery** can cap comic emails per rolling hour
  (`EMAIL_MAX_SENDS_PER_HOUR`) and per rolling day (`EMAIL_MAX_SENDS_PER_DAY`), and enforce a
  minimum gap between them (`EMAIL_MIN_SECONDS_BETWEEN_SENDS`); `0`, the default, disables each
  limit. Deliveries over the limit stay pending, in order, and go out as it allows, and the
  history shows when the limit is holding them back. Only emails actually sent count. API:
  `PUT /api/settings/email-rate-limit`. See
  [Rate limiting](docs/EMAIL_DELIVERY.md#rate-limiting).
- **`GET /api/files/folders/files`** lists the files directly in the library root, which the
  per-folder route cannot address (its folder segment would be empty). The Files view uses it
  to show those files.
- **Suwayomi downloads can be moved into the library automatically.** Suwayomi saves chapters
  as `<downloads>/<source>/<series>/<chapter>.cbz` and cannot be told to use another layout,
  so they needed mounting inside the watched directory (leaving an extra source folder level)
  or a separate script to move them. Set the Suwayomi downloads folder under **Settings →
  Suwayomi Downloads → Import downloaded chapters** (or `SUWAYOMI_DOWNLOADS_DIR`), and the new
  *Suwayomi Download Import* scheduled job (`suwayomi-download-import`, every 15 minutes)
  moves each finished archive into `<series folder>/<chapter>.cbz` and removes the download
  folders it emptied. A chapter joins the series' existing folder when the library already has
  the series (matched on title, canonical title or alias, ignoring case and punctuation);
  otherwise a folder named after the Suwayomi series is created in the optional import folder
  (`SUWAYOMI_IMPORT_DIR`, relative to the watched directory; the watched directory itself by
  default). Archives still being written or that do not open are left for the next run, a
  clashing name gets a ` (2)` suffix, and the run summary flags chapters saved as loose images
  (enable *Save as CBZ archive* in Suwayomi). The downloads folder must be outside the library
  and writable by ComicMaintainer.
- **Condensed sends can be delivered as AZW3.** Combining issues into one book used to pin
  the format picker to EPUB, so a Kindle could not receive a condensed book in its native
  format. The picker now stays available while a condense mode is selected and offers both
  EPUB and AZW3 (only the original archives are unavailable, because `.cbz`/`.cbr` files
  cannot be merged), and `deliveryFormat` is honoured by `POST /api/email/send`,
  `/send-series`, `/condense-download` and `/condense-builds`. Condensed downloads are built
  and named in the chosen format, and a request without a format inherits the device default.
- **Large condensed-EPUB downloads now report their progress and their failures.** Building
  a book from a big selection takes minutes — longer than a browser or a reverse proxy will
  hold a request open — so the download used to start, show nothing, and silently die or
  fail with no visible reason. **Download** in the send dialog now starts a tracked build
  through `POST /api/email/condense-builds` that runs detached from the request, and the
  dialog polls it and shows a progress bar with the phase (reading the archives, writing
  pages, validating, recompressing), the issue being written and the page count. A finished
  build offers Download, a failed one shows the error that stopped it, and either can be
  cancelled or dismissed. Builds are per user, run one at a time, are de-duplicated when the
  same book is requested twice, and are kept with their file for 30 minutes so an interrupted
  download can be retried. `POST /api/email/condense-download` remains for small books.
- **Every API response now carries a correlation ID, and errors show it.** Each request is
  tagged with an `X-Correlation-Id` (echoed on the response, honoured if the caller supplies
  one) and that id is stamped on every log line the request produces in `debug*.log`. An
  unhandled exception is turned into an RFC 7807 problem document that carries the same id
  instead of a bare 500, and the browser appends `(ref: …)` to toasts reporting a failed API
  call, so a user can report "it failed, ref abc123" and the matching server log line can be
  found directly.
- **System Diagnostics panel.** A new admin-only `GET /api/diagnostics` returns one snapshot
  of the instance — version, environment, uptime, health check results, runtime and memory,
  watcher state, library totals, job counts, per-directory storage (present/writable/free) and
  the log files on disk — and the settings menu gains **🩺 System Diagnostics** to display it.
  **Copy report** puts the whole snapshot on the clipboard as plain text for a bug report.
  Every section is gathered independently, so one failing probe degrades to an error note
  rather than emptying the panel.
- **Keyboard shortcuts on the library page.** <kbd>/</kbd> focuses the search box,
  <kbd>Esc</kbd> clears it, <kbd>g</kbd> <kbd>h</kbd> / <kbd>g</kbd> <kbd>l</kbd> jump to
  Overview and Library, <kbd>r</kbd> refreshes the file list, <kbd>f</kbd> opens the filter
  menu, and <kbd>?</kbd> shows a help overlay listing them all (also reachable from the
  settings menu). Shortcuts never fire while a field has focus or a dialog is open, and the
  library-only ones stay inert on Overview where their control is hidden.
- **Clear button in the header search.** The search box gains a "×" that appears once it has
  text and both empties the box and re-runs the filter, instead of requiring a manual select
  and delete.

- **Issues can be condensed into a single EPUB before they are emailed.** The send dialog
  now offers "combine every N issues into one EPUB" (N between 2 and 500) and "combine all
  issues into one EPUB", so a device receives "Batman 001-005" instead of five separate
  books. The issues are ordered first, then grouped, so the books are 1-5, 6-10, and so on.
  A condensed book keeps the series metadata and cover of its first issue, numbers its
  pages continuously and gets one table-of-contents entry per issue. Before anything is
  queued the dialog asks the new `POST /api/email/condense-plan` endpoint for each book's
  estimated size: books larger than `EMAIL_MAX_ATTACHMENT_MB` are flagged, **Send** is
  disabled, and they can be downloaded instead through `POST /api/email/condense-download`.
  Issues already delivered inside an earlier condensed book count as delivered — for
  ordinary per-issue sends too — so re-sending a series does not duplicate them. A book
  never mixes series, and a send-series request keeps the order the series supplies.

### Changed
- **`GET /api/metadata/refresh/job/{id}` returns the status by name** (`"Running"`,
  `"Completed"`, …) instead of a number, and answers 404 for a job that finished more than 30
  minutes ago or has 50 newer finished jobs.
- **`GET /api/settings/filename-format` also returns `default`**, the built-in format.
- **File sizes above 1 GB are shown in GB/TB.** They were previously reported in megabytes,
  so free disk space read as "84258.8 MB".
- **Scheduled Jobs is a page of the app, like Settings and the Library.** It used to be a
  separate `jobs.html` with its own layout, no sidebar and only a "Back to Library" link. It now
  opens at `#/jobs` inside the app shell (sidebar, ⋮ menu or <kbd>g</kbd> <kbd>j</kbd>), with
  the same page header, cards, toasts and dark mode as the other pages. Each job's card shows
  its last-run status, last and next run, schedule and options; **Run now** follows the run
  and reports how it went, and status refreshes only while the page is open, without
  discarding unsaved edits. Old links to `/jobs.html` redirect to the new page.
- **Scheduled jobs are administrator-only.** The page and its menu entries are only offered to
  administrators, and `/api/scheduled-jobs` now requires the `CanAdminister` policy for reads
  as well as changes. The jobs include database backups and library-wide auto-corrections,
  which other accounts could previously change or run.
- **`/api/metadata` reports errors as `{ "error": "…" }`**, like the rest of the API. Its 400
  and 500 responses had plain-text bodies, so some series metadata messages fell back to a
  generic text. API clients that read these bodies as text must parse the JSON instead.
- **Renaming a file or marking it read with a path outside the library** is answered with
  "File path is outside the allowed directory" and logged, like the other file endpoints,
  instead of "Invalid file path".

### Fixed
- **`CONFIG_DIR` now holds the database and the DataProtection keys too.** The logs,
  `user-settings.json` and backups followed `CONFIG_DIR`, but the SQLite database and the keys
  stayed in `/Config` (or `AppSettings:ConfigDirectory`). In Docker, with the config volume
  mounted at a custom `CONFIG_DIR`, the accounts, file state and history were therefore kept in
  the container and lost when it was recreated. Outside Docker, a non-root user could not open
  the database at all. The directory is now chosen once (`CONFIG_DIR`, then
  `AppSettings:ConfigDirectory`, then `/Config`) and used for all of them, and the container
  makes a custom `CONFIG_DIR` writable by `PUID`. At startup the container now only gives `PUID`
  access to the config directories (`/Config` and `CONFIG_DIR`); it no longer makes them readable
  by everyone, even when one is inside `/watched_dir` or `/duplicates`. If the directory cannot be
  created, everything falls back to one temporary directory and a warning says so.
  **Upgrade note:** stop the app before upgrading and preserve its existing database and
  `DataProtection-Keys` directory. The old database/key location is the previous effective
  `AppSettings:ConfigDirectory` value (from `AppSettings__ConfigDirectory` or `appsettings.json`),
  not necessarily `/Config`. Copy `comicmaintainer.db` and any SQLite `-wal`, `-shm` or
  `-journal` sidecar files together, along with `DataProtection-Keys`, into the new `CONFIG_DIR`.
  If the previous effective `AppSettings:ConfigDirectory` already equals `CONFIG_DIR`, no
  database/key relocation is needed. Keep the originals until startup confirms the existing
  accounts and history are present. For Docker, copy the state out of the old container before
  removing it: its writable layer may contain the only copy. For example, while the old container
  is stopped, use `docker cp <container>:<old-config-dir>/comicmaintainer.db <host-config-dir>/`
  and also copy any sidecars and `<old-config-dir>/DataProtection-Keys`.
  Logs, settings and backups previously followed `CONFIG_DIR` (default `/Config`); if that
  previous directory differs from the new resolved directory, preserve `user-settings.json` and
  `backups` from it there as well.
- **The file watcher recovers after it stops with an error.** When a network share disconnected,
  the watched folder was removed, or (on Linux) an error ended the watcher's event loop, the
  watcher stayed dead until the service restarted, so new comics went undetected; on Linux it
  still reported itself as running. The watcher is now recreated, retrying after 1 s and then
  twice as long each time, up to 1 min, while the folder is unavailable, and the folder is
  rescanned for files that arrived in the meantime. A buffer overflow still only triggers a
  rescan. A slow or stalled browser connection no longer holds up the recovery, and changing
  the watched folder while the watcher is being recreated watches the new folder. A browser
  that does not accept a live update within 10 s is disconnected from the live updates, so it
  can no longer delay them for everyone else.
- **The Library Scan retries files changed on disk whose re-normalize failed.** With
  `reprocessModified` on, the scan recorded a changed file's new size and modified time before
  re-normalizing it, and again afterwards whatever the outcome. So a re-normalize that could not
  be queued, failed, or was cut short by a cancelled job still counted as done, and the file
  silently kept stale metadata. The stored values are now updated only for files the job
  re-normalized successfully, and every other changed file is picked up again by the next scan.
  When a job the scan started fails or is cancelled, the scan's last-run summary now says so, for
  example `[modified: job failed, 1/2 ok, 0 failed]`.
- **Processing renames files on filesystems without an atomic no-overwrite rename.** On NFS,
  ZFS before 2.2 and FUSE filesystems such as Unraid user shares, every rename failed with
  `Atomic no-overwrite rename is not supported by this filesystem.`, leaving files under their
  old names and filing an error report each time. The file is now moved there through a hard
  link or, where hard links are unavailable, an exclusively created copy that keeps its
  permissions and modification time. As before, it never replaces an existing file: a name that
  is taken is still a duplicate, and a file that replaces the original during the move is kept.
- **A conversion error for an unreadable page says which page and comic failed.** A page stored
  with an unsupported compression method, or whose data was corrupt, failed email delivery
  or a condensed-book build with the archive reader's bare message, such as
  `Unsupported compression method: 255`, leaving no way to tell which file to fix, especially
  in a book condensed from many issues. The error now names both:
  `Page '002.jpg' in Batman 012.cbz could not be read: Unsupported compression method: 255`.
  Pages that cannot be decoded include the same context.
- **Automated error reporting works when GitHub's response is labelled with a charset .NET
  cannot decode.** When something in between, such as a proxy, labelled the response
  `charset=utf8` or another name .NET does not recognise, filing an issue failed with a raw
  error: the report was dropped, **Send Test Report** answered 500, and an issue GitHub had
  already created was never recorded and could be filed again. Responses are now always read
  as UTF-8, which JSON requires and GitHub sends, so the issue is recorded and a rejected
  assignee falls back to an unassigned issue.
- **One client can no longer stop everyone from signing in behind a reverse proxy.** Signed-out
  requests are rate-limited per client address, but behind a proxy every request comes from the
  proxy's address, so one client sending more than about 110 requests a minute (a
  password-guessing script, say) made signing in fail with `429 Too Many Requests` for every
  user until the minute was over. Set the new `TRUSTED_PROXIES` to your proxy's address or its
  Docker network's subnet (with Authelia enabled, `AUTHELIA_TRUSTED_PROXIES` is used too), and
  requests from the proxy are counted per client, by the address it appends to
  `X-Forwarded-For`; addresses a client writes into the header itself are ignored. An invalid
  entry stops the app from starting. See
  [Trusted Proxies and Rate Limiting](docs/REVERSE_PROXY.md#trusted-proxies-and-rate-limiting).
- **A blank `JWT_SECRET` no longer prevents startup when `JwtSettings:Secret` is
  configured.** Startup took an empty variable (`docker run -e JWT_SECRET=`, `JWT_SECRET=` in
  an env file, or `-e JWT_SECRET="$(cat jwt_secret.txt)"` with a missing file) for the secret
  and exited with
  "JWT_SECRET must be configured in appsettings.json or environment variable", even when
  `JwtSettings:Secret` was set. A blank `JWT_SECRET` now counts as unset both where tokens are
  signed and where they are validated, so `JwtSettings:Secret` is used. If that is missing or
  blank too, or holds only the placeholder shipped in `appsettings.json` (which is public, so
  anyone could sign tokens with it), the app still refuses to start, and the error now says
  that `JWT_SECRET` is blank.
- **Admin groups set in `AUTHELIA_ADMIN_GROUPS` now restrict administration.** The permission
  check for administration read the Authelia settings without that variable, so when the admin
  groups were set only there (the documented way) every signed-in user who was not ReadOnly,
  local accounts included, could change settings, reset the database and run scheduled jobs.
  Only Admin users can now; if your users relied on this, add them to an admin group. The
  documented `AUTHELIA_EMAIL_HEADER`, `AUTHELIA_NAME_HEADER` and `AUTHELIA_GROUPS_HEADER`
  variables, which were ignored, are applied too.
- **The comic reader is confined to the library like the rest of the API.** Its endpoints
  accepted any path that merely started with the library's (`/comics2`, a folder differing in
  case) and followed links out of it; reads now use the checked, link-resolved file.
- **Links can no longer lead out of the library through other links.** A link to a file behind
  a linked folder, a chain of links, or a `..` inside a link's target was checked as written
  instead of where it really leads. Link loops, and paths that need more than 40 links, are
  rejected.
- **A folder whose name merely starts with the library's no longer counts as inside it.**
  `/comics2` passed the containment checks for a library at `/comics`, and on Linux so did a
  folder differing only in case. The checks now compare whole path segments, case-sensitively
  except on Windows. An empty duplicate or config directory no longer rejects every request
  that names a file.
- **The tags and process endpoints check that files are in the library**
  (`GET`/`PUT /api/files/{path}/tags`, `POST /api/files/{path}/process`,
  `POST /api/files/process-batch`, `POST /api/files/tags`), like their sibling endpoints. The
  batch endpoints reject the whole request when any path is outside it, instead of silently
  skipping it.
- **`POST /api/files/tags` reports files whose tags it could not write.** It answered 200
  whether or not anything was written, so a missing, non-archive or unreadable file looked
  updated. It now answers `207 Multi-Status` listing the `failed` files when only some could
  be updated, and `400` when none could, like the single-file `PUT /api/files/{path}/tags`.
- **A malformed folder link in `GET /api/files/folders/{path}/files` is rejected** with `400`
  instead of listing the files at the root of the library.
- **Removing metadata is confined to the library**, like writing it already was.
- **Deleting selected files is confined to the library.** `POST /api/jobs/delete-selected`,
  behind the multi-select **Delete** and **Delete All Duplicates**, deleted any file the
  service could write, such as the database in the config directory, for any signed-in user
  who is not ReadOnly. It now answers 400 when any path is outside the watched and duplicate
  directories, and the delete job skips such paths, including links that lead out of them,
  paths ending in a separator and paths with a `..` segment, and reports them as failed. It
  also refuses directories and links to them. The file is deleted from the directory that was
  checked, even if that directory is replaced while the job runs, on Linux, macOS and Windows
  (including files directly under a Windows drive root). On Windows and macOS paths are
  compared as the file system spells them, so a differently cased path to a library file is
  deleted, while a differently cased sibling of the library in a case-sensitive Windows
  directory is refused. The file's library record is removed even when Windows spells its path
  in a different case, but not the record of a distinct file whose name differs only in case.
- **Reading series metadata is confined to the library**, like reading a comic's other metadata
  already was.
- **Processing a file renames the file that was checked.** The rename used the path as given,
  so a folder on it replaced by a link during processing could redirect the rename outside the
  library. It now moves the checked, link-resolved file, and skips the rename if a folder on
  that path has been replaced by a link since the check.
- **Resetting the database cancels the jobs that are running**, which kept working on the data
  being deleted; **Reset Filename Format** restores the default instead of "undefined"; and the
  GitHub token setting is never written to the log.
- **Saving the External Series Metadata settings or the series cover size limits changes all of
  them or none.** Each field was written to `user-settings.json` separately, so a request that
  was aborted or hit a write error partway through kept the fields saved so far (metadata
  lookups could end up switched on with the old ComicVine key) and still answered 500. Each
  group is now saved in one write. A cover size so small that it rounds to 0 bytes is rejected
  with 400 instead of 500.
- **Editing or removing a comic's metadata no longer deletes its large pages.** Rewriting
  ComicInfo.xml rebuilt the archive without any entry over 10 MB (or without a name) and then
  replaced the original, so big pages were lost for good. Archives are now streamed entry by
  entry with nothing skipped, and if an entry cannot be copied the original is left untouched.
- **Embedding a series cover no longer loads the first issue's whole archive into memory.**
  Writing the cover into the first archive copied every page into memory before saving the new
  archive, so memory use grew with the archive's size and a very large archive could exhaust a
  container's memory or fail to get its cover. Entries are now streamed one at a time, as
  metadata rewrites already are.
- **Two simultaneous updates of a newly added file no longer lose one of them**, and renaming a
  file to its own path no longer deletes its database record.
- **A file re-added while the database is briefly unavailable keeps its processed state.** When
  its record could not be read (a transient error such as "database is locked"), the file was
  rebuilt in memory as not renamed, not normalized and created just now, so until the next
  restart it was listed as unprocessed, was processed again and could push its series to the top
  of Series Updates. A duplicate could also lose its duplicate flag, in the database as well. The
  file now keeps all of these, as it already kept its metadata.
- **Saving the page and marking a comic read at the same moment no longer undoes one of them.**
  The reader does both when the first page it shows is the last one (a one-page comic, say). If
  the user had no read record for the comic yet, both requests created one and the request that
  lost overwrote the other's change with its defaults, so the comic stayed unread or its saved
  page went back to 1. Only each request's own change is applied now.
- **An unrelated error while replacing a file is retried** instead of being mistaken for a
  cross-device move and falling back to a non-atomic copy.
- **Comics whose names contain consecutive dots ("What If...?") are tracked and usable.** They
  were left out of the library, and every API call naming one was rejected with 400, because
  the path checks looked for the text `..` rather than a real parent-folder segment.
- **A rename whose new name cannot be generated is reported as failed**, instead of as an
  already correctly named file.
- **Issue-number padding is limited to 0–10 digits.** The settings API accepted any
  non-negative value (and answered a negative one with 500), and values from
  `user-settings.json` or the environment were not checked at all: 1,000,000,000 or more made
  renames fail or was silently ignored, and a negative value named files "S - Chapter D-1".
  The API now rejects values outside 0–10 with 400, filenames clamp configured values to that
  range, and startup logs a warning naming the value used instead.
- **Renaming keeps decimal chapter numbers whatever the server's language.** The issue number
  was read using the server's number format, so with a language that writes decimals with a
  comma (German, Spanish, Italian, Dutch, Brazilian Portuguese, Turkish, … set through `LANG` or
  `LC_ALL`, or the operating system outside Docker), chapter 5.5 was named
  "S - Chapter 0055.5 - Chapter 0005.5" and 5.0 "S - Chapter 0050 - Chapter 0005"; with French,
  the number was not padded. The default container was not affected. A "." is now always the
  decimal point, as in the rest of the app. A comma is no longer read as a thousands
  separator: a Number of "1,000" or "1,5" is kept as written, like other non-numeric values
  such as "Extra", instead of becoming chapter 1000 or 15.
- **A batch job cancelled before it starts ends as Cancelled** instead of staying Queued.
- **A saved job whose status is not exactly a status name, such as a number, is restored as
  Interrupted**, like any other unrecognised status. `1` used to come back after a restart as a
  running job that was reported as the active job but could be neither cancelled nor deleted, and
  `42` as an undefined status that could not be deleted either.
- **Cancel in the progress dialog works for password logins** (it sent no credentials), and
  library-wide actions with nothing to do say so instead of tracking an empty job.
- **The progress dialog no longer counts files still to do as failed, or loses track of a
  job.** Right after a job started, after a page reload and after the live updates
  reconnected, every file the job had not processed yet counted as failed. A job that had just
  ended could retitle and close the dialog of a job started within the next few seconds, which
  then ran with no progress, no Cancel and no library refresh when it finished. Starting
  another action while a job is starting or running, minimized or not, is now refused with a
  message instead of taking the dialog over from that job.
- **Cancelling a job just as it finishes answers "already completed" instead of 500.** The
  cancel request (`POST /api/jobs/{jobId}/cancel`) failed with "Error cancelling job" and logged
  an error, a database reset at that moment failed the same way, and shutting down stopped
  cancelling the remaining jobs.
- **Deleting selected files clears the selection on the page at once.** The selection bar kept
  saying "N files selected" with its batch actions enabled, and the boxes stayed ticked, until a
  live update redrew the library — never, if the job was cancelled before it deleted a file or
  the event stream was down — and those actions then failed with "No files selected".
- **Series metadata works for local (password) logins.** Refresh, match, search, rename,
  aliases, preferred language, apply match and removing a match sent no session token and
  failed with 401 unless signed in through Authelia.
- **Uploading a series image from the series page works**; the request was sent as JSON instead
  of a file upload and always rejected.
- **Manage Names no longer loses alias edits.** Adopt as Canonical saved only the chosen name,
  so the other names it adopted dropped out of the alias list. After a preferred language or
  series image change the list still showed unsaved edits, but Save stored the earlier aliases
  and reported "Series names saved". Adopt as Canonical now saves the adopted aliases before the
  name, and unsaved alias edits survive the modal's other controls until Save stores them.
- **The series page no longer keeps renamed or deleted issues selected.** When its issues
  reloaded (after the watcher renamed or moved a file, a job ran in another tab, or a ticked
  issue was deleted from its own menu), the selection kept the vanished files: the count was too
  high, the batch buttons stayed enabled with nothing ticked, "Select all" could not clear them,
  and the next batch action was sent files that no longer exist. Issues the reloaded list no
  longer has are now unselected once it has fully loaded, and a deleted issue is unselected
  straight away. When a series can no longer be loaded by its id (its last issue is gone, or it
  was given a new id), it is looked up by its titles first: issues it still has under a new id
  stay selected, and the rest are unselected.
- **Series images are revalidated like comic covers.** `GET /api/series-images/{key}` compared
  `If-None-Match` with its ETag as an exact string, so a client sending a weak tag (`W/"…"`), a
  list of tags or `*` without `If-Modified-Since` downloaded the whole image again instead of
  getting 304 Not Modified. Tags are now compared as RFC 9110 requires, by the check the cover
  endpoint uses. The 304 repeats the `Cache-Control: private, max-age=300` of the full response
  instead of the API-wide `no-store`, which told browsers not to keep the image, and a tag that
  no longer matches gets the current image even when `If-Modified-Since` still matches.
  `If-Match`, `If-Unmodified-Since` and `If-Modified-Since` are applied in RFC 9110 order, and a
  412 keeps `no-store` instead of the image's `max-age`.
- **Metadata refresh progress finishes.** The job status was sent as a number, so the progress
  toast showed a digit, kept polling forever and never ran the final library refresh. Finished
  refresh jobs are now forgotten after 30 minutes (or once 50 newer ones finished) instead of
  being kept until restart.
- **A failed live update no longer marks a metadata refresh as failed** or leaves it "running".
- **A metadata refresh no longer stops when the browser's connection closes.** Refresh all,
  Match unmatched, and selected, folder and queued single-series refreshes ran on the request's
  cancellation token, which the web server reuses for later requests on the same connection and
  cancels when that connection closes. Closing the browser or an idle proxy connection could end
  the job as Cancelled partway, without re-tagging the files. Refresh jobs now run to completion
  and stop only when the application shuts down.
- **Cancelling a metadata search is no longer reported as a provider failure or a "no match"**:
  the provider health stays as it was, and the search endpoints stop logging an error and
  answering 500 when the browser gives up on the request.
- **Cancelled requests, jobs and shutdowns are no longer swallowed or reported as other
  failures.** Leaving the Library page while provider health loads no longer marks every
  metadata provider "Timed out" for a minute. Applying a provider image that times out while
  its host is checked answers 504 instead of 400 "Cannot resolve image host". Cancelling a
  rename job during a series lookup no longer renames the file to its unresolved series name,
  and a cancelled rename never moves the file. A cancelled metadata refresh no longer logs a
  "refresh failed" warning. A library scan stopped by shutdown no longer queues normalize jobs
  or reports one as "job record evicted". A series cover write that fails with a cancellation
  the queue did not ask for is logged and skipped, instead of silently stopping all later cover
  writes until restart.
- **First sign-in through Authelia works without `Remote-Email` or `Remote-Name`.** The account
  gets `<username>@authelia.local` and the username as its name; before, sign-in failed with
  401.
- **Authelia users whose username is an email address can sign in without `Remote-Email`.** For a
  username such as `alice@corp.example`, the placeholder `<username>@authelia.local` is not a
  valid address, so the account was never created and every request was answered with 401. Such
  a username is now used as the account's email; in any other username, each `@` becomes `=` in
  the placeholder. `=` can't appear in a username, and a username at `authelia.local` isn't used
  as is, so two users never get the same email, which would lock out whoever signed in second.
- **About 1 in 54 first sign-ins through Authelia no longer fail with 401.** The unused password
  generated for the new account sometimes lacked a digit, which the password policy requires.
- **"Run now" on a scheduled job that is already running says so** instead of reporting that the
  job started; `POST /api/scheduled-jobs/{key}/run` answers `queued: false`.
- **A scheduled job is no longer reported as skipped while its run is still going.** When the
  schedule came due during a run (typically a long **Run now**), the card switched to "Skipped"
  with a 0 ms duration, Run now was enabled again and the page announced "Library Scan: Skipped:
  Skipped: previous run still in progress."; the run's own result was then never announced. The
  skipped run is now only logged and counted (`outcome="skipped"` in
  `comicmaintainer_scheduled_job_runs_total`), and the job's next run moves on by one interval.
- **Database backups and restores work when the connection string is a `file:` URI**; such
  databases were mistaken for in-memory ones and never backed up.
- **Backups can be downloaded, deleted and restored when the backup directory is the file system
  root** (`AppSettings__DatabaseBackupDirectory=/`). They were created and listed, but every
  action on one failed with 500 because its name was rejected as invalid.
- **SQLite connections keep their settings when switching to WAL fails.** All the PRAGMAs ran
  as one command, so when `journal_mode=WAL` failed (a read-only database, or another connection
  holding a lock during the first switch) the `busy_timeout`, `synchronous`, `temp_store`,
  `cache_size` and `mmap_size` settings after it were skipped and the connection ran on SQLite's
  defaults. The busy timeout is now applied first, so the switch also waits for a reader to
  finish; the switch runs on its own and logs its own warning; and the other settings are
  applied whether it works or not.
- **An error that happens soon after reconnecting is reported**; an error while offline used
  up the reporting window.
- **The Files view** shows its empty states instead of "Loading files..." forever, shows the
  files in the library root, and hides the "… Unmarked" actions when nothing is unmarked.
  Changing the filter, search or sort while the root's files load no longer shows the older
  answer.
- **The library keeps its sort direction and filter after a reload.** Only the sort field was
  saved, so a library sorted "Date ↓" came back as "Date ↑" after a reload or on another
  device; the direction is now saved too (`sortDirection`, `asc` or `desc`, in
  `/api/preferences`). A saved filter that was the only non-default preference was shown in the
  header but not applied, so every series was listed until the filter was picked again. It now
  applies to the library and to a series page opened directly, such as when coming back from
  the reader.
- **Changing the filter on a phone keeps the open series on screen.** On screens up to 768 px
  wide, every filter change still switched to the Files view, which no longer exists, so
  screen readers announced "Files view selected." each time. On a series page, choosing a
  filter or tapping **Clear filter** also closed the series, put the library list in its place
  while the address still pointed at the series, and saved the library view mode again. Phones
  now behave like larger screens: the series stays open, its issues are reloaded with the new
  filter, and nothing is announced.
- **Process ▸ Clear Processed Status keeps the series page open and updates its badges.** It
  replaced the series page or library grid with the retired folder list (not even refreshed),
  leaving the address unchanged, and never reloaded the series' status badges. The open series
  now reloads its issues in place, and the library reloads as well. In the folder list itself,
  open folders no longer show files listed for an earlier filter, search or sort, and the
  expand/collapse-all button matches the folders' state after one is closed or reopened or the
  list changes.
- **The Library's List/Grid/Compact toggle highlights the current layout when the Library is
  opened from another page.** The app starts on the Overview and the buttons were only updated
  while the Library was shown, so none (or a stale one) was highlighted until a layout was
  picked.
- **Library covers that failed to load are fetched again when they come back into view**, for
  example after a server restart or a dropped connection. A failed cover is watched again after
  a pause that doubles with each failure (2 to 32 seconds, five times at most); before, it stayed
  blank until the list was re-rendered.
- **A modified time ahead of the browser's clock no longer reads "-1 days ago"** in the
  duplicate review and the Files view. Up to a minute ahead reads "Just now"; further ahead
  shows the date.
- **Offline use survives the browser restarting the service worker.** A restarted worker used
  the wrong cache, and an update activated offline could delete the precache it had just built;
  runtime caching could also evict the precached app files, and an uncached stylesheet or
  script requested offline answered with nothing instead of a network error.
- **Background cache updates in the service worker are no longer cut off.** The worker stores
  the scripts, stylesheets, icons, covers and pages it fetches, refreshes cached scripts and
  stylesheets, and replaces or evicts a cached cover or page once the server has answered. All of
  that ran after the page had its response, when the browser may already stop the worker, so a
  refreshed file could stay stale, a cover or page the session may no longer read could stay
  cached, and a cache could grow past its size limit. The worker now stays running until that
  work is done.
- **The cover-write and email queues can be stopped and started again.** A restart left them
  unable to accept work and could cancel the new run; nothing in the app restarts them today.
  A new run also waits for every earlier run still draining, so deliveries and archive
  rewrites never overlap.
- **Scheduled job run times are shown in local time.** The API reports them in UTC without a
  time-zone suffix, and the old Scheduled Jobs page read them as local time, so they were off
  by the browser's UTC offset. Saving a job also no longer drops options its card does not
  show (the metadata backfill's `maxBatch`).
- **The Docker image now shows the version of the release it was published as.** A merge to
  `dotnet` started the image build and the automatic version bump at the same moment, so the
  image was built from the merge commit, which still held the previous version number. The bump
  commit could not start a build of its own because it is pushed with `GITHUB_TOKEN`, and commits
  pushed that way do not trigger other workflows. As a result, the version in the app was always
  one behind the newest GitHub release. The image is now built after the version bump finishes,
  from the commit that holds the new version. A newer build cancels an older one that is still
  running, so a stale image can no longer be pushed last.
- **The Reload button on the "new version available" banner reloads the page again.** The
  service worker relayed the page's live-update event stream, which stays open indefinitely, so
  the old worker always had a request in flight. Chrome does not let a waiting service worker
  take over while that is true, so the button stayed on "Reloading…" until Chrome stopped waiting,
  five minutes after the update arrived. The event stream now goes straight to the browser
  without passing through the service worker. A downloaded update also no longer activates by
  itself. Before, the page could reload with no click at all whenever nothing kept the old worker
  busy, which is one reason the button got stuck only some of the time. The update now waits until
  Reload is clicked. The button closes the tab's event stream before asking for the switch, and it
  reloads straight away if the new version has already taken over. If another tab still holds the
  old worker, the page reloads anyway after five seconds and loads the new version from the
  network.
- **Downloading missing issues from Suwayomi finds a series Suwayomi lists under another
  title.** Suwayomi names a series after its source, which is often a romanized or localized
  title rather than the one shown in the library, so the download failed with "No series
  matching '…' was found in the Suwayomi library" although the series was there. The request
  now also sends the series' canonical title and aliases, and when the shown title has no
  exact match the server tries each of them in turn. An exact match on any title is preferred
  to a partial match on the shown one, so "Solo Leveling" no longer picks "Solo Leveling:
  Ragnarok" when the series itself is listed as "Ore dake Level Up na Ken". The other titles
  are only matched exactly, because a short alias such as an acronym would otherwise pick an
  unrelated series and queue its chapters. The confirmation names the Suwayomi series when it
  was found under a different title, and a title made only of punctuation no longer matches
  whichever series comes first in the Suwayomi library.

- **Downloading missing issues from Suwayomi works again, and a rejected request now files an
  issue.** The query that lists the Suwayomi library declared its pagination cursor as a GraphQL
  `String`, but Suwayomi types that argument with its own `Cursor` scalar and refuses the whole
  query before running it, so every series search and every download of missing issues failed
  with "Variable 'after' of type 'String' used in position expecting type 'Cursor'". The cursor
  is now declared as `Cursor`. The failure was also never filed automatically: the server's
  refusal was turned into a message for the browser without being logged, and automated error
  reporting only sees what is logged at `Error` and above. A query that Suwayomi refuses outright
  — a GraphQL error without a field `path`, so nothing ran — is now logged at `Error`, because
  ComicMaintainer's queries are fixed strings and only a defect in them can cause one. The log
  names the rejected query and lists every error the server gave, so each query that breaks
  files its own issue instead of being folded into the first one filed. Errors raised while a
  query runs, such as an unauthorized account, and an unreachable server are still only shown
  to the user.

- **A malformed Suwayomi chapter list no longer fails the whole Missing Issues check.** When
  Suwayomi answered the chapter query with a `chapters` value that was not an object, such as
  `null`, reading it threw: the chapter list of that series failed with a 500, and the
  scheduled Missing Issues check stopped for the whole library. The answer now identifies
  `data.chapters.nodes` as requiring an array, so the chapter list answers 400 with that detail
  and the check logs a warning for that series and carries on with the rest. A
  chapter list without its `chapters` or `nodes` is reported the same way instead of being read
  as an empty list, which said every missing issue had no matching chapter.

- **One malformed entry in a MangaDex answer no longer empties the whole search.** A search
  result that was not a JSON object, such as `null`, or whose `attributes` were not one, made
  the lookup throw: every other match was dropped, and the provider health recorded a failure
  with the cryptic "The requested operation requires an element of type 'Object'…". Such
  entries, and cover-art relationships of the same kind, are now skipped and the remaining
  matches are returned.

- **Condensed downloads no longer die at 30 seconds, and a timeout now files an issue.** Every
  request in the application was capped by a 30 second default timeout policy, and because the
  timeout middleware ran *before* routing it could never see an endpoint's own policy — so the
  one declared longer policy was unreachable and nothing could opt out. Condensing a series
  builds the whole book inside the request and then streams a result that can run to gigabytes,
  so it was being cut off every time. Worse, ASP.NET Core handles a timeout internally: it logs
  it at `Warning` with no method, path or correlation id and returns an empty `504`, and
  automated error reporting only captures `Error` and above — so these failures were never
  logged usefully, never filed as an issue, and reached the browser as a bare
  "HTTP error! status: 504". Request timeouts are now applied after routing, the condensed
  build and download endpoints are exempt (as are the event stream and the progress hub) while
  planning and bulk queueing get a longer allowance, and a timeout is logged at `Error` with its
  route, method, limit and correlation id — one issue per endpoint rather than one for all of
  them — and answers with a readable message and that same correlation id.

- **Condensed books that finished building are no longer lost on the way to disk.** The browser
  was told to drop the downloaded book in the same instant the save was triggered, which in some
  browsers cancels the save outright: the UI reported a successful download and no file
  appeared. A download or build failure also ended at an on-screen banner and was never
  reported, so nobody but the person looking at it ever learned of it; these failures are now
  filed automatically, while a request the server simply refused (a series over the issue limit,
  a book that is not ready yet) is still left to the user. A build recorded as completed whose
  file has since vanished is now logged instead of silently asking the user to build it again.

- **Renaming a file no longer fails when the watcher registers the new name first.** Moving a
  file's database record to its new path checked that the destination row was free and only
  then saved the rename, so when the FileSystemWatcher inserted its own row for the target in
  between the two steps the save hit the unique index on the file path and was abandoned with
  a `DbUpdateException`. The collision is now recovered the same way an already-present
  destination row is: the processing state (renamed/normalized/duplicate, metadata stamps and
  the original creation date) is merged onto the row that won, the stale old-path row is
  removed, and every user's read state and reading position still follow the file to its new
  name instead of being left behind. The merge itself no longer fails on a file whose metadata
  has been edited either: the metadata is now copied onto the surviving row instead of being
  handed over from the row that is about to be deleted, which the database layer rejected and
  which previously left both rows in place with the edits stranded on the old one.

- **Webcomic mode now flows straight from one issue into the next.** The following issue was
  only looked up, fetched and appended once the reader hit the bottom of the current one, so
  every comic boundary paused on a "loading next comic" banner and then jumped the viewport to
  the new issue's first page. The next issue is now stitched into the continuous stream ahead
  of the reader — as soon as the current issue is fully requested and the end is within a
  couple of screens — so its opening pages are already in the DOM when the boundary is reached
  and no scroll repositioning is needed at all. The finished issue is marked read when the
  reader scrolls past it rather than when the stream bottom is reached.

- **Oversized EPUBs are now compressed to fit the mail attachment limit instead of failing.**
  A book that came out larger than the configured `EmailMaxAttachmentMegabytes` was simply
  rejected. The converter now takes the delivery size budget and, when the lossless build
  exceeds it, rebuilds the book with progressively stronger page compression (JPEG quality
  80/65/50 with the longest edge capped at 2400/1800/1400 px), stopping at the first variant
  that fits. Conversions without a budget are unchanged and keep the original page data.

- **Emailed EPUBs no longer contain blank pages, and a book that cannot be built correctly is
  never sent.** Page images were embedded verbatim with the media type guessed from the entry
  name inside the archive, so a page whose data did not match its extension (PNG bytes stored
  as `.jpg`), a format ereaders cannot render (WebP, BMP), or a truncated/empty entry produced
  a page that silently rendered blank — typically every page after the series cover. Each page
  is now fully decoded: the real format decides the media type, formats outside the
  widely-supported JPEG/PNG/GIF set (including the series cover) are re-encoded as JPEG, and a
  page that cannot be decoded fails the conversion instead of shipping a blank page. The
  finished EPUB is re-opened and verified before it is handed back (leading `mimetype` entry,
  and every declared resource present and non-empty), and email delivery additionally rejects
  an empty attachment, so a bad conversion is recorded as a failed delivery rather than mailed
  to the device.
- **Metadata edits saved while a metadata backfill is running are written to the archive.**
  Saving an edit starts a metadata backfill that writes it to the file's ComicInfo.xml. If one
  was already running, the request was dropped, and the running backfill had already chosen
  its files, so the edit was not written until the next backfill; with the schedule off (the
  default) that took another edit or a manual run. The backfill now runs again as soon as the
  current run finishes.
- **Error messages no longer show another request's support reference.** The `(ref: …)` added
  to an error was the correlation id of whichever API call had last failed with one in the
  previous 15 seconds, so a request that never reached the server (offline, network error) or
  a proxy error page without an id showed an earlier, unrelated request's reference and sent
  support to the wrong log entries. An error now shows the id of its own request, or none.

- **The ⋮ action menu stays on screen in very narrow windows, and closes when you leave the
  page.** In a window 200 CSS px wide or less (for example at 400 % zoom) the menu was placed
  past the left edge of the screen, cutting off the start of every item; it now stays at least
  10 px inside both edges of the window, narrowing (and wrapping long labels) when the window
  is too narrow for it. An open menu also stayed over the next page — still acting on the
  previous page's issue — when the page changed without a click (browser Back/Forward, the
  mouse back button, swipe-back, or the `g h` / `g l` / `g s` / `g j` shortcuts); it now
  closes whenever the page changes.

- **Sign-in and first-run setup messages stay up for their full time, and signing in redirects
  once.** Each message on the login and setup pages started a hide timer that was never
  cancelled, so an error shown again (such as a second failed sign-in three seconds after the
  first) vanished after about two seconds instead of five. After a successful sign-in the Login
  button was enabled again while "Redirecting..." was shown, so a second click sent another
  login request; it now stays disabled until the app opens. When setup was still required but a
  session already existed (an Authelia session or a stored token), the login page redirected to
  both the setup page and the app, and network timing decided where the user ended up. It now
  sends the user to setup, and checks for an existing session only when setup is not required.

- **Save Settings no longer saves part of the settings without saying so.** The settings are
  saved in groups, one request each, and a failure stopped the save without undoing the groups
  already saved or naming them; for most groups the server's reason became "HTTP error!
  status: 400". Values the server would reject (a log size above 2047 MB, mail attachments
  above 200 MB, an error reporting limit above 100 issues a day, a recurrence interval outside
  1 to 720 hours) are now refused before anything is sent. If the server still rejects a
  group, the message gives its reason and lists the groups that were saved, and the form keeps
  your edits so you can correct the value and save again. An expired session during any group
  now goes to the sign-in page, as it already did for the last three, instead of showing
  "HTTP error! status: 401".

- **A failed database reset reports the HTTP status when the error is not JSON.** A reverse
  proxy's error page or an empty body (a 502 or 504, say) showed a JSON parse error such as
  "Unexpected token '<'" instead.

- **`POST /api/files/tags` rejects a request without files, and docs/API.md documents the
  real tags, settings and watcher endpoints.** The docs described a `GET /api/files/tags?file=`
  route that does not exist and a `{"file", "tags"}` body that the endpoint answered with `200`
  while updating nothing. A body without `files` now gets `400` (`No files provided`), and the
  docs cover `GET`/`PUT /api/files/{encodedFilePath}/tags` (base64url path) and
  `POST /api/files/tags` with `{"files", "metadata"}`. The settings section drops the `default`
  field that issue-number-padding never returned and documents the `PUT` routes and their
  `{"message"}` responses; the watcher section replaces the non-existent `/api/watcher/enabled`
  routes with `GET /api/watcher`.

- **Read-only users are no longer offered actions that change the library on a series page.**
  An issue's menu listed Process, Rename, Normalize, Remove Metadata and Delete; the Actions
  menu Manage Names, Manage Folders, Refresh Metadata, Refresh Folder, Update Covers and Reset
  Processed Status; and the Edit Tags, Series Folders, Manage Series Names and Duplicate Review
  windows had Save, Merge Folders, editing and Delete Duplicate buttons. Choosing one ended,
  often after a confirmation, in a generic "Failed to …" error, because the server refuses them
  to read-only accounts. They are now hidden like the selection toolbar: an issue offers only
  Info, View/Edit, Read Comic, Mark Read/Unread and, for a duplicate, Review Duplicate; the
  Actions menu is gone; and the file menus of the Files view match.

### Changed
- **Emailed EPUBs now carry the series artwork and a properly encoded issue number.** The
  converter previously marked the first comic page as the cover and wrote the issue number
  into the title exactly as ComicInfo.xml spelled it, so a library whose covers were
  curated in ComicMaintainer lost that artwork on the device, and issues sorted `#1, #10,
  #2`. The cached series image is now embedded as a dedicated cover page (`cover-image`,
  first in the spine, referenced from the navigation landmarks), falling back to the first
  page when no image is cached or the cached file cannot be decoded. The issue number is
  zero-padded in the title and its `file-as` sort key (`Series Name #012`), which is the
  only lever available on Kindle since it ignores series metadata for sideloaded books, and
  the series/position is written in both the EPUB 3 (`belongs-to-collection` /
  `group-position`) and legacy calibre (`calibre:series_index`) forms so Kobo, calibre and
  Kavita all pick it up. Decorated numbers (`#42`, `007`, `12a`) are normalised for the
  collection position; non-numeric ones (`Annual`) are left verbatim.

- **The web UI now announces what it is doing to assistive technology.** `showMessage()` is
  the library page's only notification channel — roughly a hundred call sites, including
  every error path — but `#messageContainer` was not a live region, so none of it reached
  screen readers. The container is now a polite live region and errors are additionally
  marked `role="alert"` so a failure interrupts rather than queueing behind routine progress
  chatter. The sign-in and first-run setup pages had the same problem in a more damaging
  form: a rejected sign-in wrote to `#errorMessage` and nothing else, leaving a screen reader
  user with no indication the attempt had failed. Those elements are now `role="alert"` /
  `role="status"`, and because they are `display: none` until shown (which keeps them out of
  the accessibility tree) the element is revealed *before* the text is written, so the change
  happens in a region that is already exposed.

- **Reduced-motion users no longer get frozen loading spinners.** The
  `prefers-reduced-motion` reset zeroed every animation on the page, which included the
  spinners. Several of those replace the button label while a request is in flight, so the
  reset turned "signing in…" into a static circle with no text and no other feedback. WCAG
  2.2.2 exempts motion that is essential to conveying information, so spinners now keep
  animating while decorative motion stays suppressed.

- **Viewport resize work is debounced.** Two separate `resize` listeners re-evaluated media
  queries and wrote to the DOM on every event, which fires continuously while a window is
  dragged or a device rotated. They now share a single debounced dispatcher.

- **Service worker update checks pause in hidden tabs.** The check ran every minute for the
  lifetime of the page, so a pinned tab left open kept issuing a request a minute
  indefinitely — to show an update banner nobody was looking at. It is now skipped while the
  tab is hidden, and runs when the tab becomes visible again so a returning user is not left
  waiting. Both paths share the same minimum interval: `sw.js` is served `no-store` and the
  main script always bypasses the HTTP cache, so every check is a real request and an
  unthrottled focus-triggered check would be noisier than the plain interval it replaced.

- **Documentation reorganised.** The repository root had accumulated 69 markdown files, most of
  them point-in-time fix write-ups and PR summaries, which buried the handful of documents
  people actually look for. The root now keeps only `README.md`, `QUICKSTART.md`,
  `CONTRIBUTING.md`, `SECURITY.md` and `CHANGELOG.md`; current reference material moved to
  `docs/`, and superseded write-ups to `docs/archive/`. Nothing was deleted, all files were
  moved with history preserved, and every internal link was updated.
  - `docs/README.md` is now an accurate index of current documentation, and
    `docs/archive/README.md` states plainly that its contents are unmaintained snapshots
    rather than a description of current behaviour.

- **Read/unread status is now per-user.** It previously lived in two global places
  (`ComicFiles.IsRead` and the `FileReadStatuses` table), so in a multi-user deployment one
  user marking an issue read flipped it for everyone, and that global state could disagree
  with the per-user progress the reader itself showed. Read status and the resume page are
  now stored per user in `UserFileReadStatuses`, and marking a file read/unread from the
  library also updates that user's reader progress so "Continue Reading" stays consistent.
  - Existing read state is migrated automatically. Because the old data does not record
    *who* read a file, it is attributed to a single account: an `Admin` if one exists,
    otherwise any user. Where per-user reader progress already existed it takes precedence,
    so issues finished in the reader stay attributed to the user who actually read them.
    **In a multi-user deployment, non-admin users may need to re-mark issues read.**
  - Marking a file read or unread no longer requires the `CanModifyLibrary` policy, since it
    only affects the calling user. `ReadOnly` users can now track their own progress. This
    resolves the asymmetry noted in the previous release, where the reader's mark-read was
    open but the library's bulk mark-read was treated as a library mutation.
  - `FileReadStatusEntity` and `ComicFiles.IsRead` are retained but unused so the migration
    can be rolled back; both are scheduled for removal in v3.0.

### Fixed
- Per-file metadata is no longer zero padded. The ComicInfo `<Number>` (issue)
  field now stores the bare issue number (e.g. `12`, not `0012`) and the
  `<Title>` field is set to `Chapter <issue>` (e.g. `Chapter 12`). Only the
  generated filename continues to apply the configured zero padding.
- Allow pinning a series name for series stuck in a "bad state" with no cached
  metadata record (e.g. series grouped purely from files on disk). Setting a
  non-empty name now creates a minimal manual cache record instead of failing
  with a 404, so these series can be fixed and their on-disk `<Series>` tags
  retagged. Reverting such a series to automatic remains a no-op.

### Added
- **Email comics to an e-reader.** Issues can be sent to saved e-reader addresses one at a
  time, as a whole series, or as an arbitrary selection, and a series can be subscribed so
  that every newly processed issue is delivered automatically once the watcher has finished
  renaming and normalizing it. Each delivery is sent either as the original archive or
  converted to a fixed-layout EPUB 3 first, which is the format e-readers actually render
  page-per-image rather than reflowing. Sends are queued and drained by a single background
  consumer, so a large series does not block the API or the watcher and does not hammer the
  mail server; already-delivered files are skipped by default, making a re-send of a series
  safe. Only files inside the watched or duplicate directory can be sent — symlinks and
  junctions are resolved first, so a link inside the library cannot point at a file outside
  it — the SMTP password is never returned by the API and is redacted from logs, and
  deliveries larger than the configured attachment limit fail rather than being sent.
  STARTTLS is required unless implicit TLS is selected or the new `SMTP_ALLOW_INSECURE`
  opt-in is enabled for a trusted local relay, and sending requires the `CanModifyLibrary`
  policy. See [Email to E-Reader](docs/EMAIL_DELIVERY.md).
- **Offline awareness.** A `fetch()` made with no network rejects with a bare
  "Failed to fetch", which the UI surfaced as a generic "Failed to load …" — indistinguishable
  from a server fault, so users retried against a server they could not reach. A banner now
  states the actual cause and disappears by itself when the connection returns.
- **Last-resort error reporting.** An uncaught exception or rejected promise used to stop the
  surrounding code silently, leaving spinners spinning and stale data on screen with nothing
  to explain why. `unhandledrejection` and `error` are now handled globally and surface a
  message, rate limited so a failure inside a render or loop path cannot bury the UI in
  banners. Reports are suppressed while offline, where the offline banner already explains
  the cause.
- Keyboard focus indicators, a `<main>` landmark and an accessible name for the theme toggle
  on the sign-in and first-run setup pages. These are standalone pages that do not load
  `css/main.css`, so they had missed the global `:focus-visible` rule the rest of the app
  gained: they cleared the default focus outline for mouse users and never restored one,
  leaving keyboard users with no visible focus on the sign-in form.
- Multi-architecture Docker images. `linux/arm64` is now published alongside `linux/amd64`,
  so the image runs natively on Apple Silicon and common ARM NAS/SBC hosts. The .NET build
  runs natively on the build host and cross-compiles, so adding the second architecture does
  not require emulating the whole SDK.
- Container `HEALTHCHECK` in `Dockerfile.dotnet`, wired to the existing `/health` liveness
  endpoint, so `docker ps` and orchestrators can see container health without extra
  configuration. It deliberately uses `/health` rather than `/health/ready`: readiness also
  checks the database, and a transient database problem should not cause a restart loop.
- Dependabot configuration for NuGet, GitHub Actions and Docker base images, with Microsoft
  runtime packages grouped so their coordinated releases arrive as a single pull request.
- Batch processing jobs now survive a restart. Job state is persisted, and any job still
  queued or running when the process stops is reported as `Interrupted` on the next startup,
  along with how many files it had already processed.
  - Previously a restart (including the app's own `POST /api/settings/restart`) discarded
    in-flight jobs entirely, leaving the UI polling a job id the server no longer recognised
    and showing a progress bar that never moved.
  - Interrupted jobs are **not** resumed automatically: the batch operations are not
    transactional, so re-running one could repeat side effects on files already handled. The
    UI reports the interruption and the progress made so you can decide whether to re-run it.
  - Jobs now record which operation created them, so an interrupted job can be described
    meaningfully after a restart.
  - Completed job records are pruned after 7 days, keeping the 50 most recent regardless of age.
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

## Releases 1.0.1 to 2.0.412

These releases were automatic version bumps and recorded no changes of their own. The real changes
of the 2.0 line are listed under [2.0.413](#20413---2026-11-06). Each release's commits are on its
[GitHub release](https://github.com/mleenorris/ComicMaintainer/releases) and in the git tags.

| Versions | Date |
| --- | --- |
| 2.0.407 to 2.0.412 | 2026-11-06 |
| 2.0.406 | 2026-11-01 |
| 2.0.405 | 2026-10-31 |
| 2.0.391 to 2.0.404 | 2026-10-29 |
| 2.0.372 to 2.0.390 | 2026-10-28 |
| 2.0.353 to 2.0.371 | 2026-10-27 |
| 2.0.345 to 2.0.352 | 2026-10-26 |
| 2.0.341 to 2.0.344 | 2026-10-20 |
| 2.0.339 to 2.0.340 | 2026-10-19 |
| 2.0.337 to 2.0.338 | 2026-10-17 |
| 2.0.335 to 2.0.336 | 2026-10-16 |
| 2.0.331 to 2.0.334 | 2026-10-15 |
| 2.0.322 to 2.0.330 | 2026-10-14 |
| 2.0.313 to 2.0.321 | 2026-10-01 |
| 2.0.312 | 2026-09-30 |
| 2.0.310 to 2.0.311 | 2026-09-29 |
| 2.0.309 | 2026-09-28 |
| 2.0.301 to 2.0.308 | 2026-09-25 |
| 2.0.300 | 2026-09-22 |
| 2.0.298 to 2.0.299 | 2026-09-20 |
| 2.0.290 to 2.0.297 | 2026-09-18 |
| 2.0.289 | 2026-09-15 |
| 2.0.288 | 2026-09-13 |
| 2.0.287 | 2026-09-12 |
| 2.0.286 | 2026-09-10 |
| 2.0.285 | 2026-07-25 |
| 2.0.282 to 2.0.284 | 2026-07-24 |
| 2.0.277 to 2.0.281 | 2026-07-23 |
| 2.0.276 | 2026-07-21 |
| 2.0.275 | 2026-07-15 |
| 2.0.274 | 2026-07-12 |
| 2.0.272 to 2.0.273 | 2026-07-11 |
| 2.0.270 to 2.0.271 | 2026-06-30 |
| 2.0.269 | 2026-06-29 |
| 2.0.259 to 2.0.268 | 2026-06-27 |
| 2.0.257 to 2.0.258 | 2026-06-24 |
| 2.0.256 | 2026-06-19 |
| 2.0.255 | 2026-06-16 |
| 2.0.253 to 2.0.254 | 2026-06-05 |
| 2.0.248 to 2.0.252 | 2026-06-04 |
| 2.0.246 to 2.0.247 | 2026-06-03 |
| 2.0.239 to 2.0.245 | 2026-06-02 |
| 2.0.238 | 2026-06-01 |
| 2.0.237 | 2026-05-30 |
| 2.0.228 to 2.0.236 | 2026-05-29 |
| 2.0.219 to 2.0.227 | 2026-05-28 |
| 2.0.218 | 2026-05-27 |
| 2.0.216 to 2.0.217 | 2026-05-26 |
| 2.0.209 to 2.0.215 | 2026-05-24 |
| 2.0.206 to 2.0.208 | 2026-05-23 |
| 2.0.195 to 2.0.205 | 2026-05-22 |
| 2.0.178 to 2.0.194 | 2026-05-21 |
| 2.0.175 to 2.0.177 | 2026-05-20 |
| 2.0.169 to 2.0.174 | 2026-05-19 |
| 2.0.160 to 2.0.168 | 2026-05-18 |
| 2.0.154 to 2.0.159 | 2026-05-17 |
| 2.0.148 to 2.0.153 | 2026-05-16 |
| 2.0.145 to 2.0.147 | 2026-05-15 |
| 2.0.142 to 2.0.144 | 2026-05-14 |
| 2.0.140 to 2.0.141 | 2026-05-13 |
| 2.0.136 to 2.0.139 | 2026-05-12 |
| 2.0.133 to 2.0.135 | 2026-05-11 |
| 2.0.128 to 2.0.132 | 2026-04-09 |
| 2.0.126 to 2.0.127 | 2026-04-08 |
| 2.0.125 | 2026-03-29 |
| 2.0.123 to 2.0.124 | 2026-03-28 |
| 2.0.121 to 2.0.122 | 2026-03-26 |
| 2.0.119 to 2.0.120 | 2026-03-25 |
| 2.0.118 | 2026-01-10 |
| 2.0.117 | 2025-11-19 |
| 2.0.116 | 2025-11-18 |
| 2.0.115 | 2025-11-17 |
| 2.0.114 | 2025-11-14 |
| 2.0.103 to 2.0.113 | 2025-11-13 |
| 2.0.81 to 2.0.102 | 2025-11-12 |
| 2.0.80 | 2025-11-10 |
| 2.0.76 to 2.0.79 | 2025-11-08 |
| 2.0.58 to 2.0.75 | 2025-11-07 |
| 2.0.44 to 2.0.57 | 2025-11-06 |
| 2.0.30 to 2.0.43 | 2025-11-04 |
| 2.0.9 to 2.0.29 | 2025-11-03 |
| 2.0.1 to 2.0.8 | 2025-11-02 |
| 1.0.38 to 1.0.42 | 2025-10-26 |
| 1.0.29 to 1.0.37 | 2025-10-25 |
| 1.0.8 to 1.0.28 | 2025-10-24 |
| 1.0.1 to 1.0.7 | 2025-10-23 |

## 1.0.0 - 2024

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

[Unreleased]: https://github.com/mleenorris/ComicMaintainer/compare/v3.0.0...dotnet
[3.0.0]: https://github.com/mleenorris/ComicMaintainer/compare/v2.0.413...v3.0.0
[2.0.413]: https://github.com/mleenorris/ComicMaintainer/releases/tag/v2.0.413
