# Changelog

All notable changes to ComicTagger Watcher Service will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Added
- **`GET /api/files/folders/files`** lists the files directly in the library root, which the
  per-folder route cannot address (its folder segment would be empty). The Files view uses it
  to show those files.
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

### Fixed
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
- **Removing metadata is confined to the library**, like writing it already was.
- **Resetting the database cancels the jobs that are running**, which kept working on the data
  being deleted; **Reset Filename Format** restores the default instead of "undefined"; and the
  GitHub token setting is never written to the log.
- **Editing or removing a comic's metadata no longer deletes its large pages.** Rewriting
  ComicInfo.xml rebuilt the archive without any entry over 10 MB (or without a name) and then
  replaced the original, so big pages were lost for good. Archives are now streamed entry by
  entry with nothing skipped, and if an entry cannot be copied the original is left untouched.
- **Two simultaneous updates of a newly added file no longer lose one of them**, and renaming a
  file to its own path no longer deletes its database record.
- **An unrelated error while replacing a file is retried** instead of being mistaken for a
  cross-device move and falling back to a non-atomic copy.
- **Comics whose names contain consecutive dots ("What If...?") are tracked and usable.** They
  were left out of the library, and every API call naming one was rejected with 400, because
  the path checks looked for the text `..` rather than a real parent-folder segment.
- **A rename whose new name cannot be generated is reported as failed**, instead of as an
  already correctly named file.
- **A batch job cancelled before it starts ends as Cancelled** instead of staying Queued.
- **Cancel in the progress dialog works for password logins** (it sent no credentials), and
  library-wide actions with nothing to do say so instead of tracking an empty job.
- **Series metadata works for local (password) logins.** Refresh, match, search, rename,
  aliases, preferred language, apply match and removing a match sent no session token and
  failed with 401 unless signed in through Authelia.
- **Uploading a series image from the series page works**; the request was sent as JSON instead
  of a file upload and always rejected.
- **Metadata refresh progress finishes.** The job status was sent as a number, so the progress
  toast showed a digit, kept polling forever and never ran the final library refresh. Finished
  refresh jobs are now forgotten after 30 minutes (or once 50 newer ones finished) instead of
  being kept until restart.
- **A failed live update no longer marks a metadata refresh as failed** or leaves it "running".
- **Cancelling a metadata search is no longer reported as a provider failure or a "no match"**:
  the provider health stays as it was, and the search endpoints stop logging an error and
  answering 500 when the browser gives up on the request.
- **First sign-in through Authelia works without `Remote-Email` or `Remote-Name`.** The account
  gets `<username>@authelia.local` and the username as its name; before, sign-in failed with
  401.
- **Authelia users whose username is an email address can sign in without `Remote-Email`.** For a
  username such as `alice@corp.example`, the placeholder `<username>@authelia.local` is not a
  valid address, so the account was never created and every request was answered with 401. Such
  a username is now used as the account's email; in any other username, each `@` becomes `_` in
  the placeholder.
- **About 1 in 54 first sign-ins through Authelia no longer fail with 401.** The unused password
  generated for the new account sometimes lacked a digit, which the password policy requires.
- **"Run now" on a scheduled job that is already running says so** instead of reporting that the
  job started; `POST /api/scheduled-jobs/{key}/run` answers `queued: false`.
- **Database backups and restores work when the connection string is a `file:` URI**; such
  databases were mistaken for in-memory ones and never backed up.
- **An error that happens soon after reconnecting is reported**; an error while offline used
  up the reporting window.
- **The Files view** shows its empty states instead of "Loading files..." forever, shows the
  files in the library root, and hides the "… Unmarked" actions when nothing is unmarked.
  Changing the filter, search or sort while the root's files load no longer shows the older
  answer.
- **Offline use survives the browser restarting the service worker.** A restarted worker used
  the wrong cache, and an update activated offline could delete the precache it had just built;
  runtime caching could also evict the precached app files, and an uncached stylesheet or
  script requested offline answered with nothing instead of a network error.
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


