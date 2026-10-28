# Email to E-Reader

ComicMaintainer can email comics to saved e-reader addresses (Kindle "Send to Kindle",
Kobo, PocketBook, or any mailbox that accepts attachments). Issues can be sent one at a
time, as a whole series, or as an arbitrary selection, and a series can be subscribed so
that every newly processed issue is delivered automatically. Each delivery can be sent as
the original `.cbz`/`.cbr` archive or converted to EPUB or AZW3 first, and consecutive
issues can be condensed into a single EPUB or AZW3.

## Contents

- [Configuring SMTP](#configuring-smtp)
- [Rate limiting](#rate-limiting)
- [E-reader devices](#e-reader-devices)
- [Sending comics](#sending-comics)
- [Condensing issues into one book](#condensing-issues-into-one-book)
- [Automatic delivery for a series](#automatic-delivery-for-a-series)
- [EPUB conversion](#epub-conversion)
- [AZW3 conversion](#azw3-conversion)
- [Delivery pipeline](#delivery-pipeline)
- [Delivery history and cancelling](#delivery-history-and-cancelling)
- [API reference](#api-reference)
- [Troubleshooting](#troubleshooting)

## Configuring SMTP

Email is disabled until an SMTP host and a from-address are configured. Set them in
**Settings → Email Delivery** in the web interface, or with environment variables.

| Environment variable | Setting | Default | Description |
|---|---|---|---|
| `SMTP_HOST` | `SmtpHost` | *(empty)* | SMTP server host name. Email is disabled while empty. |
| `SMTP_PORT` | `SmtpPort` | `587` | SMTP port. `587` is submission (STARTTLS); `465` is implicit TLS. |
| `SMTP_USERNAME` | `SmtpUsername` | *(empty)* | Username for authentication. Leave empty for anonymous relays. |
| `SMTP_PASSWORD` | `SmtpPassword` | *(empty)* | Password for authentication. Never returned by the API and redacted in logs. |
| `SMTP_USE_SSL` | `SmtpUseSsl` | `false` | Connect with **implicit TLS** (SMTPS, port 465). Leave `false` for port 587, which is upgraded with STARTTLS. |
| `SMTP_ALLOW_INSECURE` | `SmtpAllowInsecure` | `false` | Allow the connection to stay plaintext when the server does not advertise STARTTLS. Only enable for a trusted local relay. |
| `EMAIL_FROM_ADDRESS` | `EmailFromAddress` | *(empty)* | Envelope sender. Must be an address your e-reader service accepts. |
| `EMAIL_FROM_NAME` | `EmailFromName` | `ComicMaintainer` | Display name on outgoing mail. |
| `EMAIL_MAX_ATTACHMENT_MB` | `EmailMaxAttachmentMegabytes` | `25` | Deliveries larger than this fail instead of being sent. |
| `EMAIL_MAX_SENDS_PER_HOUR` | `EmailMaxSendsPerHour` | `0` | Most comic emails in any rolling 60 minutes (`0`–`10000`; `0` = no limit). See [Rate limiting](#rate-limiting). |
| `EMAIL_MAX_SENDS_PER_DAY` | `EmailMaxSendsPerDay` | `0` | Most comic emails in any rolling 24 hours (`0`–`100000`; `0` = no limit). |
| `EMAIL_MIN_SECONDS_BETWEEN_SENDS` | `EmailMinSecondsBetweenSends` | `0` | Minimum gap between two comic emails, in seconds (`0`–`86400`; `0` = no gap). |

When `SMTP_USE_SSL` is `false` and `SMTP_ALLOW_INSECURE` is `false` (the defaults),
STARTTLS is *required*: a server that does not offer it is refused rather than being
handed credentials and attachments in the clear.

Environment variables take precedence over values saved in `user-settings.json`, exactly
as for the rest of the application settings.

> **Kindle users:** add `EMAIL_FROM_ADDRESS` to your Amazon "Approved Personal Document
> E-mail List", otherwise Amazon silently drops the message even though the SMTP send
> succeeded.

Settings are updated through `PUT /api/settings/email` (administrators only). Omit
`smtpPassword` from the request to keep the stored password; send an empty string to
clear it.

## Rate limiting

Mail providers throttle or suspend accounts that send many messages in a short time, and
e-reader services can reject a burst of documents. A whole-series send can queue hundreds
of emails, so ComicMaintainer can spread comic emails out instead of sending them back to
back. Set the limits in **Settings → Email Delivery** (or with the environment variables
in the [table above](#configuring-smtp)). Each limit is off at `0`, the default, and any
combination can be used:

- **Max emails per hour** – at most this many comic emails in any rolling 60 minutes.
- **Max emails per day** – at most this many comic emails in any rolling 24 hours.
- **Minimum seconds between emails** – the shortest gap between two comic emails.

How the limits are applied:

- The windows are rolling, not calendar hours or days: with a limit of 10 per hour, the
  11th email waits until the oldest of the last ten is an hour old. When several limits
  apply, the delivery waits for the strictest one.
- Only emails that were actually sent count. Failed and cancelled deliveries do not use up
  the allowance, and a condensed book is one email however many issues it holds. Test
  emails are neither counted nor held back.
- Deliveries are sent one at a time, in the order they were queued. When the limit is
  reached the next delivery waits *before* it is converted, so no converted book sits on
  disk while it waits, and everything queued behind it waits too. The deliveries stay
  `pending` and go out on their own as the limit allows.
- New limits apply to a delivery that is already waiting within 30 seconds, so raising or
  removing a limit releases it promptly.
- While a delivery is held back, **Email Delivery History** shows *"⏸ The email rate
  limit is holding pending deliveries back until …"*, and `GET /api/email/deliveries`
  reports the same time as `rate_limited_until`. A waiting delivery can be
  [cancelled](#delivery-history-and-cancelling).
- When the application stops, a delivery waiting for the rate limit stops waiting and stays
  `pending` (see [Delivery pipeline](#delivery-pipeline)).

The rate limit is updated through `PUT /api/settings/email-rate-limit` (administrators
only), which replaces all three values; an omitted field is set to `0`. Out-of-range values
are rejected with a 400 and nothing is saved. Environment variables take precedence over
the saved values, as for the SMTP settings.

```sh
curl -X PUT http://localhost:5000/api/settings/email-rate-limit \
  -H "Content-Type: application/json" \
  -d '{"maxSendsPerHour":20,"maxSendsPerDay":100,"minSecondsBetweenSends":60}'
```

## E-reader devices

A device is a saved name, email address and default delivery format.

- **Name** – free text, shown in the send menus (e.g. "Paperwhite").
- **Email address** – a single bare mailbox (`user@example.com`). Display-name forms
  (`Name <user@example.com>`) and lists are rejected, and addresses must be unique.
- **Delivery format** – `original` (send the archive untouched), `epub`, or `azw3`
  (convert before sending).

Devices are managed from **⋮ menu → 📧 Ereader Devices**. Managing devices only needs
the `CanModifyLibrary` policy, so it is deliberately *not* inside the Settings modal
(which is administrator-only) — the same dialog is also linked from the SMTP section of
Settings for convenience.

Use **Send test email** after adding a device: it sends a short message with no
attachment and reports the SMTP error verbatim if the server rejects it.

## Sending comics

Several entry points exist in the web interface, all of which queue work and return
immediately:

| Action | Where | Endpoint |
|---|---|---|
| Single issue | **📧 Email to Ereader…** in a file row or series issue ⋮ menu | `POST /api/email/send` |
| Selection | **📧 Email Selected** in the bulk actions bar, the series-detail selection bar, or the series-list selection toolbar | `POST /api/email/send` |
| Whole series | **📧 Email All Issues** in the series ⚙️ Actions menu | `POST /api/email/send-series` |
| Current comic | The **📧 Email** button in the reader toolbar | `POST /api/email/send` |

Every send picks a device and a format. Format `device` (the default) uses the format
saved on the device, so changing the device later changes future sends.

The send dialog checks `GET /api/email/status` and the saved device list before it is
usable: if no devices exist it offers an **Add Ereader Device** shortcut instead of an
empty picker, and if SMTP is not configured it disables **Send** and says so, rather than
failing only after the button is pressed.

All send actions require the `CanModifyLibrary` policy and are hidden from read-only
users.

`skipAlreadyDelivered` (on by default) suppresses files that already have a pending or
sent delivery for the same device, which makes re-sending a series safe. The response
reports `queued` and `skipped` counts. The reader's **📧 Email** button deliberately sets
it to `false`, because sending the issue you are currently reading is always an explicit
request.

A whole-series send is capped at 1000 issues; a larger series is rejected with an error
rather than silently truncated.

Only files inside the watched directory (or the configured duplicate directory) can be
sent; any other path is rejected. Symlinks and junctions are resolved first — on the file
and on every parent directory — so a link inside the library cannot point at a file
outside it.

## Condensing issues into one book

Instead of one book per issue, a send can *condense* consecutive issues into a single
book, so a device receives "Batman 001-005" rather than five separate books.

- **Send each issue separately** – the default, unchanged behaviour.
- **Combine every N issues into one book** – the issues are put in reading order and
  grouped into books of N: issues 1-5, then 6-10, and so on. N is between 2 and 500.
- **Combine all issues into one book** – the whole selection (or the whole series)
  becomes a single book.

A book never mixes series: a selection spanning several series is grouped by series first
and each group is chunked on its own. A send-series request is condensed in the issue
order the series returns, even when the series is spread over several folders.

### Choosing the condensed format

A condensed book can be delivered as EPUB or AZW3 — pick either in the **Format** picker,
which stays available while a condense mode is selected. Only **Original archive** (and
**Device default**, which may resolve to it) is unavailable, because `.cbz`/`.cbr`
archives cannot be merged; the picker hides those entries while condensing and restores
the previous choice when condensing is turned off, except that choosing EPUB or AZW3
while condensing clears the remembered pre-condense choice. `POST /api/email/send`,
`/send-series`, `/condense-download` and `/condense-builds` take the same
`deliveryFormat`: `epub` (the default) or `azw3`; omitting it inherits the device default
and falls back to EPUB when that device sends original archives, and `original` is
rejected with a 400.

An AZW3 is built from the same condensed EPUB (see [AZW3 conversion](#azw3-conversion)),
so both formats contain exactly the same pages. The condensed book keeps the series
metadata of its first issue, opens on the series cover page (see
[EPUB conversion](#epub-conversion)), numbers its pages continuously, and gets one
table-of-contents entry per issue so the device can still jump between them. Because it
spans a range, no single issue number is written to the series position metadata.

### Size check and the download fallback

Mailboxes cap attachment size (`EMAIL_MAX_ATTACHMENT_MB`, 25 MB by default), and a book
made of dozens of issues will not fit. Before anything is queued the send dialog asks
`POST /api/email/condense-plan`, which reports each book, how many issues it holds and
its estimated size — the pages keep their original compression, so the sum of the source
archives plus the container overhead is a close approximation.

Books over the limit are flagged, **Send** is disabled, and each book can be downloaded
instead (with no size budget, so the pages keep full quality). The dialog lists ten books
at a time with a **Show more** control, so every book — including an oversized one far
down a long plan — has its own Download button. Reducing the issues per book is the other
way out. `POST /api/email/send` and `/send-series` apply the same size check, so an
oversized book is rejected with an error rather than queued.

### Tracking a download build

Condensing hundreds of issues takes minutes — longer than a browser or a reverse proxy
will hold a request open — so a download is not built inside the request that asks for
it. **Download** posts to `POST /api/email/condense-builds`, which validates the request,
registers a build and returns immediately with a `build_id`. The build then runs detached
from that request, so closing the dialog, navigating away or losing the connection does
not abandon it.

The dialog polls `GET /api/email/condense-builds` every two seconds and shows each build
with a progress bar, the phase it is in (reading the source archives, writing pages,
validating, or recompressing when a size budget forces another pass), the issue being
written and the page count of the current pass. A finished build offers **Download**; a
failed one shows the error that stopped it. **Cancel** stops a build that is still
running, and **Dismiss** forgets a finished one and deletes its file.

Details worth knowing:

- Builds are **per user**: a build is only visible to, downloadable by and cancellable by
  the account that started it.
- Only **one build runs at a time** per instance. Additional builds are accepted with
  status `queued` and start when the one ahead of them finishes, so a big selection
  cannot be turned into many parallel conversions.
- Clicking **Download** twice for the same book does not start a second build; the request
  is fingerprinted and an identical build that is still active is returned instead.
- A finished build is kept for **30 minutes**, and its file stays on disk for that long,
  so an interrupted download can simply be retried. After that the build and its file are
  removed; the 20 most recent finished builds per user are kept.
- Because the build outlives the request, a failure is reported as a build status rather
  than being lost when the connection drops.

`POST /api/email/condense-download` still exists and builds one book synchronously in the
request, streaming it back and deleting the temporary file afterwards. It is retained for
scripts and is only suitable for small books.

Condensed sends honour `skipAlreadyDelivered`, and an issue that already went out *inside
an earlier condensed book* counts as delivered — for ordinary per-issue sends too, so an
issue is never mailed twice. Pass the same `deviceId` and `skipAlreadyDelivered` to
`condense-plan` and to the build as to the send, or the plan will describe different books
than the ones queued.

## Automatic delivery for a series

A series can be subscribed to a device. After the watcher finishes processing a file —
after any rename and metadata normalization — a delivery is queued automatically for each
enabled subscription whose series matches.

- Matching uses the same normalized series key as the rest of the library, and matches
  both the metadata `Series` value and the containing folder name, because the library
  groups by folder name first.
- A subscription may override the device format, or inherit it with `device`.
- Subscriptions can be disabled without deleting them.
- Automatic deliveries always skip files that were already delivered to that device, so
  reprocessing an existing file does not resend it.
- A subscription's "last sent" timestamp is stamped only once one of its deliveries has
  actually been sent, not when it is queued.

Deleting a device deletes its subscriptions.

## EPUB conversion

Conversion produces a fixed-layout EPUB 3 containing one page per image, which is what
e-readers expect for comics:

- Images are ordered with the same natural sort used elsewhere in the application, and
  `__MACOSX/` entries are ignored. A root-level `cover.<ext>` entry is cover art rather
  than an ordinary page — ComicMaintainer embeds the series cover into the first archive
  of a series that way — so it is moved to the front of its issue instead of sorting in
  among (or after) the numbered pages. A `cover.<ext>` inside a sub-folder stays an
  ordinary page.
- Each page is a minimal XHTML document whose viewport matches the image dimensions, so
  pages are displayed whole rather than reflowed.
- The series artwork (the same image the library shows for the series) is embedded as a
  dedicated cover page, marked `cover-image`, placed first in the spine and referenced
  from the navigation landmarks, so every book — single issue or condensed — opens on the
  series image. It is taken from the cached series image, and when the series has none
  (or the cached file is gone) from the `cover.<ext>` sidecar written next to the issues.
  An embedded `cover.<ext>` entry that is a byte-identical copy of that artwork is dropped
  from the pages so the same image is not shown twice in a row — unless it is the
  archive's only image, which is kept as the sole page.
- When no series artwork can be resolved at all — or the file is missing or undecodable —
  the first comic page is marked as the cover instead, as before.
- Series, issue number and the series collection are written to the OPF metadata in both
  the EPUB 3 form (`belongs-to-collection` / `collection-type` / `group-position`) and the
  legacy calibre form (`calibre:series` / `calibre:series_index`), because readers support
  one or the other and rarely both.
- The issue number in the title is zero-padded to three digits (`Series Name #012`, and
  `#012.5` for half issues). Kindle has no series support for sideloaded books and sorts by
  title, so an unpadded `#10` would sort before `#2`. The same padded value is written as
  the title's `file-as` sort key. Numbers that are not numeric at all (`Annual`) are kept
  verbatim, and decorated numbers (`#42`, `007`, `12a`) are normalised to a plain value for
  the collection position.
- The file is written to a temporary name and moved into place, so an interrupted
  conversion cannot leave a truncated EPUB behind.

Converted files are temporary: they are created for the delivery and removed once the
message has been sent.

## AZW3 conversion

AZW3 (Kindle Format 8) is Amazon's own fixed-layout format. A Kindle renders a sideloaded
AZW3 comic with the panel/page handling it uses for its own store comics, whereas an EPUB
sent to a Kindle is converted by Amazon's service on the way in, which can re-compress the
pages. Send AZW3 to Kindles; send EPUB to Kobo, PocketBook and everything else.

Conversion is performed natively by ComicMaintainer — no Calibre, `kindlegen` or any other
external tool is needed, and the container image gains no extra dependencies.

- The book is first built with the EPUB converter described above, so AZW3 inherits its
  page ordering, series cover page, padded issue title, and size tiering. The AZW3 carries
  the title, creator, publisher and language metadata; EPUB3 and calibre series metadata
  are not copied. The intermediate EPUB is written to a staging directory that is always
  removed, so only the `.azw3` file is left behind.
- The EPUB is then rewritten as a single-file KF8 book: a PalmDB container holding the
  uncompressed UTF-8 page documents, the skeleton and chunk (fragment) indices a Kindle
  uses to reassemble each page, and one image resource record per page referenced with
  `kindle:embed:`.
- The EXTH metadata marks the book as a fixed-layout comic with no gutter or margin and a
  locked orientation (`fixed-layout`, `book-type=comic`, `orientation-lock=none`,
  `original-resolution`), which is what makes a Kindle show one full page at a time.
- Compression tier selection uses the intermediate EPUB's size budget. The completed AZW3
  is then checked against the attachment limit and rejected if it exceeds the limit.
  Because the pages are the same images as in the EPUB, the attachment size is
  approximately the same.

Condensed books support AZW3 as well: the issues are merged into one intermediate EPUB
first and that book is rewritten as KF8, exactly as a single issue is.

## Delivery pipeline

1. The request validates the device, format and file paths and inserts one delivery row
   per file with status `pending`.
2. Delivery ids are pushed onto an in-process queue drained by a single background
   consumer, so SMTP work never blocks an API request or the file watcher, and the mail
   server is not hit concurrently.
3. For each delivery the consumer first waits for the [rate limit](#rate-limiting), if
   one is configured; then the archive is converted if required, checked against
   `EmailMaxAttachmentMegabytes`, and sent.
4. The row is marked `sent` or `failed` (with the error message), or `cancelled` if it
   was [cancelled](#delivery-history-and-cancelling) before it was sent, and any temporary
   file is removed.

Deliveries still queued when the application stops — including one waiting for the rate
limit — stay `pending` in the database and are **not** resumed when it starts again.
Because `skipAlreadyDelivered` treats `pending` rows as already handled, simply re-sending
would skip them; use **Cancel All Pending** in Email Delivery History first (after a
restart nothing is in progress, so they are cancelled immediately), then send again —
cancelled deliveries do not count as delivered.

Deliveries — including failures and their error text — are listed by
`GET /api/email/deliveries`, and are shown in the UI under
**⋮ menu → 📨 Email Delivery History** (also reachable from the Ereader Devices dialog).

## Delivery history and cancelling

**⋮ menu → 📨 Email Delivery History** lists every delivery, newest first, 50 to a page.
**Previous** and **Next** page through the whole history, and the page counter shows the
total. Each entry shows its status — ⏳ Pending, ✅ Sent, ❌ Failed or 🚫 Cancelled —
along with the device, the format and, for failures, the error.

A delivery can be cancelled until its message is handed to the mail server:

- Every pending delivery has a **Cancel** button. A delivery still waiting in the queue is
  cancelled at once and will be skipped when its turn comes.
- A delivery that is being *prepared* — waiting for the rate limit, or being converted to
  EPUB or AZW3 — is asked to stop: the conversion is abandoned, its temporary file is
  removed and the delivery is marked `cancelled`. If it has not stopped within five
  seconds, the dialog says it is still stopping; it is marked `cancelled` as soon as the
  current step finishes.
- Once the message is being sent it can no longer be recalled. Cancelling then reports
  that the delivery is already being sent, and it finishes as `sent` or `failed`.
- **Cancel All Pending (N)** cancels every pending delivery on every page, not just the
  page on display, and reports how many were cancelled, how many are still stopping and
  how many were already being sent.

Cancelling requires the `CanModifyLibrary` policy, like sending, so the buttons are hidden
from read-only users.

A cancelled delivery does not count as delivered, the same as a failed one: re-sending
with `skipAlreadyDelivered` queues the file again, and a series subscription queues it
again if the watcher processes the file again.

## API reference

All routes are under `/api/email`. Write operations require the `CanModifyLibrary`
policy; the SMTP and rate-limit settings endpoints require `CanAdminister`.

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/email/status` | Whether email is configured and usable. |
| `GET` | `/api/email/devices` | List saved devices. |
| `POST` | `/api/email/devices` | Create a device. |
| `PUT` | `/api/email/devices/{id}` | Update a device; omitted fields are unchanged. |
| `DELETE` | `/api/email/devices/{id}` | Delete a device and its subscriptions. |
| `POST` | `/api/email/devices/{id}/test` | Send a test message. |
| `POST` | `/api/email/send` | Queue specific files. |
| `POST` | `/api/email/send-series` | Queue every issue of a series. |
| `POST` | `/api/email/condense-plan` | Describe the condensed books a selection or series would produce, with size estimates. |
| `POST` | `/api/email/condense-download` | Build and stream one condensed book in the request (legacy; small books only). |
| `POST` | `/api/email/condense-builds` | Start a tracked background build of one condensed book. |
| `GET` | `/api/email/condense-builds` | List the caller's builds with status and progress. |
| `GET` | `/api/email/condense-builds/{buildId}` | Status and progress of one build. |
| `GET` | `/api/email/condense-builds/{buildId}/download` | Download the file of a completed build. |
| `POST` | `/api/email/condense-builds/{buildId}/cancel` | Cancel a queued or running build. |
| `DELETE` | `/api/email/condense-builds/{buildId}` | Forget a build and delete its file. |
| `GET` | `/api/email/subscriptions` | List subscriptions, optionally filtered by series. |
| `PUT` | `/api/email/subscriptions` | Create or update a series subscription. |
| `DELETE` | `/api/email/subscriptions/{id}` | Remove a subscription. |
| `GET` | `/api/email/deliveries` | One page of the delivery history, newest first. `limit` (default 50, clamped to 1–500) and `offset` (default 0) select the page. Returns `deliveries`, `total`, `offset`, `limit`, `pending_count` (pending deliveries on every page) and `rate_limited_until` (UTC time the rate limit is holding the next delivery until, otherwise `null`). |
| `POST` | `/api/email/deliveries/{id}/cancel` | Cancel a delivery that has not been sent. `200` `{cancelled: true, delivery}` once cancelled; `202` `{cancelled: false, stopping: true, delivery}` while a delivery being prepared is still stopping; `404` for an unknown id; `409` `{error, delivery}` when it is already being sent or has finished. |
| `POST` | `/api/email/deliveries/cancel-pending` | Cancel every pending delivery. Returns the counts `{cancelled, stopping, sending}`. |
| `PUT` | `/api/settings/email` | Update SMTP settings. |
| `PUT` | `/api/settings/email-rate-limit` | Update the [rate limit](#rate-limiting): `maxSendsPerHour`, `maxSendsPerDay`, `minSecondsBetweenSends` (`0` = no limit). |

Example — queue two issues as EPUB:

```sh
curl -X PUT http://localhost:5000/api/email/subscriptions \
  -H "Content-Type: application/json" \
  -d '{"seriesTitle":"Batman","deviceId":1,"deliveryFormat":"epub","enabled":true}'

curl -X POST http://localhost:5000/api/email/send \
  -H "Content-Type: application/json" \
  -d '{"files":["/comics/Batman/Batman - Chapter 0001.cbz"],"deviceId":1,"deliveryFormat":"epub"}'
```

Example — check what condensing a series into books of five issues would produce, then
queue it:

```sh
curl -X POST http://localhost:5000/api/email/condense-plan \
  -H "Content-Type: application/json" \
  -d '{"seriesId":"batman","deviceId":1,"skipAlreadyDelivered":true,"condenseMode":"count","issuesPerBook":5}'

curl -X POST http://localhost:5000/api/email/send-series \
  -H "Content-Type: application/json" \
  -d '{"seriesId":"batman","deviceId":1,"condenseMode":"count","issuesPerBook":5,"deliveryFormat":"azw3"}'
```

## Troubleshooting

| Symptom | Cause |
|---|---|
| "Email is not configured" | `SmtpHost` or `EmailFromAddress` is empty. |
| Test email succeeds but nothing arrives | The receiving service is filtering the sender; for Kindle, approve the from-address. |
| Delivery fails with a size error | The attachment exceeds `EMAIL_MAX_ATTACHMENT_MB`, or the receiving service has a lower limit of its own. |
| Nothing is sent automatically | The subscription is disabled, the series name does not match the folder or metadata series, or the file was already delivered to that device. |
| Authentication errors | Providers with 2FA usually require an app-specific password rather than the account password. |
| "The SMTP server does not support the STARTTLS extension" | The server offers no TLS. Use implicit TLS (`SMTP_USE_SSL=true`, port 465) or, for a trusted local relay only, set `SMTP_ALLOW_INSECURE=true`. |
| "Series has N issues, which exceeds the ... limit" | A whole-series send is capped at 1000 issues; select the issues to send instead. |
| Send is disabled with "larger than the ... attachment limit" | The condensed book(s) exceed `EMAIL_MAX_ATTACHMENT_MB`. Download them instead, or condense fewer issues per book. |
| Deliveries stay ⏳ Pending and the history says the rate limit is holding them back | The configured [rate limit](#rate-limiting) has been reached. They are sent automatically as it allows; raise or clear the limit in Settings (applied within 30 seconds), or cancel the ones you no longer want. |
| Deliveries stay ⏳ Pending after a restart | Queued deliveries are not resumed when the application starts. Use **Cancel All Pending**, then send them again. |
| Cancel reports "already being sent" | The message was already being handed to the mail server and cannot be recalled; it finishes as sent or failed. |
