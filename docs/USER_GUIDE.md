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
old generated `Chapter N`. Everything else in `ComicInfo.xml` stays as it was, except for these fills:

- **Volume number.** `<Volume>` holds only the volume number. An empty one gets the number when the series, file or
  folder name gives one: a `Vol. 2` or `v2` token gives `2`. One that holds a year instead (`1990` or `(1990)`, as
  ComicTagger and older versions wrote it) has the year moved to the start year below, and gets the number from the
  names or is removed. A number that is already set is never replaced.
- **Volume start year.** ComicInfo has no element for the year a volume started, so ComicMaintainer keeps it in its
  own database. `The Astonishing X-Men (1995)` started in 1995 and `(1999)` is volume 2's start. The year is read from
  a `<Volume>` that holds one, as ComicTagger can write it, or from the series, file or folder name. It is never written
  into the archive, and a re-scan of the archive keeps it.
- **Cover year.** The start year never goes into `<Year>`, but an empty `<Year>` gets the issue's cover year: a year in
  brackets after the issue number, as in `Saga 001 (2012)`. A `<Year>` of 1970 counts as empty: some downloaders
  write it when the date is unknown, so the cover year replaces it, and it is kept only when no cover year is found.

Both volume values can be edited in **View/Edit**, or for many files at once in **Update**: **Volume** is the number
and **Volume start year** the year. On the series page, a volume that some issues name by number and others by start
year is shown once, as `Vol. 2 (2016)`, once one issue has both.

A volume known only by its start year can get its number from ComicVine. On the series page, choose **Actions** >
**Find Volume Numbers**. For each such volume, ComicVine's volumes with the same name and publisher are counted by start
year: Batman (2016) is DC's third `Batman`, after those of 1940 and 2011, so it is Vol. 3. Volumes of a single issue,
such as one-shots, are not counted. The dialog lists the volumes it counted, with links to ComicVine, so you can check
them. Nothing changes until you choose **Set Vol. N**, which writes the number into `<Volume>` of that volume's issues.
This needs a ComicVine API key.

### Issue details and story arcs from ComicVine

ComicVine knows each issue's title, cover date, summary, writers and story arcs. On the series page, choose **Actions** >
**Fetch Issue Details** to fill them in. It runs as a job, with progress like any other. It only fills in:

- a title that only names the issue, such as `Chapter 8`, `#8` or an old file name. A real title is kept;
- a missing cover year, or the `1970` some downloaders write when they do not know it, and the cover month;
- a missing summary, or one that is only the series' synopsis;
- missing writers, and an empty Story Arc and Web link;
- an empty publisher, with the one ComicVine lists for the issue's volume (once a series is linked to that volume). A
  file filled before is filled again for it. The publisher is never taken from the series' match: several volumes of one
  name share it, and may have different publishers (Star Wars at Marvel and at Dark Horse). A locked publisher is kept.

It also records which ComicVine issue the file was filled from: `[CVDB12345]` is added to `<Notes>`, the way ComicTagger
writes it, and the issue's ComicVine page goes into `<Web>` when that is empty or still holds the series' ComicVine page.
What else the notes hold, and a web link you set yourself, is kept. The file then names its issue wherever it goes.

A field you changed yourself is never touched.

An issue whose tags already name its ComicVine issue, because ComicTagger, Mylar or this app tagged it, needs no
matching: `[CVDB12345]` or `[Issue ID 12345]` in `<Notes>`, or the issue's ComicVine page in `<Web>`. It is filled from
that issue, Annuals and Specials too, and its volume becomes the ComicVine volume of its series' volume when that has none
yet, so the rest of the volume is found there. `[Issue ID 12345]` counts only when the notes say the tags came from
Comic Vine: ComicTagger writes it for Metron and the Grand Comics Database too, and their ids are left alone.

A tag is not trusted when `<Notes>` and `<Web>` name different issues, or ComicVine has no such issue: the issue is then
matched as below, and the tags are kept as they are. When ComicVine's issue has another number than the file, the tags
were copied from another file: the issue is matched, and the wrong id and page are replaced. A tag that cannot be read
yet, because no ComicVine requests are left, waits for the next run rather than being matched by name.

