# Email to E-Reader

ComicMaintainer can email comics to saved e-reader addresses (Kindle "Send to Kindle",
Kobo, PocketBook, or any mailbox that accepts attachments). Issues can be sent one at a
time, as a whole series, or as an arbitrary selection, and a series can be subscribed so
that every newly processed issue is delivered automatically. Each delivery can be sent as
the original `.cbz`/`.cbr` archive or converted to EPUB first.

## Contents

- [Configuring SMTP](#configuring-smtp)
- [E-reader devices](#e-reader-devices)
- [Sending comics](#sending-comics)
- [Automatic delivery for a series](#automatic-delivery-for-a-series)
- [EPUB conversion](#epub-conversion)
- [Delivery pipeline](#delivery-pipeline)
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

## E-reader devices

A device is a saved name, email address and default delivery format.

- **Name** – free text, shown in the send menus (e.g. "Paperwhite").
- **Email address** – a single bare mailbox (`user@example.com`). Display-name forms
  (`Name <user@example.com>`) and lists are rejected, and addresses must be unique.
- **Delivery format** – `original` (send the archive untouched) or `epub` (convert
  before sending).

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
  `__MACOSX/` entries are ignored.
- Each page is a minimal XHTML document whose viewport matches the image dimensions, so
  pages are displayed whole rather than reflowed.
- The cached series artwork (the same image the library shows for the series) is embedded
  as a dedicated cover page, marked `cover-image`, placed first in the spine and referenced
  from the navigation landmarks. When no series image is cached — or the cached file is
  missing or undecodable — the first comic page is marked as the cover instead, as before.
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

## Delivery pipeline

1. The request validates the device, format and file paths and inserts one delivery row
   per file with status `pending`.
2. Delivery ids are pushed onto an in-process queue drained by a single background
   consumer, so SMTP work never blocks an API request or the file watcher, and the mail
   server is not hit concurrently.
3. For each delivery the archive is converted if required, checked against
   `EmailMaxAttachmentMegabytes`, and sent.
4. The row is marked `sent` or `failed` (with the error message) and any temporary file
   is removed.

Deliveries still queued when the application stops stay `pending` in the database; they
are not resumed automatically, but re-sending is a no-op-free operation because
`skipAlreadyDelivered` treats `pending` rows as already handled.

Recent deliveries — including failures and their error text — are available from
`GET /api/email/deliveries`, and are shown in the UI under
**⋮ menu → 📨 Email Delivery History** (also reachable from the Ereader Devices dialog).

## API reference

All routes are under `/api/email`. Write operations require the `CanModifyLibrary`
policy; the SMTP settings endpoint requires `CanAdminister`.

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
| `GET` | `/api/email/subscriptions` | List subscriptions, optionally filtered by series. |
| `PUT` | `/api/email/subscriptions` | Create or update a series subscription. |
| `DELETE` | `/api/email/subscriptions/{id}` | Remove a subscription. |
| `GET` | `/api/email/deliveries` | Recent delivery history. |
| `PUT` | `/api/settings/email` | Update SMTP settings. |

Example — queue two issues as EPUB:

```sh
curl -X PUT http://localhost:5000/api/email/subscriptions \
  -H "Content-Type: application/json" \
  -d '{"seriesTitle":"Batman","deviceId":1,"deliveryFormat":"epub","enabled":true}'

curl -X POST http://localhost:5000/api/email/send \
  -H "Content-Type: application/json" \
  -d '{"files":["/comics/Batman/Batman - Chapter 0001.cbz"],"deviceId":1,"deliveryFormat":"epub"}'
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
