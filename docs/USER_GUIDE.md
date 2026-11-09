# User Guide

How ComicMaintainer treats your files, and what the main features do. [README](../README.md) covers installing it and
[Configuration](../README.md#configuration) covers settings.

## Files and formats

ComicMaintainer tracks `.cbz`, `.cbr`, `.zip` and `.rar` files. It looks at the content to tell the format, so a ZIP named
`.cbr` works. Supported containers are ZIP (Store, Deflate and Deflate64) and RAR 1.5 to 5.x. Other formats, such as 7z or
tar, and password-protected archives are refused, and the reason shows in the job result and in Processing History. A
raw-split archive is not joined to its other parts, so rejoin it first. Multi-volume RAR sets work.

### Limits

To protect the server, an archive over these limits is refused, and the file is left unchanged.

| When reading | Limit |
| --- | --- |
| One page, uncompressed | 100 MiB |
| `ComicInfo.xml` | 8 MiB |
| Entries in an archive | 20,000 |
| Compression ratio, for an entry over 16 MiB | 100:1 |
| RAR 5 dictionary | 256 MiB |
| RAR 5 archive comment | 1 MiB |

| When rewriting (tagging, converting, embedding a cover) | Limit |
| --- | --- |
| One entry | 1 GiB |
| Compression ratio, for an entry over 16 MiB | 100:1 |
| Total output | 20 times the archive size (at least 2 GiB, at most 64 GiB) |

A near-blank uncompressed page, such as a BMP over 16 MiB, can trip the ratio check. Repack that page as JPEG, PNG or WebP.

Solid RAR archives decompress from the start for each page, so page turns are slower. Convert them to CBZ if that matters.

### Writing to your files

- **Tags are merged.** Every write keeps the elements already in `ComicInfo.xml`. Tags are written as one comma-separated
  `<Tags>`, and authors as one comma-separated `<Writer>`.
- **Nested `ComicInfo.xml`.** One in a subfolder is honoured. A root-level one wins. When only a nested one exists, a write
  replaces it with one at the root.
- **Folders must be writable.** A rewrite builds `.tmp_<guid>.comicinfo-write` next to the archive and swaps it in, so the
  folder must allow creating files. Leftover temporary files are removed at startup.
- **CBR, RAR and ZIP become CBZ** on a write, unless you turn that off. See
  [Configuration](../README.md#configuration). With conversion off, these files are never rewritten: tag edits stay in the
  database and show only in this app, so external readers such as Komga do not see them.

## Processing

**Process** applies whichever of rename and normalize are on at **Settings > Watcher**. The explicit **Rename** and
**Normalize** actions always run. A file that could not be fully processed, because of a duplicate name, an unreadable
archive, a failed write or no series, counts as failed with a reason.

### Normalize

Normalize writes the series, title and number. It keeps a real chapter title, and only fills a missing title or fixes an
old generated `Chapter N`. Everything else in `ComicInfo.xml` stays as it was.

### Rename

Files are named from **Settings > Filename Format** (`{series} - Chapter {issue}` by default) and
**Issue Number Padding** (4). Renaming with normalize off never rewrites the archive.

A comic directly in the watched folder takes its series from `ComicInfo.xml` or its own file name, never from the
folder. A loose file that names no series is left alone, and the history says why.

### Issue numbers in file names

1. A marked number wins: `Ch 12`, `Chapter 012.5`, `c012`, `Issue 3` or `#007`.
2. Otherwise the name is split at ` - `. The last part that is only a number is the issue. If no part is only a number,
   it is the last number of the first part that has one.
3. Anything in brackets, and volume tokens such as `Vol.1`, are ignored.

| File name | Issue |
| --- | --- |
| `Batman (2016) 005` | 5 |
| `Spider-Man 2099 012` | 12 |
| `Mob Psycho 100 - 005` | 5 |
| `Series 012 - Part 2` | 12 |
| `Vol.1 Ch.12` | 12 |
| `Series (2023)` | None |

### How the series name is chosen

The first of these that gives a name wins:

1. A name you pinned for the series
2. A successful or manual provider match, in your preferred language
3. A localized title of the folder's match that the file already uses
4. A live provider lookup
5. The series already in the file
6. The folder name
7. `Unknown Series`

To see the result for one file without changing anything, call `GET /api/files/series-resolution?filePath=...`.

### When files are refreshed

| Trigger | When | What it does |
| --- | --- | --- |
| Watcher | A file appears or changes, while rename or normalize is on | Processes that file. See [Background Processing](ASYNC_PROCESSING.md) |
| Library Scan job (`library-scan`) | On its schedule, if enabled | Adds new files, reconciles deletions, and re-normalizes files whose series metadata has changed since they were last written |
| Actions in the web interface or API | When you start them | Runs the normal pipeline. The API's `forceReprocess` includes files already done |
| Audit jobs | On their schedule, if enabled | Records findings. With `autoCorrect` on, queues the files through the normal pipeline |

The Library Scan job is what picks up a change that involves no file, such as an edited alias or language preference.

### Tag editor

The **Writer** field is comma-separated authors. **Batch Update** applies only the fields you fill in.

`PUT /api/files/{encodedPath}/tags` and `PUT /api/files/metadata` also leave out fields you do not send. To clear a field
through the API, list it in `Fields` with an empty value on `POST /api/files/tags` or
`POST /api/jobs/update-metadata-selected`.

## Duplicates

A file whose target name is taken is flagged as a duplicate. Nothing is overwritten, and nothing is moved. Find them with
the **Duplicates** filter or **Review Duplicates**, where **Delete All Duplicates** removes them.

On case-insensitive file systems (Windows, macOS, Docker Desktop, SMB), a name that differs only in case is renamed, not
flagged.

Library-wide jobs leave out files marked as duplicates. To act on one, select it.

## Combine Folders

**Combine Folders** merges folders that hold the same series. Pick the destination, and use **Preview Merge** to see what
will move and which folders will be removed or kept.

After the moves, a source folder is removed only if what remains is disposable: its own `ComicInfo.xml` and `cover.*` or
`folder.*` image, operating system files (`.DS_Store`, `Thumbs.db`, `desktop.ini`) and empty subfolders. A folder holding
anything else, and any link, is kept and reported when the job ends. The watched, duplicates and Suwayomi folders are always
kept.

## Library filters

Filter by all, unmarked, marked, duplicates, renamed, normalized, read, unread, matched, unmatched or missing issues. The
**Genre** and **Status** menus use the series' provider match, and a series shows these once its match has been refreshed.
Each menu can also find series with no such data.

Read state is per user. **Mark All Read** and **Mark All Unread** apply to the whole library for your account.

## External metadata

Turn on the providers at **Settings > External Series Metadata**, or with `ENABLE_EXTERNAL_SERIES_METADATA`. ComicVine needs
an API key. Lookups run from the library's **Metadata** menu, or per series under **Manage Names**.

- **Automatic matching is exact.** Only an exact title or alias match is accepted. A series with only near matches stays
  unmatched until you pick one. Use **Manage Series Names**, search, then **Use This Match**.
- **All enabled providers are asked at once.** The best score wins, and a tie goes to provider order. Search results take
  turns across providers.
- **Refreshing.** A manual match refreshes only from its own provider, by its stored id. If that provider is off or the
  entry is gone, the match is kept. An automatic match refreshed through one of its other names re-fetches its own match.
- **Outages are not "not found".** When a provider cannot answer, existing matches are kept, a series with no match shows
  an error, and the API answers `503`. A failing provider is left alone for 30 seconds, doubling up to an hour, or
  longer if the provider asks for it.
- **Rewrites are limited.** After a refresh, only files whose series differs from the resolved name are rewritten, and none
  if normalize is off.

Requests are paced to stay within each provider's limits. ComicVine is held to 1 request per second and 180 per hour. Both
AniList providers share 28 requests per minute, so large refreshes run at about 14 series a minute while AniList is on.
Disable AniList for faster bulk runs. See [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) for the limits.

### Filling empty ComicInfo fields

With **Fill ComicInfo from providers** on (the default, and it needs normalize on), processing a file fills fields that are
empty from the series' match:

| Field | Filled with |
| --- | --- |
| Summary | Plain text, at most 2000 characters, leaving out text AniList marks as a spoiler |
| Genre | The provider's genres |
| Web | The series' page on the provider's site |
| Count | Total issues, only when the provider says the series is finished |
| Manga | `YesAndRightToLeft`, only for Japanese origin |

`LanguageISO` is never written. A value already in the file is never changed, and neither is a locked Summary. `Manga`
set to `Unknown` and `Count` set to `-1` count as empty. Existing files change only when they are next processed. Values
filled from a wrong match are not removed if you correct the match later.

## Scheduled jobs

Administrators manage jobs on the **Scheduled Jobs** page.

| Job | Default | What it does |
| --- | --- | --- |
| Database Backup | On, daily | Backs up the database. See [DATABASE.md](DATABASE.md) |
| Suwayomi Download Import | On, every 15 minutes | Moves finished Suwayomi downloads into the library, once a downloads folder is set |
| Library Scan | Off, hourly | See [When files are refreshed](#when-files-are-refreshed) |
| Metadata Audit | Off, weekly | Reports files with a missing number or a series tag that does not match |
| File Naming Audit | Off, daily | Reports files whose names do not match their metadata |
| Series Language Audit | Off, daily | Reports series tags that do not match the preferred language |
| Missing Issues Check | Off, daily | Reports gaps in issue numbering. With `autoDownload`, queues matches through Suwayomi |
| Metadata Backfill | Off, every 15 minutes | Writes metadata edits stored in the database into the archives. A CBR, RAR or ZIP stays queued while conversion is off |

## Reader

The reader has **manga** mode, one page at a time, and **webcomic** mode, a continuous scroll. Progress is saved per user.

On a touch screen, tap the middle of the page, or anywhere on the stream in webcomic mode, to show or hide the controls.
Turning pages and scrolling leave them as they are. In webcomic mode the bar along the top shows your position.

### Offline reading

Click **Save offline** in the reader to keep a comic for reading with no connection. You can also install the site as an
app from your browser. A comic counts as saved only when the whole download has finished. An interrupted download shows
**Resume N/M** and fetches only the missing pages. One not resumed within 7 days is removed.

## Suwayomi

With a Suwayomi server connected at **Settings > Suwayomi Downloads**, you can download the issues a series is missing, or
find a series that is not in your library.

To keep the search bounded, each source is searched for up to 3 result pages per title, 4 titles, and 2 minutes, and a
series for up to 10 minutes. A series beyond those limits may not be found. The result says when a search was cut short.

## History

**Processing History** lists changes to files, with the before and after values. Operations that changed nothing are not
recorded. Entries older than 90 days, or beyond the newest 50,000, are removed. See [DATABASE.md](DATABASE.md#scheduled-maintenance)
to change this.
