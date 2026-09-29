# Error Reporting

ComicMaintainer can turn a crash into a GitHub issue so it can be fixed, instead of
it sitting unnoticed in someone's `debug.log`. The feature is **off by default** and
transmits nothing until you turn it on.

This document covers what is captured, what is stripped before anything leaves your
instance, how duplicates and floods are prevented, and the two delivery options.

---

## Why it exists

ComicMaintainer is self-hosted. When something breaks on your library, the maintainer
has no visibility into it at all — no crash telemetry, no aggregated logs, nothing.
The failure is only fixed if you happen to notice it, reproduce it, and write it up by
hand. Most people, reasonably, don't.

At the same time, an error report from a self-hosted media server is unusually
sensitive: stack traces and log lines routinely contain your library paths, the names
of the comics you own, your username, your SMTP credentials and your API keys. That
tension is why this feature is built the way it is: **capture everything locally,
send nothing without redaction and explicit opt-in.**

---

## Quick start

1. Open **Settings → Error Reporting**.
2. Tick **Enable error reporting**.
3. Leave delivery set to **Ask me each time**.
4. Press **Save error reporting settings**.

From then on, failures are captured locally. When one occurs, open the same panel,
press **Preview** to read the exact report, and press **Send this report** to open a
pre-filled GitHub issue that you submit yourself. Nothing is transmitted by your
instance in this mode.

---

## What is captured

| Source | How |
| --- | --- |
| Controllers, services, hosted services | A Serilog sink observes every `Error` and `Fatal` event, so no call site needs changing |
| Unhandled request exceptions | A global `IExceptionHandler` that also returns a consistent ProblemDetails response |
| Background loops | Through the same logging pipeline; the watcher, e-mail queue and scheduled jobs all log errors already |
| Browser failures | `window.onerror` and `unhandledrejection` hooks in both `js/main.js` and `reader.html` |

### What is deliberately *not* captured

Per-file processing failures (`ProcessingHistoryEntry.Success` / `ErrorMessage`) are
excluded. They are overwhelmingly caused by damaged or unusual input files, not by
defects in the code, and filing them would drown the real reports.

### What is suppressed

Environmental failures are not bugs and are dropped before anything is stored:

- Cancellation and client aborts (`OperationCanceledException`, disconnected clients)
- SQLite `busy` / `locked` contention
- Transient HTTP failures from external metadata providers
- Permission-denied and disk-full errors

---

## Redaction

**Every outbound report is redacted in full — the whole payload, not just the log
lines.** Redaction runs before a report is stored, so even the local database holds
the redacted form.

Stripped and replaced with `[redacted]`:

- Absolute paths and your library's folder structure
- Comic and series filenames
- Usernames and e-mail addresses
- SMTP host credentials
- Your ComicVine API key
- JWT signing keys and any bearer tokens
- GitHub tokens (`ghp_`, `github_pat_`, and friends)
- Authelia forwarded-auth headers
- IPv4 and IPv6 addresses

Deliberate exceptions, because a report is useless without them:

- **Source filenames** in stack frames (`SeriesLibraryService.cs`) are kept. They are
  public repository files and identify where the fault is.
- **The host of an outbound URL** is kept (`https://comicvine.gamespot.com/[redacted]`)
  so it is clear *which* provider failed. The path and query are removed. Hosts that
  are bare IP addresses are dropped entirely.

`debug.log` is never attached wholesale. Only a bounded window of already-redacted
lines around the error is included, controlled by `ERROR_REPORT_LOG_CONTEXT_LINES`.

If redaction itself fails — for example the regex engine times out on a pathological
input — the redactor **fails closed** and returns `[redacted]` rather than risking a
leak.

### Verify it yourself

Do not take the above on trust. **Preview** shows the exact bytes that would be
filed, and in automatic mode it shows exactly what was posted. If you find something
in a preview that should have been stripped, please report it as a security issue
(see `SECURITY.md`) rather than opening a public issue.

---

## De-duplication and rate limiting

A report is identified by a **fingerprint**: a stable hash over the exception type,
the message with variable parts masked (numbers, GUIDs, paths, identifiers), the top
few `ComicMaintainer.*` stack frames, and the app version.

- The same fault recurring increments an occurrence count instead of filing again.
- The app version is part of the hash on purpose, so the same trace in a later release
  is treated as a fresh regression rather than being silently swallowed.
