# ComicMaintainer

ComicMaintainer is a self-hosted comic library manager. It watches a folder for `.cbz`, `.cbr`, `.zip` and `.rar` files,
renames them, writes clean `ComicInfo.xml` metadata and flags duplicates. A web interface lets you browse the library
as series, read in the browser, fix metadata, and send issues to an e-reader.

It runs on ASP.NET Core and .NET 10, stores its state in SQLite, and is usually run with Docker.

## Features

- **File watching**: new or changed comics are processed automatically. Files that arrived while the app was stopped are
  picked up at the next start.
- **Renaming and metadata**: files are renamed from a filename format, and `ComicInfo.xml` is written without losing the
  fields it already holds.
- **Series library**: browse your comics as series, with covers, in a grid or a list. Filter by genre, status, read
  state and more.
- **Web reader**: manga and webcomic modes, per-user progress, and offline reading when installed as a PWA.
- **Provider data**: optional series names, aliases, covers, synopsis, genres and publication status from ComicVine,
  MangaDex and AniList. Empty `ComicInfo` fields can be filled from the match.
- **Duplicate detection**: a file whose target name is already taken is flagged for you to review. Nothing is
  overwritten.
- **Combine Folders**: merge folders that hold the same series. Folders that still hold other files are kept and
  reported.
- **OPDS catalog**: browse and download from comic apps such as Panels, Chunky and KOReader. See [OPDS](docs/OPDS.md).
- **Email to e-reader**: send issues, a series or a selection to saved addresses, as the original file or EPUB, and
  subscribe a series to automatic delivery. See [Email to E-Reader](docs/EMAIL_DELIVERY.md).
- **Notifications and rescans**: ntfy, Gotify, Discord and JSON webhook messages, and Komga or Kavita library scans. See
  [Notifications](docs/NOTIFICATIONS.md).
- **Suwayomi downloads**: find and download missing issues through a Suwayomi server, and import finished chapters into
  the library.
- **Scheduled jobs**: backups, audits, library scans and imports, managed by administrators on the Scheduled Jobs page.
- **Accounts**: Admin, User and ReadOnly roles, API keys, and Authelia single sign-on. See [AUTHELIA.md](docs/AUTHELIA.md).
- **Runs under a subfolder**: set `BASE_PATH`, such as `/comics`, to serve the app behind a reverse proxy at a sub-path.
  See [REVERSE_PROXY.md](docs/REVERSE_PROXY.md).
- **Backups and monitoring**: daily database backups, restore from the web interface, health endpoints, Prometheus
  metrics and OpenTelemetry.

The [User Guide](docs/USER_GUIDE.md) explains how these behave. [docs/README.md](docs/README.md) lists all documentation.

## Getting started

### Docker

Published images are at `iceburn1/comicmaintainer` on Docker Hub, for `linux/amd64` and `linux/arm64`. The `dotnet` tag
is the newest release. Each release is also tagged with its version, such as `3.0.0`.

```bash
docker run -d \
  --name comicmaintainer-dotnet \
  -p 5000:5000 \
  -e PUID=$(id -u) -e PGID=$(id -g) \
  -v /path/to/comics:/watched_dir \
  -v /path/to/duplicates:/duplicates \
  -v /path/to/config:/Config \
  iceburn1/comicmaintainer:dotnet
```

Open `http://localhost:5000`. The first page creates the administrator account and asks for a setup code. The code is
in the log:

```bash
docker logs comicmaintainer-dotnet 2>&1 | grep "setup code"
```

Set `PUID` and `PGID` to the owner of your comic folders, or the app cannot rename or tag files there. The app does not
change your library's permissions. If they are wrong, it logs a warning. You can run once with
`-e FIX_LIBRARY_PERMISSIONS=true` to give the library to `PUID` and `PGID`, then remove that option.

To build the image yourself with Docker Compose:

```bash
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
cp .env.example .env    # then set PUID and PGID in it
mkdir -p test_comics duplicates config
docker compose -f docker-compose.dotnet.yml up -d
```

The compose file watches `./test_comics`. Edit its `volumes:` entries to point at your library.
[QUICKSTART.md](QUICKSTART.md) has a shorter walkthrough, and
[DOCKER_DEPLOYMENT_DOTNET.md](docs/DOCKER_DEPLOYMENT_DOTNET.md) covers production setups.

### Windows

