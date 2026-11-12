# Logging, Diagnostics and Error Reports

Where to look when something goes wrong, and what automated error reporting sends.

## Log files

Log files are in the config directory, `/Config` by default. There is no debug mode to turn on: `debug` logs are always
written.

| File | Contents | Lowest level | Days kept |
| --- | --- | --- | --- |
| `app<date>.log` | General application messages, leaving out the three specialist logs below | Information | 7 |
| `debug<date>.log` | Everything, with source and correlation id | Debug | 3 |
| `watcher<date>.log` | File watcher | Debug | 7 |
| `metadata<date>.log` | Metadata providers, the metadata cache, refresh jobs and series images | Debug | 7 |
| `auth<date>.log` | Sign-ins, failed attempts, tokens, API keys and Authelia | Debug | 7 |

A new file starts each day, named like `app20261109.log`. Each file is capped at the size set at **Settings > Log Rotation**
(10 MiB by default, read at startup). When a file reaches its cap, nothing more is written to it until the next day, so raise
the size if the day's `debug` log runs out before you need it.

The console, which is what `docker logs` shows, carries Information and above.

### Reading logs

Administrators can open **View Logs** from the menu and pick a log. The same is available with
`GET /api/logs?type=debug&lines=500` and `GET /api/logs/files?type=debug`, where `type` is `app`, `debug`, `watcher`, `metadata`
or `auth`. Logs name users, client addresses and paths, so they are admin-only.

```bash
docker logs -f comicmaintainer-dotnet
tail -f /path/to/config/debug$(date +%Y%m%d).log
```

### Following one request

Every response has an `X-Correlation-Id` header, and each `debug` log line carries the id of the request that wrote it. Work
outside a request, such as the watcher or a scheduled job, shows `-`. When the web interface reports a failure, it shows
`(ref: <id>)`. Search `debug` for that id.

### More detail

- **SQL.** Set `EFCORE_QUERY_LOGGING=true` to print EF Core's SQL to the console. It is slow, so use it briefly.
- **Browser requests.** Run `localStorage.setItem('debug_api', 'true')` in the browser console to log each API request.
- **Instance snapshot.** Administrators can open **System Diagnostics** from the menu for uptime, health checks, watcher
  state, disk space and database size, and copy it into a bug report.

## Automated error report

ComicMaintainer can file errors it logs as issues on the project's GitHub repository, `mleenorris/ComicMaintainer`. It is off
by default, and nothing leaves your server until you turn it on and supply a token. The destination is fixed.

**Issues on that repository are public.** Read [what is sent](#what-is-sent) before you turn this on.

### Turning it on

1. Create a fine-grained GitHub token with `issues: write` on that repository only.
2. At **Settings > Automated Error Reporting**, turn it on, paste the token and save.
3. Click **Send Test Report** to check the token works.

You can set it with environment variables instead, which keeps the token out of `user-settings.json`:

| Variable | Default | Meaning |
| --- | --- | --- |
| `ERROR_REPORTING_ENABLED` | `false` | File issues |
| `ERROR_REPORTING_GITHUB_TOKEN` | _(empty)_ | The token |
| `ERROR_REPORTING_ASSIGNEE` | _(empty)_ | GitHub login to assign new issues to |
| `ERROR_REPORTING_MAX_ISSUES_PER_DAY` | `10` | New issues in a rolling 24 hours |
| `ERROR_REPORTING_DEDUPE_WINDOW_HOURS` | `24` | Time between "still happening" comments |
| `ERROR_REPORTING_COMMENT_ON_RECURRENCE` | `true` | Comment on an existing issue when it recurs |

### What is sent

Errors and fatal errors logged by the server, and errors caught in the browser. Each report has the app version, the
exception type, the message and message template, the source, the correlation id and the leading stack frames. Conditions
that are about your own setup, such as a refused administrator password, are logged but not filed.

Everything is scrubbed first:

| Found | Becomes |
| --- | --- |
| Configured passwords, tokens and API keys | `[redacted]` |
| `password=...`, `token: ...` and similar values, and `Bearer` or `Basic` credentials | `[redacted]` |
| Credentials inside a URL | `[redacted]` |
| Email addresses | `[redacted]` |
| A file path | `<path>/` followed by the last part only |

The last part of a path survives, so a comic's **file name, which usually contains the series title, can appear in a public
issue**. Leave reporting off if that matters to you.

### How issues are filed

- **One issue per defect.** A failure is identified by its type, message template and stack, so the same bug on many comics
  files one issue. The record is kept in the database, so a restart does not file it again.
- **Daily cap.** Past the daily limit, errors are only counted.
- **Recurrences** add a comment, at most once per the interval above, if comments are on.
- **Lost responses.** If a request to create an issue may have reached GitHub but the answer was lost, the repository is
  searched for that fingerprint after a short wait. The issue is linked if found, and filed only if not.
- **Rate limits.** `Retry-After`, a rate-limit reset, or a secondary rate limit pauses reporting for as long as GitHub asks.
  Repeated temporary failures pause it for 15 minutes.
- **A rejected configuration**, such as a bad token, stops reporting until the configuration changes.

Administrators can list reported failures, and mute one, through `/api/error-reports`. A muted failure is still counted but
never filed.

Reporting never affects the app: if GitHub is unavailable, your server carries on.