- **Cooldown** (`ERROR_REPORT_COOLDOWN_HOURS`, default 24) — how long before the same
  fingerprint may be reported again.
- **Daily cap** (`ERROR_REPORT_MAX_PER_DAY`, default 5) — a hard ceiling on deliveries
  per instance per day.
- Browser hooks additionally cap themselves at 10 reports per page load and report each
  distinct signature only once, so a fault inside a render loop cannot flood the API.

Throttles apply only to transports that actually transmit. In consent mode you can
preview and send as often as you like.

---

## Delivery options

### 1. Ask me each time (default)

Your instance transmits nothing. It builds a pre-filled `issues/new` URL against the
`auto_error_report.yml` template and opens it in a new tab. You review the body on
GitHub, edit it if you want, and submit it — or close the tab.

Requires no credentials and works for every install. The body is truncated to stay
within GitHub's URL length limit; the full report is always available in the preview.

### 2. Send automatically (opt-in)

Your instance posts the issue itself using a token you supply.

Use a **fine-grained personal access token** scoped to the single target repository
with **Issues: write** and nothing else. A classic token or a broader scope gives the
instance far more access than this feature needs.

The reporter searches for an existing open issue carrying the same fingerprint and
adds a comment instead of filing a duplicate.

The token is stored in `user-settings.json` on your instance, masked on read, never
returned by the API and never written to the log.

> A hosted relay — a central endpoint that receives reports and files issues on your
> behalf — was considered and rejected. It would centralise other people's data on
> infrastructure that does not exist today.

---

## Configuration

All settings are editable in the UI. They can also be set through the environment:

| Variable | Default | Meaning |
| --- | --- | --- |
| `ENABLE_ERROR_REPORTING` | `false` | Master switch |
| `ERROR_REPORTING_MODE` | `manual` | `manual` (consent) or `automatic` |
| `ERROR_REPORT_GITHUB_REPOSITORY` | `mleenorris/ComicMaintainer` | Target repository, `owner/repo` |
| `ERROR_REPORT_GITHUB_TOKEN` | *(unset)* | Fine-grained token, automatic mode only |
| `ERROR_REPORT_MAX_PER_DAY` | `5` | Hard daily delivery cap |
| `ERROR_REPORT_COOLDOWN_HOURS` | `24` | Wait before re-reporting a fingerprint |
| `ERROR_REPORT_LOG_CONTEXT_LINES` | `40` | Redacted log lines attached around the error |

A malformed or hostile `ERROR_REPORT_GITHUB_REPOSITORY` falls back to the upstream repository
rather than sending your report to an arbitrary host.

---

## API

All endpoints require administrator rights except the browser hook.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/errorreports` | List captured reports, newest first |
| `GET /api/errorreports/{fingerprint}/preview` | The exact issue body that would be filed |
| `POST /api/errorreports/{fingerprint}/submit` | Deliver, or return the pre-filled URL |
| `DELETE /api/errorreports/{fingerprint}` | Discard, so a recurrence is captured afresh |
| `POST /api/errorreports/client` | Browser hook; any signed-in user, always returns 202 |

`POST /api/errorreports/client` takes attacker-controlled input: every field is
length-capped, redacted like any other report, and never auto-transmitted. It always
returns 202 so a page cannot use it to probe which errors the instance is tracking.

---

## What the maintainer receives

A filed issue uses the `auto_error_report.yml` template and contains the app version
and platform, the fingerprint, the exception type and redacted stack, the originating
endpoint or hosted service, the occurrence count and first-seen date, and the last UI
action. It is labelled `bug`, `auto-reported`, and an area label derived from the top
stack frame (`area:reader`, `area:metadata`, `area:email`, `area:watcher`, `area:auth`,
…) so work starts with scoped context.

Because the report body is untrusted input, only reports originating from the
reporter identity are auto-assigned to the coding agent. Anything user-submitted needs
a maintainer to apply the `ready-for-agent` label first, and there is a cap on how many
agent sessions can be started per day.

---

## CI failures

The same idea covers the build. When a scheduled or push run of a workflow concludes
in failure, `report-ci-failure.yml` searches for an open issue labelled `ci-failure`
for the same workflow and job, comments on it if found, and otherwise opens a new one
with the run URL, the failing job and a log excerpt. Pull-request failures are skipped
because they already surface on the pull request.

---

## Turning it off

Untick **Enable error reporting**. Capture stops and nothing further is delivered —
including reports that were already captured. Stored reports can be removed
individually with **Dismiss**.