1. Download `ComicMaintainer-vX.Y.Z-win-x64.zip` or `-win-x86.zip` from
   [GitHub Releases](https://github.com/mleenorris/ComicMaintainer/releases) and extract it.
2. Set your folders as environment variables. The app does not read a `.env` file. In PowerShell:
   ```powershell
   $env:WATCHED_DIR = "C:\Comics\ToProcess"
   $env:DUPLICATE_DIR = "C:\Comics\Duplicates"
   $env:CONFIG_DIR = "C:\ComicMaintainer\Config"
   # Optional: listen on every interface instead of localhost only
   $env:ASPNETCORE_URLS = "http://+:5000"
   ```
3. Run `ComicMaintainer.WebApi.exe` from the same window, then open `http://localhost:5000`.
4. The setup code is printed in that window.

To run it as a service, use [NSSM](https://nssm.cc/): run
`nssm install ComicMaintainer "C:\ComicMaintainer\ComicMaintainer.WebApi.exe"`, set the variables on the Environment tab
of `nssm edit ComicMaintainer`, then run `nssm start ComicMaintainer`.

### Android app

The Android app is a client for a server you already run. It holds no comics itself.

1. Create the administrator in a web browser first. The app cannot do this.
2. Download `ComicMaintainer-vX.Y.Z-android.apk` from
   [GitHub Releases](https://github.com/mleenorris/ComicMaintainer/releases) and install it. It needs Android 6.0 or later.
3. Sign in with the server's full address, such as `http://192.168.1.100:5000` or `https://comics.example.com`, and a
   password or an API key.

You can browse with search, filter by all, unmarked, marked or duplicates, and process or delete one file or a
selection. It has no reader or metadata editing. [MAUI_ANDROID_SETUP.md](docs/MAUI_ANDROID_SETUP.md) has the details.

## Configuration

Settings come from these sources, and a later one overrides an earlier one:

1. `appsettings.json`, section `AppSettings`, which holds the shipped defaults.
2. `AppSettings__<Setting>` environment variables, such as `AppSettings__MaxWorkers=2`.
3. The **Settings** page, which saves to `user-settings.json` in the config directory.
4. The dedicated environment variables, such as `WATCHED_DIR` and `SMTP_HOST`.

A dedicated environment variable pins its setting. The field shows as read-only on the Settings page, with a hint
naming the variable, and a change through the API is refused with `409`. To change a pinned setting, change or remove
the variable and restart. An `AppSettings__<Setting>` variable pins nothing: it only sets a starting value, and a value
saved on the Settings page replaces it.

[ENVIRONMENT_VARIABLES.md](docs/ENVIRONMENT_VARIABLES.md) lists every variable. These are the ones you are most likely
to need:

### Configuration Options

| Setting | Environment Variable | Default | Description |
| --- | --- | --- | --- |
| WatchedDirectory | WATCHED_DIR | `/watched_dir` | The folder of comics to watch and manage |
| DuplicateDirectory | DUPLICATE_DIR | `/duplicates` | A second folder the app may act on. Nothing is moved there for you: duplicates are only flagged |
| ConfigDirectory | CONFIG_DIR | `/Config` | Holds the database, settings, logs, backups and caches. Must be writable |
| PUID | PUID | `99` | Docker only: the user the app runs as. Set it to the owner of your library |
| PGID | PGID | `100` | Docker only: the group the app runs as |
| FIX_LIBRARY_PERMISSIONS | FIX_LIBRARY_PERMISSIONS | `false` | Docker only: at start, give everything in the library to `PUID` and `PGID`. Use once, then remove it |
| ADMIN_USERNAME | ADMIN_USERNAME | `admin` | The administrator to create at first start, used with `ADMIN_PASSWORD` |
| ADMIN_PASSWORD | ADMIN_PASSWORD | _(empty)_ | Create that administrator with this password instead of using first-run setup. Published example passwords are refused |
| SETUP_TOKEN | SETUP_TOKEN | _(generated)_ | Your own code for first-run setup. Without it, a new code is printed in the log at each start until an account exists |
| BasePath | BASE_PATH | _(empty)_ | The subfolder the app is served under, such as `/comics`. Leave empty when it is served at the domain root |
| ReverseProxySettings:TrustedProxies | TRUSTED_PROXIES | _(empty)_ | Your reverse proxies, as IP addresses or CIDR ranges. Needed behind a proxy, or every client shares one sign-in limit |
| OpdsEnabled | OPDS_ENABLED | `false` | Serve the OPDS catalog under `/opds/v1.2/` |
| EnableExternalSeriesMetadata | ENABLE_EXTERNAL_SERIES_METADATA | `false` | Turn on lookups from ComicVine, MangaDex and AniList |
| FillComicInfoFromProviders | FILL_COMICINFO_FROM_PROVIDERS | `true` | Fill empty `ComicInfo` fields from the series' provider match when a file is processed |

Two behaviours to decide on early, because both rewrite your files:

- **CBR, RAR and ZIP files become CBZ.** Writing tags saves such a file as `<name>.cbz` and deletes the original, moving
  its library record, read status and progress. A file symlink is never converted. To keep these files as they are,
  turn off **Convert CBR/RAR to CBZ when writing tags** at **Settings > Watcher**, or set
  `AppSettings__ConvertRarToCbzOnWrite=false`. Tag edits then stay in the database only, and files that normalize would
  change stay unprocessed until the setting is on and they are processed again.
- **Empty fields are filled from providers.** With a provider match, a file's empty Summary, Genre, Web, Count and Manga
  fields are filled the next time it is processed. A value already in the file or a locked Summary is never changed.
  Existing files change only when they are next processed. Filling a CBR, RAR or ZIP converts it to CBZ, unless the
  setting above is off.

Other useful settings: `AppSettings__MaxWorkers` (files processed at once, default `4`, read at startup) and
`ASPNETCORE_URLS` for the port outside Docker. In Docker, change the port by publishing another one, such as
`-p 8080:5000`.

## Security

- Sign-in uses accounts with Admin, User and ReadOnly roles. ReadOnly accounts can browse and read but cannot change the
  library. Administrators also manage settings, accounts, scheduled jobs, backups and logs.
- Only an administrator can create accounts. There is no sign-up page. Administrators add accounts, change roles, set
  passwords, and disable or delete accounts on the **Users** page, or with the
  [user account routes](docs/API.md#user-accounts). Changes that give access need the administrator's own password.
- Behind a reverse proxy, set `TRUSTED_PROXIES` so that clients are counted separately. See
  [REVERSE_PROXY.md](docs/REVERSE_PROXY.md).

See [SECURITY.md](SECURITY.md) for the security model and how to report a vulnerability.

## Monitoring, Backups & API Docs

**Health.** `GET /health` answers once the process is serving requests, and `GET /health/ready` also checks the
database. Neither needs an account.

**Metrics.** Set `TELEMETRY__PROMETHEUSENABLED=true` to serve Prometheus metrics at `/metrics`. Set
`TELEMETRY__METRICSTOKEN` to require `Authorization: Bearer <token>`; without it the endpoint is anonymous. Set
`OTEL_EXPORTER_OTLP_ENDPOINT` to push metrics and traces to an OpenTelemetry collector.

**Backups.** The `database-backup` job runs daily and keeps the newest 7 copies in `backups/` in the config directory.
Administrators can create, download, restore and delete backups at **Settings > Backups**. A restore is applied at the
next restart. See [DATABASE.md](docs/DATABASE.md).

**API.** The reference is [API.md](docs/API.md). Swagger UI is at `/swagger` when `ASPNETCORE_ENVIRONMENT=Development`,
or when `API__ENABLESWAGGER=true`. Requests need a session token (`Authorization: Bearer <token>`) or an API key
(`X-API-Key: <key>`). `POST /api/rename-file` and `DELETE /api/delete-file?filePath=` are deprecated aliases of
`POST /api/files/{encodedFilePath}/rename` and `DELETE /api/files/{encodedFilePath}`.

## Building from source

You need the .NET 10 SDK.

```bash
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
dotnet build ComicMaintainer.sln
WATCHED_DIR=/path/to/comics DUPLICATE_DIR=/path/to/duplicates CONFIG_DIR=/path/to/config \
  dotnet run --project src/ComicMaintainer.WebApi
```

The web interface is at `http://localhost:5072`. `ComicMaintainer.sln` and a bare `dotnet test` do not need the Android
workload. The Android app builds from `src/ComicMaintainer.Android.sln`.

See [CONTRIBUTING.md](CONTRIBUTING.md) to work on the project.

## License

ComicMaintainer is released under the [MIT License](LICENSE).