The id this app records after matching an issue by name is its own record of that match, not a tagger's, and says so:
`[CVDB12345] (matched by name in ComicMaintainer)`. If you later set the issue's volume number or start year, the issue is
matched again and the recorded id follows the new match, even after the file was renamed or moved. The issue page earlier
versions wrote into `<Web>` after a match is treated the same way while the file stays where it was filled. A tagger's id
added later wins over the app's own record. When the tags name an
issue ComicVine does not have, they are left as they are, and that id is not asked for again.

Any other issue is matched to a ComicVine volume through its series. When its issues name neither a start year nor a
volume number, that is the series' own ComicVine match (matching the series takes effect the next time). Otherwise every
ComicVine volume with exactly the series' name is weighed against what you have of that volume:

- the start year your issues name: the same year, or one off (with fewer points), and among that year's volumes of the
  name the longest one, which is the series rather than a reprint or a one-shot;
- for a volume number, being the publisher's Nth volume of that name, one-shots not counted;
- being the series' own ComicVine match;
- the publisher your issues name (`DC Comics`, `DC` and `DC Comics, Inc.` count as the same);
- having at least as many issues as your highest one: ComicVine lists Marvel's collected editions of All-New X-Men
  (4 issues) between its 2013 and 2016 volumes, and your Vol. 2 #19 rules them out;
- having started no later than your earliest issue's cover year.

