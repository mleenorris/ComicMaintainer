# Background Processing

Files are processed in two ways: the watcher handles files as they appear, and batch jobs run work you start from the
web interface or the API. Both run in the background, so the web interface stays responsive.

## The watcher

`FileWatcherService` watches `WATCHED_DIR`. It runs while **rename** or **normalize** is on at **Settings > Watcher**
(`WatcherEnableRename`, `WatcherEnableNormalize`).

When a file appears or changes, the watcher waits until it has stopped changing (`WatcherFileStabilityDelaySeconds`,
30 by default), then processes it. A burst of events for one file becomes one run. A file queued this way is flagged in
the database, so a restart does not lose it.

### Catching up at start

Every start, restart of the watcher, or change of the watched directory reconciles the library with the disk:

- Tracked files that moved or changed case while the app was down are matched to their new paths, and keep their state.
- Files flagged as waiting are queued again.
- New files are processed, but only if this directory has been scanned to the end before. A directory never fully
  scanned, such as on the first start, only has its files added. Process them from the file list when you are ready.
- If none of the tracked files are found under the directory, such as when another volume is mounted there, whatever is
  found is added without processing.
- If the directory holds no comics while rows remain, the scan record is cleared, so the library's return is not treated
  as new arrivals.

The `LibraryScans` table records the last directory scanned completely.

Folders renamed, moved or deleted outside the app are followed live. Rows, read status, reader progress, metadata edits
and field locks move with a folder. A deleted folder's rows are removed.

A failed watcher is recreated, with delays growing to at most a minute. After a watcher error or buffer overflow, a
recovery scan queues untracked files and files still waiting. It does not retry every unprocessed file; use **Process
Unmarked** for that.

On volumes where file events do not arrive reliably, such as some network shares, enable the **Library Scan** scheduled
job.

## Batch jobs

### Starting a job

Jobs start with `POST /api/jobs/{operation}-{scope}`:

| Operation | `selected` | `all` | `unmarked` |
| --- | --- | --- | --- |
| `process` | Yes | Yes | Yes |
| `rename` | Yes | Yes | Yes |
| `normalize` | Yes | Yes | Yes |
| `remove-metadata` | Yes | Yes | |
| `update-metadata` | Yes | | |
| `delete` | Yes | | |

`selected` takes a body such as `{"files": ["/watched_dir/a.cbz"]}`. The `all` and `unmarked` scopes need no body, and
leave out files marked as duplicates. The answer carries the job id and item count, as `job_id` and `total_items`.

Add `?forceReprocess=true` to reprocess files already marked as done. See [API.md](API.md) for the details and errors.

### Following a job

| Request | Answer |
| --- | --- |
| `GET /api/jobs/{id}` | Status, counts, the result for each file, and `reported_problems` |
| `GET /api/jobs` | Every job |
| `GET /api/active-job` | The running job, or the last one a restart interrupted |
| `POST /api/jobs/{id}/cancel` | Cancels a running job |
| `DELETE /api/jobs/{id}` | Forgets a finished job |

A job is `queued`, `running`, `completed`, `failed`, `cancelled` or `interrupted`. One failed file does not stop a job:
it is counted in `failed_items` and its reason is in `results`.

The web interface gets progress from the [event stream](EVENT_BROADCASTING_SYSTEM.md). If the stream is down, it polls
the active job every 5 seconds. Reloading the page picks up the running job from `/api/active-job`, and the browser warns
before you leave a page that is following one.

### After a restart

A job that was queued or running when the process stopped is marked `interrupted`. It is not resumed: the operations are
not transactional, and running one again could repeat its effects. Start it again if you need to.

Finished jobs are kept for 7 days, and the newest 50 for longer.

## How many files run at once

`MaxWorkers` (`AppSettings__MaxWorkers`, default 4, read at startup) is one limit shared by the watcher, API metadata
edits and every running job. Several jobs at once share those workers, so each may finish later. One archive is never
processed by two operations at the same time.

Use a lower value on slow or network storage. See [PERFORMANCE_TUNING.md](PERFORMANCE_TUNING.md).

## Stale-entry cleanup

A cleanup waits while the watcher is scanning, so a file that moved while the app was down is followed instead of
removed. This includes the cleanup two minutes after a start. The manual cleanup endpoints therefore take longer during
a scan. See [DATABASE.md](DATABASE.md#stale-entries-and-unmounted-shares).

## Where to look in the code

| Part | Location |
| --- | --- |
| Watcher | `src/ComicMaintainer.Core/Services/FileWatcherService.cs` |
| Job state, cancellation and retention | `Services/BatchJobRegistry.cs`, `Services/JobStateStore.cs`, `Services/BatchJobRetention.cs` |
| Job operations | `Services/ComicProcessorService.BatchJobs.cs` |
| Per-file lock | `Services/FileOperationLock.cs` |
| Marking interrupted jobs at start | `src/ComicMaintainer.WebApi/Services/JobStateReconciliationHostedService.cs` |
| Routes | `src/ComicMaintainer.WebApi/Controllers/JobsController.cs` |
