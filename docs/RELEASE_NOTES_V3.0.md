# Release Notes - Version 3.0.0

Released 2026-11-09.

## Overview

ComicMaintainer 3.0 fixes problems that could lose comics or data, closes security holes, and adds
an OPDS catalog, notifications and richer series details.

**Read this page before you upgrade.** Several changes need action from you, and one changes your
files. You can upgrade directly from any 2.x release. If you use the API, also read the full list
of breaking changes in the [changelog](../CHANGELOG.md#300---2026-11-09).

## The change that affects your comic files

**CBR, RAR and ZIP files are converted to CBZ when tags are written to them, unless you turn that
off.** This setting is on by default. A tag write means normalize, a tag edit, Batch Update Tags,
Remove Metadata or the Metadata Backfill job. The new `.cbz` replaces the file and the original is
deleted. The library record, read status and reading progress move with it. A file that is itself
a symbolic link is never converted.

**Files you have already processed can be rewritten and converted the next time they are
processed.** A new setting, on by default, fills a file's empty Summary, Genre, Web, Count and
Manga fields from the series' provider match when the file is processed or normalized. If there is
anything to fill, the file is rewritten once, and a CBR, RAR or ZIP becomes a CBZ. Values already
in the file, and a locked Summary, are never changed.

**Nothing happens to your comics at the upgrade itself**, apart from the two small exceptions under
[What the upgrade does to your files](#what-the-upgrade-does-to-your-files). Files marked as
processed are skipped by ordinary processing, so they change only when one of these happens:

- you run Process or Normalize with the option to reprocess files already done;
- a metadata refresh retags a series because its match or name changed;
- a scheduled job you turned on normalizes them, such as Library Scan or Metadata Audit. These jobs
  are off by default.

### What you can change

| If you want to | Do this |
| --- | --- |
| Keep CBR, RAR and ZIP files as they are | Turn off **Settings > Watcher > Convert CBR/RAR to CBZ when writing tags**, or set `AppSettings__ConvertRarToCbzOnWrite=false` |
| Stop filling fields from providers | Untick **Settings > External Series Metadata > Fill empty ComicInfo fields from the matched provider**, or set `FILL_COMICINFO_FROM_PROVIDERS=false` |

With conversion off, those archives are never rewritten. Tag edits stay in the database and appear
only in the app, so readers such as Komga or Kavita do not see them. Files normalize would change
stay unprocessed, and files that could be filled stay unfilled. If you turn the setting on later,
process those files again.

To have a choice in force from the first start, set the environment variable before you upgrade.

## Before you upgrade

1. **Make your own backup.** The app backs up its database before changing it, but not your comics
   or settings. Copy the folder mounted at `/Config`. If you have comics you cannot replace, copy
   them too, because converted originals are deleted.
2. **Write down your current version** so you can go back to it. The web app shows it as `X.Y.Z.0`,
   and the Docker tag is `X.Y.Z`.
3. **Decide about conversion and the provider fill** using the table above.
4. **Check for a published password.** `Admin123!`, `YourStrongPassword123!` and
   `YourSecurePassword123!` no longer work, even on an existing account. If an account uses one, set
   `ADMIN_USERNAME` to that account and `ADMIN_PASSWORD` to a password of your own before you
   start 3.0. The password is replaced at startup. For the other affected accounts, repeat this, or
   sign in as an administrator and set new passwords on the **Users** page.
5. **Check your Compose file or `.env`.**
   - Remove any `ADMIN_PASSWORD` that holds a published password.
   - Remove `BASE_PATH` unless the app really is served under that subfolder. It used to be ignored
     and now takes effect, so a wrong value breaks every URL.
6. **Check your reverse proxy.** See [Reverse proxy setup](REVERSE_PROXY.md).
   - Set `TRUSTED_PROXIES`, or every client shares one limit of 10 sign-in attempts per 5 minutes.
   - Let the browser fetch `/manifest.json`.
   - Do not cache `/api/comicreader/page` or `/api/comicreader/cover` in a shared proxy or CDN.
   - If you use Authelia and are coming from an older release, set `AUTHELIA_TRUSTED_PROXIES`. The
     container refuses to start without it. See [Authelia](AUTHELIA.md).
7. **Check that the app can write to your library.** The container no longer changes the owner of
   your comics. Library folders must also allow creating files, because rewrites now build a
   temporary file next to the archive. Set `PUID` and `PGID` to the folder owner or grant access on
   the host. You can also set `FIX_LIBRARY_PERMISSIONS=true` for one start and then remove it.
8. **Decide about old email deliveries.** Every delivery still pending from an earlier version is
   sent at the first start. If you no longer want them, use **Cancel All Pending** in Email Delivery
   History before upgrading.
9. **Check for an outbound proxy.** Series cover downloads no longer use `HTTP_PROXY` or
   `HTTPS_PROXY`. If the container can reach the internet only through a proxy, those downloads will
   fail.
10. **Update scripts and API clients.** Many routes and answers changed. See the
    [HTTP API](../CHANGELOG.md#http-api) list.
11. **Check free disk space in the config folder.** If the automatic backup fails, the app stops
    without upgrading the database.
12. **Plan how new accounts are made.** Visitors can no longer create their own account, even if
    you allowed it at **Settings > Access**; that setting is gone. Only an administrator can create
    accounts, on the new **Users** page or with `POST /api/users`. See [Users](USER_GUIDE.md#users)
    and [User accounts](API.md#user-accounts).

## Upgrading

If you use the published image:

```bash
docker-compose -f docker-compose.dotnet.yml pull
docker-compose -f docker-compose.dotnet.yml up -d
docker logs -f comicmaintainer-dotnet
```

To stay on this release, use `image: iceburn1/comicmaintainer:3.0.0` instead of `:dotnet`.

If you build from source:

```bash
git pull origin dotnet
docker-compose -f docker-compose.dotnet.yml up -d --build
```

## What the upgrade does to your database

- **A backup is taken first.** Before changing anything, the app copies the database to
  `/Config/backups/comicmaintainer-<timestamp>-premigrate.db` (or your configured backup folder).
  The startup log gives the exact path. The newest three of these are kept.
- **If the backup fails, the app stops** and the database is left as it was. Fix the disk or
  permissions and restart.
- **If the upgrade is interrupted**, the next start tries again from the same backup.
- **The database structure changes.** New tables hold revoked sessions, library scan records and
  notifications. New columns are added for file and series details, email retries and API keys.
  The `FileReadStatuses` table, the old API key column, each user's library view preference and
  some unused indexes are removed.
- **Two kinds of data change.** Unprocessed files flagged as duplicates are marked for one more
  pass. E-reader devices and delivery history are assigned to the first administrator, or stay
  administrator-only if there is none.
- **Old API keys are dropped.** They never worked for authentication.

The upgrade cannot be undone from inside the app. To go back, see [How to go back](#how-to-go-back).

## What the upgrade does to your files

Upgrading does not convert, retag or rewrite your comics, with two exceptions at the first start.

**Unprocessed duplicates get one more pass.** If watcher processing is on, every unprocessed file
flagged as a duplicate is processed once. This fixes files wrongly flagged by two old bugs: they
are renamed and lose the flag, and real duplicates are flagged again. This pass is real processing,
so it can write tags and convert a CBR, RAR or ZIP in the same way as any other.

**Leftover temporary files are cleaned up.** Temporary files from interrupted writes are deleted
from the library, the duplicates folder and the temp folder. An archive that is missing or
unreadable is restored from a leftover `<archive>.backup` if that can be read. A `.backup` next to
an intact archive is only reported in the log, never deleted, so you can remove it yourself once
you no longer need it. The cleanup is recorded in `/Config/archive-write-recovery-v3.done`, and later
starts delete only temporary files from interrupted writes.

Files that land in the watched folder during the upgrade are only added, not processed, because the
app has no scan record yet. Process them from the file list. From then on, files that land while the
app is stopped are processed at the next start.

## After you upgrade

1. **Sign in again.** Every user and device is signed out, because old session tokens are no longer
   accepted.
2. **Check the log** (`docker logs comicmaintainer-dotnet`) for warnings about folder permissions,
   leftover `.backup` files and published passwords.
3. **Create new API keys** if you use them, at **Settings > OPDS Catalog > Create API key**.
   Changing your password or signing out of all sessions revokes a key.
4. **Review the duplicate list** before you use **Delete All Duplicates**.
5. **Run Process Unmarked** for anything that arrived during the upgrade, or that used to be retried
   automatically after a watcher error.
6. **Match series by hand** that only had fuzzy matches before (Manage Series Names, search, Use This
   Match). Automatic matching now needs an exact title or alias.
7. **Add e-reader devices.** Devices and delivery history are now private per user, so users other
   than the first administrator must add their own. With Authelia, set `AUTHELIA_ADMIN_GROUPS`, or
   every non-ReadOnly user counts as an administrator for these records.
8. **Check `EMAIL_MAX_ATTACHMENT_MB`.** It now limits the whole encoded email. The default of 25
   allows a book of about 18 MB. If your mail server accepts larger messages, about 34 keeps the old
   allowance.
9. **Reinstall the Android app.** It now needs Android 6.0 or later and a sign-in, with the full
   server address including `http://` or `https://`. See [Android setup](MAUI_ANDROID_SETUP.md).

If you use a network or removable mount for your library, also consider setting
`STALE_CLEANUP_SENTINEL_FILE`, so a cleanup does nothing while the mount is missing. See
[Environment variables](ENVIRONMENT_VARIABLES.md).

## What's new

- **OPDS catalog.** Browse and read the library from comic apps. It is off by default. See
  [OPDS](OPDS.md).
- **Notifications.** ntfy, Gotify, Discord, a JSON webhook, and Komga and Kavita rescans. See
  [Notifications](NOTIFICATIONS.md).
- **Richer series details.** Site link, genres, status, chapter count and origin from the providers,
  with Genre and Status filters in the library. Series cached before 3.0 show none of this until
  their metadata is next refreshed.
- **Working API keys**, for scripts and OPDS apps.
- **Safer metadata.** Writes keep every existing `ComicInfo.xml` element, and automatic matching no
  longer accepts fuzzy hits.
- **Safer file handling.** Archive rewrites are atomic. A missing mount can no longer empty the
  library. Folder renames and moves made outside the app are followed. Combine Folders no longer
  deletes files it did not move.
- **Catch-up at startup.** Files that land while the app is stopped are processed at the next start.
- **Offline reading that finishes.** A comic counts as saved only when its download completed, and an
  interrupted one can be resumed.
- **Email retries.** Pending deliveries resume after a restart and temporary failures are retried.
- **Stronger sign-in.** Account lockout, rate limits, sessions that can be revoked, and a setup code
  for new installs.
- **A Users page for administrators.** Add accounts with a role, change roles, reset passwords, and
  disable or delete accounts. See [Users](USER_GUIDE.md#users).
- **A rebuilt Android app** that can sign in.

The [changelog](../CHANGELOG.md#300---2026-11-09) lists everything, including all breaking changes.

## How to go back

A 2.x release cannot use a database that 3.0 has changed, so going back means restoring the backup
taken at the upgrade.

1. Stop the app.

   ```bash
   docker-compose -f docker-compose.dotnet.yml down
   ```

2. In the folder mounted at `/Config`, restore the backup. Use the file name from the startup log.

   ```bash
   cp comicmaintainer.db comicmaintainer.db.v3-copy
   cp backups/comicmaintainer-YYYYMMDD-HHMMSS-premigrate.db comicmaintainer.db
   rm -f comicmaintainer.db-wal comicmaintainer.db-shm
   ```

3. Set the image to the version you noted, for example `image: iceburn1/comicmaintainer:2.0.413`.
4. Start the app.

   ```bash
   docker-compose -f docker-compose.dotnet.yml up -d
   ```

Going back has limits:

- **Everything recorded since the upgrade is lost**: read status, progress, new accounts, password
  changes and the rest of the database.
- **Converted files stay CBZ.** The originals were deleted and restoring the database does not bring
  them back. The restored database still knows them by their old names, so they appear as new files
  without their read status.
- **Tags already written stay written.** Restoring the database does not change your comics.

## Known limits

- OPDS, notification, Komga and Kavita support has been tested against those services' published
  APIs, not against every real server or app. Please report what you find.
- Published Android APKs are signed with a key generated for that build, so Android refuses to
  install one over an earlier version. Uninstall the old app first, then sign in again.
- Anyone who knows a username can keep that account locked out by failing repeatedly. The lockout
  ends on its own after 15 minutes. An administrator can lift it earlier by resetting the account's
  password on the **Users** page, but repeated failures lock it again.

## Support

- **Repository**: https://github.com/mleenorris/ComicMaintainer
- **Issues**: https://github.com/mleenorris/ComicMaintainer/issues

## License

MIT License. See the [LICENSE](../LICENSE) file.