A volume needs evidence that names it (its start year, its place among the publisher's volumes, or the series' match) to
fit at all, and the best is taken only when it is clearly ahead of every other volume that fits, and ahead of any that
does not: a volume named only by its start year, which another outscores on everything else (a foreign edition of that
year beside the publisher's own a year off), is left for you. When your issues name no publisher, editions cannot be told
apart that way, so any volume close behind the best has the issue lists read before one is taken. A one-shot or giveaway of the same name counts against itself. When several fit about equally well, their
issue lists are read too: the volume whose cover years match your issues' (within a year), and whose story titles match
your real titles, wins. Then, with a Metron account set (see below), Metron's record of each volume counts: Metron
records the ComicVine volume of each of its series, with its own volume number and start year, so a volume Metron counts
as yours gains and one it counts as another loses. Then the covers: your covers of up to two issues are compared with each
volume's covers of the same numbers, by a fingerprint that survives resizing and re-encoding, and a matching cover counts
for much. If that still does not settle it, nothing is guessed: the volume is left for you to review, and its issues are
not filled until you choose.

**Metron (optional).** Metron (metron.cloud) is a free, carefully kept comic database. With a Metron API token (make one on
metron.cloud; it can be revoked without changing your password), or the account's username and password, under
**Settings** > **External Metadata**, every volume taken is also checked against Metron's record of it: when Metron
counts it as another volume number, or a start year more than one off, it is left for you to review with the reason
(`ComicVine's Batman (2016) fits best, but Metron lists it as Vol. 2 (2010), not started in 2016`). Metron allows 20
requests a minute and 5,000 a day; the app asks at most 18 a minute (a background job waits for the next one) and 4,900 a
day, remembers each answer for a week, and leaves Metron out for 10 minutes after a refusal or failure. The address must be
https, so the token or password is never sent in the clear. A rejected token or password stops Metron until you change it
(or for 6 hours), and the settings show why Metron is left out. Metron has its own entry on the provider status screen
(**External Providers**): whether it is set up and reachable, its last problem, and how its requests went. Opening the
screen asks Metron at most once every 10 minutes. Metron is a second opinion: when it cannot be asked (it is
failing, or its daily limit is reached), a volume is taken on ComicVine's evidence alone and not checked later.

The volume is looked for in ComicVine's search first. For a much-used name such as Batman, the search can leave the right
volume out, so when it finds none that fits, every ComicVine volume with exactly that name is listed, oldest first, and
looked through too. That takes up to 5 more requests.

Without a tag naming their issue, Annuals and Specials are left out, and duplicates always are. An issue dated more than a
year away from ComicVine's issue of that number in its volume is not filled from it either: it is another volume's, or its
date is wrong (a cover year an earlier fill wrote does not count). An issue that cannot be matched counts as failed in the
job, with the reason, such as `ComicVine has no Batman Vol. 7`. Set its volume number or start year with **Update**, or match the series
to ComicVine, and run it again. A volume ComicVine did not have is searched for again each time you run **Fetch Issue
Details**; the scheduled job waits 30 days before searching again.

ComicVine allows about 200 requests an hour. Titles, cover dates and summaries come 100 issues to a request, but writers
and story arcs take a request per issue. A job asks for at most 250. Issues it did not reach get their writers and story
arcs from the **ComicVine Issue Details** scheduled job, which does the whole library a batch at a time once you turn it
on. Its `maxRequests` option sets how many requests each run may use.

The series page lists its volumes under the synopsis, each with its number or start year, how many issues you have,
and a link to the ComicVine volume it is matched to, or "not linked to ComicVine yet". It also shows:

- **needs review**, with **Choose Volume…**, for a volume left for you to choose, and **Choose Volume…** too for one
  ComicVine's search did not find;
- **Change…** beside a linked volume, to choose another;
- issues dated years away from ComicVine's issue of that number, with the years when you point at them;
- for the issues that name no volume, the cover years across which their numbers repeat (`#1` of 2011 and of 2016): they
  are likely several volumes, filed as one. Set their volume years with **Update**.

**Choose Volume…** lists the volumes that were weighed, best first, each with what counted for and against it and a link
to it on ComicVine. Choose one, or paste the link (or id) of another ComicVine volume, which is checked on ComicVine and
named from it, or choose **None of These**. Your choice is final: the volume is never searched for again, a running job
does not change it, and the next **Fetch Issue Details** fills its issues from it. A volume left for review is not weighed
again on every run: it waits for you, or for 30 days.

Each issue links to its ComicVine page too: **ComicVine** on its card, and **Open on ComicVine** in its menu. An issue
filled by **Fetch Issue Details** links to the issue it was filled from. Another one links to the issue of its number
in its volume's ComicVine listing; hover the link to see which. When the listing has no such issue, or the volume has
not been listed yet, it links to the volume instead (**Volume on ComicVine**). An issue whose volume is not linked to
ComicVine yet, or an Annual, Special or other kind with a numbering of its own, has no link. The links come from what was stored
when issues were matched; showing them asks ComicVine nothing. It also lists the story arcs its
issues are part of. Choose one to see the arc's reading
order across every series, as ComicVine lists it. Issues you have can be read from there, and the others are marked
**Not in your library**. The arcs are also written into each file's `<StoryArc>`, which Komga and Kavita use to build
reading lists.

### Replacing a cover with ComicVine's

An issue's cover can be replaced with its cover from ComicVine. Open the issue's menu on the series page and choose
**Replace Cover…**. The dialog shows the issue's cover now and every cover ComicVine has for its volume, with the issue's
own chosen. If the issue was matched to the wrong ComicVine issue, or not matched at all, choose another cover from the
list. **Replace Cover** downloads the full-size cover and makes it page 1 of the CBZ, in place of the old cover. Every
other page is kept. Only a CBZ can be changed this way; convert a CBR to CBZ first.

The issue is matched to ComicVine as for issue details: through its series and its volume's start year or number.
Annuals, Specials, Giant-Size issues and One-Shots are not matched yet. This needs a ComicVine API key.

### Removing pages

To take pages out of an issue, such as scan credits, ads or a cover that appears twice, open the issue's menu on the
series page and choose **Remove Pages…**. The dialog shows every page; tick the ones to remove and choose **Remove
Pages**, then confirm. The CBZ is written again without them, which cannot be undone. Every other page is kept, and so
are the page types in its ComicInfo.xml (`<Pages>` follows the pages it describes, and `<PageCount>` is updated). A comic
keeps at least one page. Only a CBZ can be changed this way; convert a CBR to CBZ first. Every reader's saved page moves
down past the pages removed before it. A copy downloaded for offline reading keeps the old pages until it is downloaded
again.

A file renamed before this version may have lost its volume from its name. If its `<Title>` still holds the old file
name, the volume and cover year are read from there, and the title is then replaced by the generated `Chapter 10`, which
**Fetch Issue Details** replaces in turn with ComicVine's title. An old file name may start with the series, as in
`All New X-Men v2 008 (2016)`, or not: taggers often left the name of a file inside a series folder, such as
`v4 010 (2018)`, `v1 #1`, `001 (2015)` or `z-issue 004 (2016)`. A real story title is never read this way: the title has
to contain the issue's own number, and either start with the series name or hold nothing else than a volume, issue words
such as `#` or `Annual`, bracketed parts and punctuation. A title you set yourself is kept.

The series name in an old file name may be shortened or different. A title whose first words all appear, in order, in
the series name, followed only by the parts above, counts: `Soulfire Vol 1 03 (of 10)` under "Michael Turner's
Soulfire", `Batman 003` under "Batman: The Dark Knight". So does a title that starts with one of the series' aliases:
the ones ComicVine lists for the matched volume, and the ones you add to the series yourself (`ASM 003` under "The
Amazing Spider-Man" with the alias `ASM`).

When an old file name in the title disagrees with the tags, the tags are trusted. A title `Vol. 2 003 (2023)` on a file
whose `<Number>` is 2 names another issue, so the number stays 2, the title still becomes `Chapter 2`, and the year
(2023, #3's cover year) is not read. Likewise a `<Volume>` already set is never changed by a title naming another
volume, and that title's year is not read either. A volume runs across its issues, so such a title still fills an empty
`<Volume>`.

One tagger mistake is corrected from the file name: the volume's number written as the issue. When `<Number>` equals
`<Volume>` and the file name gives another issue right after that volume (`Vol. 2 004`, `v02 - 004`, `Vol. 2 Ch. 4`),
the file name's issue is used: `Vol. 2 004 (2023).cbz` tagged Number 2, Volume 2 becomes issue 4 (and its generated
title `Chapter 4`), instead of every issue of the volume becoming #2 and all but one being left as duplicates. A
collected edition is left alone: `100 Bullets Vol. 1 (2000)` or `Saga Vol. 1 - Chapters 1-6` tagged Number 1, Volume 1
gives no issue after the volume. An issue number you locked is kept.

The file name and the title together also outweigh `<Number>`. Some downloaders number an issue by its place in the
list, so `Issue #40.cbz` titled `Issue #40` comes tagged Number 41 (counting an `Issue #0`). When the file name marks
its number (`Issue #40`, `#40`, `Chapter 40`) and a title that names nothing but an issue marks the same number, that
number is used. A locked issue number is kept, and an old file name in the title alone is still not enough. Neither
counts when the title is one this application generated (`Chapter 40`), which only repeats an earlier number, nor when
the tags name a ComicVine issue.

Annuals keep "Annual" in their series, the way ComicVine names them. That means `<Series>Green Lantern Annual</Series>`
with `<Number>1</Number>`, and an issue written as `Annual 1` becomes number `1`. A file is an Annual when its series
ends in "Annual", its number is written as one, its file name has "Annual" before the issue number (`Green Lantern
Annual 001`), or its `<Format>` is `Annual`. A title with "Annual" right before the issue number also counts, such as
the old file name `All New X-Men Annual 001 (2015)` or `#Annual 2021` that some taggers leave in `<Title>`: once such a
file has been renamed, the title is all that still marks it. Normalize writes `<Format>Annual</Format>` into an Annual
whose `<Format>` is empty. Annuals stay in the Green Lantern series, in their own "Annual" section with their own missing issues.

An Annual may have no issue number. One named by its year (`#Annual 2026.cbz`) is not given the year as its number, and
when you clear an Annual's number in **View/Edit**, processing keeps it cleared instead of reading a number back from
its file name (`… Chapter 0021.cbz`). Such an Annual is named without a number: its title stands in for it
(`Absolute Wonder Woman Annual - 2026.cbz`), and a missing or generated title becomes `Annual 2026` (its cover year), or
`Annual` without one. Two such Annuals that would take one name keep their own names instead of one being flagged as a
duplicate. The library lists it without a number, and does not count the years before it as missing issues. An Annual
whose name gives a number that is not a year, and whose number you did not clear, still takes that number
(`Batman Annual 003.cbz` is #3).

Editing the issue number in **View/Edit** or **Update** locks it, so processing keeps the number you set or cleared.

Specials work the same way. Set **Format** to `Special` in **View/Edit**, or for many files at once in **Update**. A
title can also mark one: "Special" right before the issue number (`Special 1`), or, for an issue with no number, "Special"
alone or before a separator (`All New X-Men .Special – One Down`). Specials get their own "Special" section, after the
Annuals.

Giant-Size issues have their own numbering too. `<Format>` is `Giant-Size`; `Giant` and `Giant Size` are read as it. A
file is one when its series or file name starts or ends with "Giant-Size" or "Giant Size" (`Giant-Size X-Men`), or its
title has "Giant" right before the issue number (`Giant 2025`, `#Giant 2025 2`). "Giant" alone in a series name, such
as `Giant Days`, does not count. Normalize writes `<Format>Giant-Size</Format>` or `<Format>Special</Format>` into an
empty `<Format>`. Giant-Size issues get their own "Giant-Size" section, after the Specials.

One-Shots, single issues published on their own, are a kind of their own as well. `<Format>` is `One-Shot`; `One Shot`
and `Oneshot` are read as it. A file is one when its series or file name starts or ends with "One-Shot"
(`X-Men: Red One-Shot`), its file name holds `(One-Shot)`, or its title has "One-Shot" right before the issue number
(`One-Shot 1`) or as its label. Normalize writes `<Format>One-Shot</Format>` into an empty `<Format>`, `{format}` names
it, and One-Shots get their own "One-Shot" section, after the Giant-Size issues, so a One-Shot #1 is neither taken for
issue #1 nor flagged as its duplicate.

You can also set a kind of your own in **Format**, in **View/Edit** or for many files in **Update**: any value counts,
so a one-shot with a name of its own, such as DC's `Rebirth`, gets its own "Rebirth" section, `{format}` names it
(`Batman Rebirth - Chapter 0001.cbz`), and it is never taken for issue #1. The values taggers write to describe an
edition, not a kind, still mean a regular issue: `Series`, `Main Series`, `Limited Series`, `Mini-Series`, `Digital`,
`Print`, `Web`, `TPB`, `Trade Paperback`, `Hardcover`, `HC`, `Graphic Novel`, `Omnibus` and the like.

A story title that only contains one of these words, such as "The Annual Party" or "A Special Day", never counts.

### Rename

Files are named from **Settings > Filename Format** and **Issue Number Padding** (4). Renaming with normalize off never
rewrites the archive. The default format is `{series} {format} {volume_label} - Chapter {issue}`:

| Issue | Name |
|---|---|
| Green Lantern (1990) #1 | `Green Lantern (1990) - Chapter 0001.cbz` |
| Green Lantern Annual (1990) #1 | `Green Lantern Annual (1990) - Chapter 0001.cbz` |
| Green Lantern #1, Format `Special` | `Green Lantern Special - Chapter 0001.cbz` |
| X-Men #2025, title `Giant 2025` | `X-Men Giant-Size - Chapter 2025.cbz` |
| Saga, volume unknown, #1 | `Saga - Chapter 0001.cbz` |
| All-New X-Men, no number, title `.Special – One Down` | `All-New X-Men Special - One Down.cbz` |

`{series}` is the series name alone. Its volume and kind appear only where the format places them:

- `{volume_label}` is the volume number, `Vol. 2`, when it is known, else the start year, `(1990)`. They come from
  ComicInfo `<Volume>`, the stored start year, the series name, the file name (a `v2` token, or a year before the
  issue number), an old file name left in `<Title>`, and the folder name.
- `{volume_number}` is the number alone, `2`, and `{volume_year}` the start year alone, `1990`.
- `{format}` is `Annual`, `Special`, `Giant-Size` or `One-Shot`.
- `{annual}` is `Annual` for an Annual only. Prefer `{format}`, which covers the other kinds too.

Each renders empty when it does not apply, and any brackets or ` - ` separator around it go with it. The old defaults,
`{series} - Chapter {issue}` and `{series} {annual} {volume_label} - Chapter {issue}`, are read as the new one.

An issue with no number never gets a name ending in a bare "Chapter". The word before `{issue}` (`Chapter`, `Ch.`,
`Issue`, `#`) is dropped, and `{issue}` becomes the issue's title. The series name and kind an old file name left at the
start of the title are taken off. When there is no title either, the file keeps its name. A file an older version named
`... - Chapter.cbz` loses the bare "Chapter" when it is processed again.

> **Using this for comic books? Keep `{volume_label}` and `{format}` in your format.** American comics restart their
> numbering with each volume, and Annuals and Specials have their own numbering. Without these placeholders, Green
> Lantern (1990) #1, Green Lantern (2005) #1 and Green Lantern Annual #1 all get the same name, and all but one are flagged as
> duplicates. *Delete All Duplicates* would then delete real issues.

A comic directly in the watched folder takes its series from `ComicInfo.xml` or its own file name, never from the
folder. A loose file that names no series is left alone, and the history says why.

Some characters can't be used in file or folder names. They are replaced so the name still reads well:

| Character | Becomes | Example |
|---|---|---|
| `:` followed by a space | ` - ` | `Solo Leveling: Ragnarok` gives `Solo Leveling - Ragnarok` |
| Any other `:`, `/`, `\`, `\|` | `-` | `Fate/Zero` gives `Fate-Zero` |
| `"` | `'` | |
| `?`, `*`, `<`, `>` | Left out | `What If?` gives `What If` |

Older versions used `_` instead (`Solo Leveling_ Ragnarok`). Such files get the new name the next time they are
renamed or processed.

### Series folders

By default a file stays in the folder it is in. To give every series one tidy folder, set **Settings > Filename
Format > Series Folder Template**, for example `{series} ({volume_year})` or `{series}`. Processing and renaming then
move each file into that folder, directly in the watched folder. The folder is created when needed.

- The template must contain `{series}` and name one folder, so it can't contain `/` or `\`.
- You can use `{series}`, `{volume_year}`, `{volume_number}`, `{volume_label}`, `{volume}`, `{format}`, `{annual}` and
  `{publisher}`. They work as in the file name. Issue placeholders such as `{issue}` render nothing.
- An empty placeholder goes away with its brackets: `{series} ({volume_year})` gives `Saga` when the year is unknown.
- A folder that differs only in case is reused, so `X-men (2024)` is not doubled as `X-Men (2024)`.
- If the new place is taken, the file is flagged as a duplicate and stays where it is. Nothing is overwritten.
- Files in the duplicates folder are not moved.
- When the last comic leaves a folder, the folder is removed if nothing else is left in it, by the same rules as
  [Combine Folders](#combine-folders). Otherwise it is kept.
- Files already processed move the next time they are processed or renamed. Use **Rename** or **Process** on them,
  or the API's `forceReprocess`.

### Issue numbers in file names

1. A marked number wins: `Ch 12`, `Chapter 012.5`, `c012`, `Issue 3` or `#007`.
2. Otherwise the name is split at ` - `. The last part that is only a number is the issue. If no part is only a number,
   it is the last number of the first part that has one.
3. Anything in brackets, and volume tokens such as `Vol.1`, are ignored.
4. A number may carry a letter suffix after a dot, as publishers number crossover tie-ins inside a series: Marvel's
   Monsters Unleashed tie-in of All-New X-Men is #1.MU, and ComicVine lists it so. One to four letters are kept, so
   `001.MU`, `1.NOW` or `12.INH` are not read as issue 1 or 12.

A tie-in's file is named with its suffix (`All-New X-Men Vol. 2 - Chapter 0001.MU.cbz`), so it is not taken for a
duplicate of #1. When `<Number>` holds only `1` but the file name, or an old file name left in the title, says `001.MU`,
normalize writes `1.MU` into `<Number>`. A file already flagged as a duplicate of #1 is cleared by **Re-check
Duplicates**.

| File name | Issue |
| --- | --- |
| `Batman (2016) 005` | 5 |
| `Spider-Man 2099 012` | 12 |
| `Mob Psycho 100 - 005` | 5 |
| `Series 012 - Part 2` | 12 |
| `Vol.1 Ch.12` | 12 |
| `All New X-Men v2 001.MU (2017)` | 1.MU |
| `Series (2023)` | None |

#### Shifting issue numbers

Some downloaders number a run from the wrong start, so every issue is off by the same amount (The Amazing Spider-Man
files where issue 3 is really #1). On the series page, tick the issues, choose **Shift Issue Numbers**, and enter how far
to move them: `-2` makes issue 3 issue 1. The dialog shows each issue's number before and after; an issue whose number
is not plain (`1.MU`, `Annual 1`) or would fall below 0 is left out.

Nothing changes unless every issue sent can move: no two may end on one number of one volume, and none may end on a
number an issue left out of the shift keeps in that folder (shift it too, or leave the one that would land on it out).
The reasons are shown and nothing is written. Otherwise the shift runs as a job, one issue at a time, the lowest new
number first when the numbers go down (the highest first when they go up), so each takes a name the one before it freed:

- its new number is written and locked, so processing keeps it, and a generated title (`Chapter 3`) follows it;
- the file is renamed to match, unless renaming is turned off in the settings;
- if one fails, the issues after it are skipped and left as they were, so none is left as another's duplicate. A
  cancelled job leaves the issues it reached shifted and renamed, and the rest untouched.

### How the series name is chosen

The first of these that gives a name wins:

1. A name you pinned for the series
2. A successful or manual provider match, in your preferred language
3. A localized title of the folder's match that the file already uses
4. A live provider lookup
5. The series already in the file
6. The folder name
7. `Unknown Series`

In a folder name, `_` is read as `:`, as other tools such as Suwayomi write it. Some tools capitalize the letter after an
apostrophe (`Trash Of The Count’S Family`). In steps 5 and 6 that capital is lowered: `Count’s`, `I’ll`. Names such
as `O'Brien` are left alone. Series are matched without regard to case, punctuation or spacing, so `’` and `'`, and
`:`, `_` and ` - `, count as the same series.

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

### Preview changes

Check what processing would do before it does it. Nothing is changed until you process the files.

- **Process > Preview Process All** previews every file that is not a duplicate.
- **Process > Preview Process Selected** previews the files you selected.
- **Preview Changes** in a series' **Actions** menu previews that series.

The preview runs in the background, like other jobs. When it ends, the results open, grouped by series. Each file shows:

- its current name and its new name;
- each tag that changes, with its old and new value. `VolumeYear` is kept in the app's database, not in the file;
- a clash, when another file already has the new name or another file in the preview gets it first. Processing flags such a
  file as a duplicate instead of renaming it;
- a note when processing would fail for the file, or would leave its tags unwritten (a CBR, RAR or ZIP while conversion to
  CBZ is off).

**Only changes** is on at first. Turn it off to list the files processing would leave alone too.

The preview follows your rename and normalize settings. It shows what reprocessing would do, so it includes files already
marked processed.

Files with changes are ticked, except clashes and files that would fail. Untick the files you want left alone, then
choose **Process Ticked Files**. This reprocesses exactly the ticked files.

The app keeps the five most recent previews until it restarts. If the library changes after a preview, run it again.

## Duplicates

A file whose target name is taken is flagged as a duplicate. Nothing is overwritten, and nothing is moved. Find them with
the **Duplicates** filter or **Review Duplicates**, where **Delete All Duplicates** removes them.

On case-insensitive file systems (Windows, macOS, Docker Desktop, SMB), a name that differs only in case is renamed, not
flagged.

Library-wide jobs leave out files marked as duplicates. To act on one, select it.

A flag stays after its cause is gone. For example, an Annual once took a regular issue's name. Annuals are now named as
Annuals, but the real issue is still flagged. **Re-check** finds these:

- **Re-check All** in **Review Duplicates** checks every duplicate. **Re-check Duplicates** in a series' **Actions** menu
  checks that series' duplicates.
- For each file, it works out the name processing would give it now.
- If no other file has that name, the flag is cleared and the file is processed. It gets its proper name and tags.
- If another file still has it, nothing changes. The job's results name the other file.
- When the job ends, a message counts the files cleared, still duplicate and failed.

**Compare** (in **Review Duplicates**, or **Compare Duplicate** in an issue's menu) shows a duplicate next to the file that
has its name: cover, pages, size, date and tags, plus the name processing would give each file. Rows that differ are
highlighted.

- **Keep Both** changes nothing.
- **Swap** gives the duplicate the name. The other file gets ` (duplicate)` added to its name and is flagged instead.
- If the name is free, **Re-check This File** clears the flag.

None of these delete a file.

## Combine Folders

**Combine Folders** merges folders that hold the same series. Pick the destination, and use **Preview Merge** to see what
will move and which folders will be removed or kept.

After the moves, a source folder is removed only if what remains is disposable: its own `ComicInfo.xml` and `cover.*` or
`folder.*` image, operating system files (`.DS_Store`, `Thumbs.db`, `desktop.ini`) and empty subfolders. A folder holding
anything else, and any link, is kept and reported when the job ends. The watched, duplicates and Suwayomi folders are always
kept.

When a series is in folders that can be combined, its page says so: "This series is in 2 folders: Nightwing, Nightwing
(2016-)." **Merge** opens **Manage Folders**, where **Merge Folders** combines them.

## Library filters

Filter by all, unmarked, marked, duplicates, renamed, normalized, read, unread, matched, unmatched or missing issues. The
**Genre** and **Status** menus use the series' provider match, and a series shows these once its match has been refreshed.
Each menu can also find series with no such data.

Missing issues are the gaps from #1 up to the highest issue number. A number far beyond the rest is left out of that, so
Nightwing #1,000,000 does not make a million issues missing. The numbers are split wherever one is more than 1000 above
the one before it (or 5 per issue in the run, when that is more). The largest group is the run; groups above it leave no
gap. The series page, the missing filter and the Missing Issues Check all use this rule.

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
| ComicVine Issue Details | Off, hourly | Fills issue titles, cover dates, summaries, writers and story arcs from ComicVine. See [Issue details and story arcs from ComicVine](#issue-details-and-story-arcs-from-comicvine) |

## Users

Administrators manage accounts on the **Users** page: open it from the sidebar or the account menu, or press `g` then
`u`. There is no sign-up page, so this is where accounts come from after the first administrator.

| Role | What the account can do |
| --- | --- |
| Administrator | Everything a User can do, plus settings, accounts, backups, logs and scheduled jobs |
| User | Browse and read the library, and change it: process, rename, edit metadata, delete files and email comics |
| Read-only | Browse and read the library, and keep their own reading progress. It cannot change files or metadata, or email comics |

- **Add user** creates an account with a username, a password, a role, and optionally a full name and an email. Without
  an email the account gets `<username>@comicmaintainer.local`.
- **Change role** signs the account out everywhere, so it signs in again with the new role.
- **Reset password** sets a new password, signs the account out everywhere, revokes its API key and lifts a lockout. Tell
  the person their new password; they can change it with **Change Password** in the account menu.
- **Disable** signs the account out everywhere and revokes its API key at once. A disabled account cannot sign in until
  an administrator enables it again, and its old sessions and key stay revoked.
- **Delete** removes the account with its reading progress, read status, preferences, e-reader devices and their series
  subscriptions, and email delivery history. Comic files are not touched. If `ADMIN_USERNAME` names the account and
  `ADMIN_PASSWORD` is set, it is created again at the next start.

Adding an account, changing a role, resetting a password and enabling an account ask for your own password. They give
someone lasting access, so a stolen session alone cannot do them. Disabling and deleting only take access away, so they
do not ask. With Authelia, you are not asked, because your account has no password here.

The page will not let you change your own role, disable or delete your own account, or reset your own password; use
**Change Password** for that. The last active administrator cannot be demoted, disabled or deleted. Each refused action
is greyed out with the reason shown on the account.

With Authelia enabled, Authelia creates accounts when people first sign in, sets their roles from its groups, and keeps
their passwords. The page then only disables, enables and deletes accounts. A deleted account comes back, empty, the
next time that person signs in through Authelia, so disable an account to keep someone out. See
[AUTHELIA.md](AUTHELIA.md#user-management).

## Reader

The reader has **manga** mode, one page at a time, and **webcomic** mode, a continuous scroll. Progress is saved per user.

On a touch screen, tap the middle of the page, or anywhere on the stream in webcomic mode, to show or hide the controls.
Turning pages and scrolling leave them as they are. In webcomic mode the bar along the top shows your position.

### Offline reading

You can keep comics on a device for reading with no connection:

- **One issue:** choose **Save Offline** in the issue's menu, or click **Save offline** in the reader.
- **A series:** click **Save Offline** on the series page to save every issue.
- **Several series:** select them in the library and click **Save Offline** in the selection bar.

Saving more than one issue asks first, since the comics take storage space. They are saved one at a time, and a saved
issue is marked **Offline** on its cover. A comic counts as saved only when the whole download has finished. An interrupted
download shows **Offline N/M** (**Resume N/M** in the reader) and fetches only the missing pages when saved again. One not
resumed within 7 days is removed.

**Offline Comics** in the sidebar lists the comics saved on the device you are using, with the space they take, and lets
you read, resume or remove each one. When the app is opened with no connection, it shows this list instead of the library,
and starts normally once the connection returns. Saved comics stay on the device when you sign out, until you remove them.
You can also install the site as an app from your browser.

## Suwayomi

With a Suwayomi server connected at **Settings > Suwayomi Downloads**, you can download the issues a series is missing, or
find a series that is not in your library.

To keep the search bounded, each source is searched for up to 3 result pages per title, 4 titles, and 2 minutes, and a
series for up to 10 minutes. A series beyond those limits may not be found. The result says when a search was cut short.

## History

**Processing History** lists changes to files, with the before and after values. Operations that changed nothing are not
recorded. Entries older than 90 days, or beyond the newest 50,000, are removed. See [DATABASE.md](DATABASE.md#scheduled-maintenance)
to change this.