## [1.0.16] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.17] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.18] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.19] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.20] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.21] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.22] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.23] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.24] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.25] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.26] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.27] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.28] - 2025-10-24

### Changed
- Automatic version bump on merge to master


## [1.0.29] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.30] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.31] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.32] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.33] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.34] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.35] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.36] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.37] - 2025-10-25

### Changed
- Automatic version bump on merge to master


## [1.0.38] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.39] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.40] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.41] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [1.0.42] - 2025-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.1] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.2] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.3] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.4] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.5] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.6] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.7] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.8] - 2025-11-02

### Changed
- Automatic version bump on merge to master


## [2.0.9] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.10] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.11] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.12] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.13] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.14] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.15] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.16] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.17] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.18] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.19] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.20] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.21] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.22] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.23] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.24] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.25] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.26] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.27] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.28] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.29] - 2025-11-03

### Changed
- Automatic version bump on merge to master


## [2.0.30] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.31] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.32] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.33] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.34] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.35] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.36] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.37] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.38] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.39] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.40] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.41] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.42] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.43] - 2025-11-04

### Changed
- Automatic version bump on merge to master


## [2.0.44] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.45] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.46] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.47] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.48] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.49] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.50] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.51] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.52] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.53] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.54] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.55] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.56] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.57] - 2025-11-06

### Changed
- Automatic version bump on merge to master


## [2.0.58] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.59] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.60] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.61] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.62] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.63] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.64] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.65] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.66] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.67] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.68] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.69] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.70] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.71] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.72] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.73] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.74] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.75] - 2025-11-07

### Changed
- Automatic version bump on merge to master


## [2.0.76] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.77] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.78] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.79] - 2025-11-08

### Changed
- Automatic version bump on merge to master


## [2.0.80] - 2025-11-10

### Changed
- Automatic version bump on merge to master


## [2.0.81] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.82] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.83] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.84] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.85] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.86] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.87] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.88] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.89] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.90] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.91] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.92] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.93] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.94] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.95] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.96] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.97] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.98] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.99] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.100] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.101] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.102] - 2025-11-12

### Changed
- Automatic version bump on merge to master


## [2.0.103] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.104] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.105] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.106] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.107] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.108] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.109] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.110] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.111] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.112] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.113] - 2025-11-13

### Changed
- Automatic version bump on merge to master


## [2.0.114] - 2025-11-14

### Changed
- Automatic version bump on merge to master


## [2.0.115] - 2025-11-17

### Changed
- Automatic version bump on merge to master


## [2.0.116] - 2025-11-18

### Changed
- Automatic version bump on merge to master


## [2.0.117] - 2025-11-19

### Changed
- Automatic version bump on merge to master


## [2.0.118] - 2026-01-10

### Changed
- Automatic version bump on merge to master


## [2.0.119] - 2026-03-25

### Changed
- Automatic version bump on merge to master


## [2.0.120] - 2026-03-25

### Changed
- Automatic version bump on merge to master


## [2.0.121] - 2026-03-26

### Changed
- Automatic version bump on merge to master


## [2.0.122] - 2026-03-26

### Changed
- Automatic version bump on merge to master


## [2.0.123] - 2026-03-28

### Changed
- Automatic version bump on merge to master


## [2.0.124] - 2026-03-28

### Changed
- Automatic version bump on merge to master


## [2.0.125] - 2026-03-29

### Changed
- Automatic version bump on merge to master


## [2.0.126] - 2026-04-08

### Changed
- Automatic version bump on merge to master


## [2.0.127] - 2026-04-08

### Changed
- Automatic version bump on merge to master


## [2.0.128] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.129] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.130] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.131] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.132] - 2026-04-09

### Changed
- Automatic version bump on merge to master


## [2.0.133] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.134] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.135] - 2026-05-11

### Changed
- Automatic version bump on merge to master


## [2.0.136] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.137] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.138] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.139] - 2026-05-12

### Changed
- Automatic version bump on merge to master


