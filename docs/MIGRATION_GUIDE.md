# Migration Guide

This guide covers moving between major versions of ComicMaintainer.

- [From 2.x to 3.0](#from-2x-to-30)
- [From the Python version to .NET](#from-the-python-version-to-net)

## From 2.x to 3.0

Follow the [3.0 release notes](RELEASE_NOTES_V3.0.md). They list what to do before and after
upgrading, what the upgrade does to your database and files, and how to go back. These points are
the easiest to miss:

- **Duplicates are re-checked once.** If watcher processing is on, the first 3.0 start gives every
  unprocessed file flagged as a duplicate one more pass. Review the duplicate list before you use
  **Delete All Duplicates**.
- **Files that land during the upgrade are only added.** They are not processed, because the app has
  no scan record yet. Process them from the file list. After that, files that land while the app is
  stopped are processed at the next start.
- **A watcher error no longer retries everything.** The recovery rescan queues only untracked files
  and files still waiting. Use **Process Unmarked** to retry failed files.
- **`BASE_PATH` now takes effect.** It used to be accepted and ignored. If you set it but serve the
  app at the domain root, remove it. See [Reverse proxy setup](REVERSE_PROXY.md).

## From the Python version to .NET

The Python version used the image `iceburn1/comictagger-watcher`. Since 2.0 the application runs on
.NET, from the image `iceburn1/comicmaintainer`. The two share no database or settings, so moving
is a fresh start on the same library.

### What carries over

- **Your comics.** Point the new container at the same folders.
- **`WATCHED_DIR`, `DUPLICATE_DIR`, `PUID`, `PGID` and `BASE_PATH`.** These keep their names.

Nothing else is read: not the Python version's markers, its configuration file, or its processing
history. The .NET version keeps its own database in `/Config`.

### Steps

1. Stop the Python version. Do not run both on the same library, because both would rewrite and
   rename the same files.
2. Back up its configuration folder.
3. Give the .NET version a new, empty configuration folder. Keep the old one for going back.
4. Start the .NET version with [Docker](DOCKER_DEPLOYMENT_DOTNET.md), mounting your library at
   `/watched_dir` and the new folder at `/Config`.
5. Create the first administrator. Open the web interface and enter the setup code from
   `docker logs`, or set `ADMIN_PASSWORD` before the first start.
6. Check the settings, then process your library. The first scan adds existing files without
   processing them, so nothing is renamed until you run **Process All** or **Process Unmarked**.

### Environment variables

| Python | .NET | Notes |
|--------|------|-------|
| `WATCHED_DIR` | `WATCHED_DIR` | Same |
| `DUPLICATE_DIR` | `DUPLICATE_DIR` | Same |
| `PUID` | `PUID` | Same |
| `PGID` | `PGID` | Same |
| `BASE_PATH` | `BASE_PATH` | Same |
| `FILENAME_FORMAT` | `AppSettings__FilenameFormat` | Or **Settings > Filename Format** |
| `ISSUE_NUMBER_PADDING` | `AppSettings__IssueNumberPadding` | Or **Settings > Issue Number Padding** |
| `WATCHER_ENABLED` | `AppSettings__WatcherEnableRename`, `AppSettings__WatcherEnableNormalize` | Or **Settings > Watcher**; turn both off to stop automatic processing |
| `MAX_WORKERS` | `AppSettings__MaxWorkers` | Read at startup |
| `WEB_PORT` | None | Change the host side of the port mapping (`-p 8080:5000`), or set `ASPNETCORE_URLS` outside Docker |
| `PROCESS_SCRIPT` | None | Not needed |
| `GUNICORN_WORKERS` | None | Single process, nothing to tune |
| `DB_CACHE_SIZE_MB` | None | The SQLite page cache is fixed at 64 MiB per connection |
| `DEBUG_MODE` | None | Not used |

See [Environment variables](ENVIRONMENT_VARIABLES.md) for everything the app reads now.

### Scripts and the API

Routes and response shapes differ from the Python version, and most requests need authentication.
See the [API reference](API.md).

### Going back

Stop the .NET version and start the Python version with its original configuration folder. Comics the
.NET version has already tagged or renamed stay that way, and any CBR, RAR or ZIP it converted stays
a CBZ, so back up files you cannot replace before you process them.
