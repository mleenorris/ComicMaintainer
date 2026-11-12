# Quick Start

Get ComicMaintainer running with Docker in a few minutes. For other ways to install it, see the
[README](README.md#getting-started).

## 1. Start the container

Point the volumes at your own folders. `PUID` and `PGID` must be the owner of your comic folders.

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

The image is built from the `dotnet` branch. To pin a version, use a release tag such as `iceburn1/comicmaintainer:3.0.0`.

If you would rather build the image yourself:

```bash
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
cp .env.example .env              # set PUID and PGID in it
mkdir -p test_comics duplicates config
docker compose -f docker-compose.dotnet.yml up -d
```

That compose file watches `./test_comics`. Edit its `volumes:` entries to use your library.

## 2. Create the administrator

Open `http://localhost:5000`. A new server shows a setup page that asks for a setup code. The code is in the log, and a
new one is printed at each start until an account exists:

```bash
docker logs comicmaintainer-dotnet 2>&1 | grep "setup code"
```

To skip this page, start the container with `-e ADMIN_USERNAME=<name> -e ADMIN_PASSWORD=<your password>`. Choose your
own password: the examples published with earlier versions are refused.

To add accounts for other people, sign in as the administrator and open **Users** in the sidebar.

## 3. Use the library

1. Files in the watched folder are added and processed by themselves. Files that were there before the first start are
   only added: select them and choose **Process** to process them.
2. Open **Library** to browse series, and use the filters to find unprocessed files or duplicates.
3. Open a series to read an issue or edit its metadata.
4. Open **Settings** to set the filename format and turn on features such as external metadata.

A file is rewritten when it is processed. By default a CBR, RAR or ZIP file is converted to CBZ and the original is
deleted. See [Configuration](README.md#configuration) to change this before processing a library you care about.

## Using the API

Most requests need a session token or an API key. Create a key at **Settings > OPDS Catalog**, then send it as a header:

```bash
curl -H "X-API-Key: YOUR_KEY" http://localhost:5000/api/files/counts
curl -H "X-API-Key: YOUR_KEY" http://localhost:5000/api/watcher/status

# Process every file not yet processed; the answer has a job_id
curl -X POST -H "X-API-Key: YOUR_KEY" http://localhost:5000/api/jobs/process-unmarked
```

Or sign in for a session token, which lasts 24 hours by default:

```bash
TOKEN=$(curl -s http://localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"YOU","password":"YOUR_PASSWORD"}' | jq -r .token)

curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/files/counts
```

See [API.md](docs/API.md) for every route. Swagger UI is at `/swagger` when you set `API__ENABLESWAGGER=true`.

## Run from source

You need the .NET 11 SDK. The solution builds without the Android workload.

```bash
git clone https://github.com/mleenorris/ComicMaintainer.git
cd ComicMaintainer
dotnet build ComicMaintainer.sln

export WATCHED_DIR=/path/to/comics
export DUPLICATE_DIR=/path/to/duplicates
export CONFIG_DIR=/path/to/config
dotnet run --project src/ComicMaintainer.WebApi
```

Open `http://localhost:5072`. To use another address, add `-- --urls http://localhost:5000`. The setup code is printed
in the terminal. `CONFIG_DIR` must be writable: it holds the database, settings, logs and backups.

The Android app builds separately, from `src/ComicMaintainer.Android.sln`. See
[MAUI_ANDROID_SETUP.md](docs/MAUI_ANDROID_SETUP.md).

## Troubleshooting

**The log warns that it cannot write to a folder.** `PUID` and `PGID` do not match the owner of that folder. Change them
to the owner's IDs, or start once with `-e FIX_LIBRARY_PERMISSIONS=true` and then remove that option.

**Port 5000 is taken.** Publish another port, such as `-p 8080:5000`, and open that one.

**New files are not processed.** Check that the watcher shows as running in the header, that the path inside the
container is correct, and that at least one of **Settings > Watcher > rename** or **normalize** is on. On network shares
where file events do not arrive, enable the **Library Scan** job on the Scheduled Jobs page.

**Sign-in fails after an upgrade.** Everyone signs in again after an upgrade to 3.0, and the published example passwords
no longer work. See the [Migration Guide](docs/MIGRATION_GUIDE.md).

## Next steps

- [README](README.md) for features and configuration
- [User Guide](docs/USER_GUIDE.md) for how the library, metadata and reader behave
- [Environment variables](docs/ENVIRONMENT_VARIABLES.md) for every setting
- [Docker deployment](docs/DOCKER_DEPLOYMENT_DOTNET.md) and [reverse proxies](docs/REVERSE_PROXY.md) for production use
- [GitHub Issues](https://github.com/mleenorris/ComicMaintainer/issues) to report a problem