## [2.0.140] - 2026-05-13

### Changed
- Automatic version bump on merge to master


## [2.0.141] - 2026-05-13

### Changed
- Automatic version bump on merge to master


## [2.0.142] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.143] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.144] - 2026-05-14

### Changed
- Automatic version bump on merge to master


## [2.0.145] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.146] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.147] - 2026-05-15

### Changed
- Automatic version bump on merge to master


## [2.0.148] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.149] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.150] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.151] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.152] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.153] - 2026-05-16

### Changed
- Automatic version bump on merge to master


## [2.0.154] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.155] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.156] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.157] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.158] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.159] - 2026-05-17

### Changed
- Automatic version bump on merge to master


## [2.0.160] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.161] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.162] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.163] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.164] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.165] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.166] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.167] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.168] - 2026-05-18

### Changed
- Automatic version bump on merge to master


## [2.0.169] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.170] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.171] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.172] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.173] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.174] - 2026-05-19

### Changed
- Automatic version bump on merge to master


## [2.0.175] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.176] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.177] - 2026-05-20

### Changed
- Automatic version bump on merge to master


## [2.0.178] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.179] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.180] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.181] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.182] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.183] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.184] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.185] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.186] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.187] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.188] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.189] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.190] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.191] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.192] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.193] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.194] - 2026-05-21

### Changed
- Automatic version bump on merge to master


## [2.0.195] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.196] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.197] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.198] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.199] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.200] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.201] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.202] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.203] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.204] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.205] - 2026-05-22

### Changed
- Automatic version bump on merge to master


## [2.0.206] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.207] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.208] - 2026-05-23

### Changed
- Automatic version bump on merge to master


## [2.0.209] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.210] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.211] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.212] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.213] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.214] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.215] - 2026-05-24

### Changed
- Automatic version bump on merge to master


## [2.0.216] - 2026-05-26

### Changed
- Automatic version bump on merge to master


## [2.0.217] - 2026-05-26

### Changed
- Automatic version bump on merge to master


## [2.0.218] - 2026-05-27

### Changed
- Automatic version bump on merge to master


## [2.0.219] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.220] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.221] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.222] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.223] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.224] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.225] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.226] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.227] - 2026-05-28

### Changed
- Automatic version bump on merge to master


## [2.0.228] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.229] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.230] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.231] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.232] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.233] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.234] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.235] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.236] - 2026-05-29

### Changed
- Automatic version bump on merge to master


## [2.0.237] - 2026-05-30

### Changed
- Automatic version bump on merge to master


## [2.0.238] - 2026-06-01

### Changed
- Automatic version bump on merge to master


## [2.0.239] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.240] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.241] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.242] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.243] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.244] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.245] - 2026-06-02

### Changed
- Automatic version bump on merge to master


## [2.0.246] - 2026-06-03

### Changed
- Automatic version bump on merge to master


## [2.0.247] - 2026-06-03

### Changed
- Automatic version bump on merge to master


## [2.0.248] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.249] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.250] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.251] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.252] - 2026-06-04

### Changed
- Automatic version bump on merge to master


## [2.0.253] - 2026-06-05

### Changed
- Automatic version bump on merge to master


## [2.0.254] - 2026-06-05

### Changed
- Automatic version bump on merge to master


## [2.0.255] - 2026-06-16

### Changed
- Automatic version bump on merge to master


## [2.0.256] - 2026-06-19

### Changed
- Automatic version bump on merge to master


## [2.0.257] - 2026-06-24

### Changed
- Automatic version bump on merge to master


## [2.0.258] - 2026-06-24

### Changed
- Automatic version bump on merge to master


## [2.0.259] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.260] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.261] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.262] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.263] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.264] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.265] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.266] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.267] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.268] - 2026-06-27

### Changed
- Automatic version bump on merge to master


## [2.0.269] - 2026-06-29

### Changed
- Automatic version bump on merge to master


## [2.0.270] - 2026-06-30

### Changed
- Automatic version bump on merge to master


## [2.0.271] - 2026-06-30

