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
| `SMTP_PORT` | `SmtpPort` | `587` | SMTP port. `465` implies implicit SSL. |
| `SMTP_USERNAME` | `SmtpUsername` | *(empty)* | Username for authentication. Leave empty for anonymous relays. |
| `SMTP_PASSWORD` | `SmtpPassword` | *(empty)* | Password for authentication. Never returned by the API and redacted in logs. |
| `SMTP_USE_SSL` | `SmtpUseSsl` | `false` | Use implicit TLS (SMTPS, usually port 465). When false, use STARTTLS when available. |
| `EMAIL_FROM_ADDRESS` | `EmailFromAddress` | *(empty)* | Envelope sender. Must be an address your e-reader service accepts. |
| `EMAIL_FROM_NAME` | `EmailFromName` | `ComicMaintainer` | Display name on outgoing mail. |
| `EMAIL_MAX_ATTACHMENT_MB` | `EmailMaxAttachmentMegabytes` | `25` | Deliveries larger than this fail instead of being sent. |

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

Use **Send test email** after adding a device: it sends a short message with no
attachment and reports the SMTP error verbatim if the server rejects it.

## Sending comics

Three entry points exist in the web interface, all of which queue work and return
immediately:

| Action | Where | Endpoint |
|---|---|---|
| Single issue | The ✉ action on a file row | `POST /api/email/send` |
| Selection | "Email Selected" in the bulk actions bar | `POST /api/email/send` |
| Whole series | The series card menu | `POST /api/email/send-series` |

Every send picks a device and a format. Format `device` (the default) uses the format
saved on the device, so changing the device later changes future sends.

`skipAlreadyDelivered` (on by default) suppresses files that already have a pending or
sent delivery for the same device, which makes re-sending a series safe. The response
reports `queued` and `skipped` counts.

Only files inside the watched directory (or the configured duplicate directory) can be
sent; any other path is rejected.

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

Deleting a device deletes its subscriptions.

## EPUB conversion

Conversion produces a fixed-layout EPUB 3 containing one page per image, which is what
e-readers expect for comics:

- Images are ordered with the same natural sort used elsewhere in the application, and
  `__MACOSX/` entries are ignored.
- Each page is a minimal XHTML document whose viewport matches the image dimensions, so
  pages are displayed whole rather than reflowed.
- The first image is marked as the cover.
- Series, issue number and the series collection are written to the OPF metadata.
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
`GET /api/email/deliveries`.

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
