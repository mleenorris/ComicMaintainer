# Notifications

ComicMaintainer can tell you when something happens, and can ask a media server to rescan the library.

| Target | What it does |
|---|---|
| ntfy | Publishes a message to an ntfy topic |
| Gotify | Pushes a message to a Gotify server |
| Discord | Posts to a Discord webhook |
| JSON webhook | POSTs a JSON document to a URL of your choice |
| Komga scan | Asks Komga to scan its libraries |
| Kavita scan | Asks Kavita to scan its libraries |

Set them up under **Settings > Notifications**. Only administrators can change these settings or use Send Test. Each target has its own switch and its own list of events, so you can, for example, send failures to ntfy and new chapters to Discord. Everything is off until you enable a target.

Addresses on your own network are allowed, because a Komga or Kavita server on your LAN is the normal setup.

## Setting Up a Target

Save your settings before using **Send Test**, because the test uses the saved values.

### ntfy

| Field | Required | Value |
|---|---|---|
| Server URL | Yes | For example `https://ntfy.sh` |
| Topic | Yes | The topic to publish to |
| Access Token | Only if the topic requires one | An ntfy access token |

Failures are sent at a higher priority than other messages.

On a public ntfy server, anyone who knows the topic name can subscribe to it, so choose a name that is hard to guess.

### Gotify

| Field | Required | Value |
|---|---|---|
| Server URL | Yes | For example `https://gotify.example.com` |
| Application Token | Yes | The token of a Gotify application |

Failures are sent at a higher priority than other messages.

### Discord

| Field | Required | Value |
|---|---|---|
| Webhook URL | Yes | From the channel's Integrations settings, such as `https://discord.com/api/webhooks/...` |

Series and chapter names can never mention or ping anyone, and long messages are cut to Discord's 2000-character limit.

### JSON Webhook

| Field | Required | Value |
|---|---|---|
| Webhook URL | Yes | Any URL that accepts a POST, such as `https://example.com/hooks/...` |