### Changed
- Automatic version bump on merge to master


## [2.0.272] - 2026-07-11

### Changed
- Automatic version bump on merge to master


## [2.0.273] - 2026-07-11

### Changed
- Automatic version bump on merge to master


## [2.0.274] - 2026-07-12

### Changed
- Automatic version bump on merge to master


## [2.0.275] - 2026-07-15

### Changed
- Automatic version bump on merge to master


## [2.0.276] - 2026-07-21

### Changed
- Automatic version bump on merge to master


## [2.0.277] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.278] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.279] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.280] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.281] - 2026-07-23

### Changed
- Automatic version bump on merge to master


## [2.0.282] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.283] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.284] - 2026-07-24

### Changed
- Automatic version bump on merge to master


## [2.0.285] - 2026-07-25

### Changed
- Automatic version bump on merge to master


## [2.0.286] - 2026-09-10

### Changed
- Automatic version bump on merge to master


## [2.0.287] - 2026-09-12

### Changed
- Automatic version bump on merge to master


## [2.0.288] - 2026-09-13

### Changed
- Automatic version bump on merge to master


## [2.0.289] - 2026-09-15

### Changed
- Automatic version bump on merge to master


## [2.0.290] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.291] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.292] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.293] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.294] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.295] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.296] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.297] - 2026-09-18

### Changed
- Automatic version bump on merge to master


## [2.0.298] - 2026-09-20

### Changed
- Automatic version bump on merge to master


## [2.0.299] - 2026-09-20

### Changed
- Automatic version bump on merge to master


## [2.0.300] - 2026-09-22

### Changed
- Automatic version bump on merge to master


## [2.0.301] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.302] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.303] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.304] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.305] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.306] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.307] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.308] - 2026-09-25

### Changed
- Automatic version bump on merge to master


## [2.0.309] - 2026-09-28

### Changed
- Automatic version bump on merge to master


## [2.0.310] - 2026-09-29

### Changed
- Automatic version bump on merge to master


## [2.0.311] - 2026-09-29

### Changed
- Automatic version bump on merge to master


## [2.0.312] - 2026-09-30

### Changed
- Automatic version bump on merge to master


## [2.0.313] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.314] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.315] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.316] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.317] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.318] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.319] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.320] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.321] - 2026-10-01

### Changed
- Automatic version bump on merge to master


## [2.0.322] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.323] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.324] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.325] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.326] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.327] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.328] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.329] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.330] - 2026-10-14

### Changed
- Automatic version bump on merge to master


## [2.0.331] - 2026-10-15

### Changed
- Automatic version bump on merge to master


## [2.0.332] - 2026-10-15

### Changed
- Automatic version bump on merge to master


## [2.0.333] - 2026-10-15

### Changed
- Automatic version bump on merge to master


## [2.0.334] - 2026-10-15

### Changed
- Automatic version bump on merge to master


## [2.0.335] - 2026-10-16

### Changed
- Automatic version bump on merge to master


## [2.0.336] - 2026-10-16

### Changed
- Automatic version bump on merge to master


## [2.0.337] - 2026-10-17

### Changed
- Automatic version bump on merge to master


## [2.0.338] - 2026-10-17

### Changed
- Automatic version bump on merge to master


## [2.0.339] - 2026-10-19

### Changed
- Automatic version bump on merge to master


## [2.0.340] - 2026-10-19

### Changed
- Automatic version bump on merge to master


## [2.0.341] - 2026-10-20

### Changed
- Automatic version bump on merge to master


## [2.0.342] - 2026-10-20

### Changed
- Automatic version bump on merge to master


## [2.0.343] - 2026-10-20

### Changed
- Automatic version bump on merge to master


## [2.0.344] - 2026-10-20

### Changed
- Automatic version bump on merge to master


## [2.0.345] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.346] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.347] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.348] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.349] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.350] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.351] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.352] - 2026-10-26

### Changed
- Automatic version bump on merge to master


## [2.0.353] - 2026-10-27

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