See [The JSON Webhook Body](#the-json-webhook-body).

### Komga Scan

| Field | Required | Value |
|---|---|---|
| Server URL | Yes | For example `http://komga.local:25600` |
| API Key | Yes | An API key belonging to a Komga **administrator**, because the scan call requires that role |
| Library ID | No | The library to scan. Leave blank to scan every library |

### Kavita Scan

| Field | Required | Value |
|---|---|---|
| Server URL | Yes | For example `http://kavita.local:5000` |
| Auth Key | Yes | An Auth Key from an administrator account, found in Kavita's User Settings. Kavita versions without Auth Keys are not supported |
| Library ID | No | The numeric ID of the library to scan. Leave blank to scan every library |

## Events

### Message Targets (ntfy, Gotify, Discord, JSON Webhook)

| Event | Setting label | When it fires |
|---|---|---|
| `new-chapters` | New chapters | A chapter arrives in the library |
| `suwayomi-import-failed` | Failed Suwayomi imports | A Suwayomi import pass could not bring every chapter in |
| `job-failed` | Failed scheduled jobs | A scheduled job fails |
| `job-findings` | Scheduled jobs that report findings | A scheduled job finishes and reports something to look at |

All four are selected by default.

A chapter counts as new only if its file was first tracked within the last 7 days and it was not a forced reprocess, so reprocessing old files does not announce them.

An unexpected crash during a Suwayomi import raises both `suwayomi-import-failed` and `job-failed`. A target that chose both gets two messages about it.

### Komga and Kavita

| Event | Setting label | When it fires |
|---|---|---|
| `new-chapters` | New chapters arrive | A chapter arrives in the library |
| `library-changed` | Existing files change | A file is renamed, tagged or deleted, or a cover is written into an archive |

Both are selected by default. A `cover.jpg` written beside the archives does not trigger a scan.

## How Messages Are Grouped

- **New chapters** are collected per series, so a batch of files is one message. A batch is sent once its series has been quiet for 2 minutes, and is held for at most 15 minutes while chapters keep arriving.
- **Many series at once** are folded into one digest when 10 or more of them are due together, such as during a first import.
- **Jobs and imports** reach a target at most once per 6 hours for the same job, so a job that keeps failing does not flood you.

## How Rescans Are Timed

- A rescan is requested 2 minutes after the last change. Further changes push it back, for at most 30 minutes.
- It is never requested while the library is being changed: while a file is being rewritten or moved, a batch job or a Metadata Backfill run is going, a Suwayomi import pass is going, or the watcher has files queued. It checks again every 30 seconds until the library is idle.

Komga and Kavita therefore never scan a half-finished batch.

## Send Test

Each target has a **Send Test** button, which works even before the target is enabled:

- For message targets it sends a real test message.
- For Komga and Kavita it asks for a real scan, and refuses while the library is being changed.

A successful test also ends any pause on that target and retries what was waiting.

| Answer | Meaning |
|---|---|
| Test sent | The target accepted it |
| 400 | The target's settings are incomplete |
| 409 | Komga or Kavita: the library is being changed, so no scan was requested |
| 502 | The target refused or could not be reached. The message gives its own error |

## Retries

Notifications are stored in the database, so a restart picks up where the last run stopped.

| Situation | What happens |
|---|---|
| The target cannot be reached, does not answer within 15 seconds, or answers 408, 425, 429 or 5xx | Retried up to 6 attempts in all, after 1 minute, 5 minutes, 30 minutes, 2 hours and 6 hours (about 9 hours) |
| The target answers with `Retry-After` | The wait is at least what it asks for, up to 1 hour |
| The target refuses with another error, or answers with a redirect | Fails at once. Redirects are not followed, so a token is never sent to another address. Check the URL, including `http` or `https` |
| The target's settings are incomplete | Fails at once |
| A target that keeps failing | It is left alone for up to 6 hours, so it costs little and cannot hold up the others. Changing its settings ends the pause, noticed within a minute; so does a successful Send Test |

At most one delivery goes to a target every 2 seconds. Other limits keep the queue small:

- A delivery still waiting after 3 days is given up on.
- At most 500 deliveries wait per target. The oldest make way when that is reached.
- Finished deliveries are kept for 7 days, and at most 200 per target.

Failures are written to the log. Look for `Notification to ... failed` and `Giving up on notification to ...`.

## The JSON Webhook Body

The webhook target sends a POST with `Content-Type: application/json`:

```json
{
  "source": "ComicMaintainer",
  "event": "new-chapters",
  "title": "3 new chapters: Batman",
  "message": "12, 13, 14",
  "isFailure": false,
  "series": "Batman",
  "count": 3,
  "items": ["12", "13", "14"],
  "occurredAt": "2026-11-09T12:34:56Z"
}
```

| Field | Type | Meaning |
|---|---|---|
| `source` | string | Always `ComicMaintainer` |
| `event` | string | `new-chapters`, `suwayomi-import-failed`, `job-failed`, `job-findings`, or `test` from Send Test |
| `title` | string | One line saying what happened |
| `message` | string | The detail. For a batch of chapters it lists the first 10 and how many more there are |
| `isFailure` | boolean | True for `suwayomi-import-failed` and `job-failed` |
| `series` | string or null | The series of a batch of chapters; null for a digest and for other events |
| `count` | number | Chapters in the batch; 0 for other events |
| `items` | array of strings | The chapters of the batch, up to 50. In a digest, the series, each as `Name (chapters)`. Empty for other events |
| `occurredAt` | string | When the delivery was created, in UTC |

Any 2xx response counts as delivered. A chapter's label is its issue number when known, otherwise its file name.

## Configuring With Environment Variables

Every field can also be set with an environment variable. A setting pinned this way shows as read-only in Settings. Tokens, API keys and webhook URLs can be written but are never returned by the API.

| Target | Variables |
|---|---|
| ntfy | `NOTIFY_NTFY_ENABLED`, `NOTIFY_NTFY_URL`, `NOTIFY_NTFY_TOPIC`, `NOTIFY_NTFY_TOKEN`, `NOTIFY_NTFY_EVENTS` |
| Gotify | `NOTIFY_GOTIFY_ENABLED`, `NOTIFY_GOTIFY_URL`, `NOTIFY_GOTIFY_TOKEN`, `NOTIFY_GOTIFY_EVENTS` |
| Discord | `NOTIFY_DISCORD_ENABLED`, `NOTIFY_DISCORD_WEBHOOK_URL`, `NOTIFY_DISCORD_EVENTS` |
| JSON webhook | `NOTIFY_WEBHOOK_ENABLED`, `NOTIFY_WEBHOOK_URL`, `NOTIFY_WEBHOOK_EVENTS` |
| Komga | `NOTIFY_KOMGA_ENABLED`, `NOTIFY_KOMGA_URL`, `NOTIFY_KOMGA_API_KEY`, `NOTIFY_KOMGA_LIBRARY_ID`, `NOTIFY_KOMGA_EVENTS` |
| Kavita | `NOTIFY_KAVITA_ENABLED`, `NOTIFY_KAVITA_URL`, `NOTIFY_KAVITA_API_KEY`, `NOTIFY_KAVITA_LIBRARY_ID`, `NOTIFY_KAVITA_EVENTS` |

`*_EVENTS` is a comma-separated list of event names. A name the target cannot receive is ignored. URLs must be absolute `http` or `https` addresses, without a username or password in them.

For the API, see `POST /api/notifications/{target}/test` and the `notifications` group of `PUT /api/settings` in the [API reference](API.md).
