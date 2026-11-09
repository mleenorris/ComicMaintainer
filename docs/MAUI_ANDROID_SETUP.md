# Android App

ComicMaintainer has a small .NET MAUI app for Android. It is a client of your server: it signs in over the REST API
and works on the library the server holds. It stores no comics on the device and contains no server code.

The web interface does far more, and installs on Android as a PWA with offline reading. Use the app if you want a
native way to browse the library and process or delete files.

## What the app does

- Signs in with a password or an API key
- Lists the library, page by page, with search
- Filters by all, unmarked, marked or duplicates
- Processes or deletes one file or a selection

It has no comic reader, no offline mode, no metadata editing and no iOS version. Read-only accounts can browse, and
are told they lack permission if they try to process or delete.

## Requirements

- Android 6.0 (API 23) or later
- A ComicMaintainer 3.x server the device can reach, already set up. A new server has no accounts: create the
  administrator in a web browser first, and the app says so if you try to sign in before that.

## Installing

1. Download `ComicMaintainer-vX.Y.Z-android.apk` from
   [GitHub Releases](https://github.com/mleenorris/ComicMaintainer/releases).
2. Allow installing from unknown sources for your browser or file manager.
3. Open the APK.

**Updating:** release APKs are signed with a temporary key that differs on every build, so Android refuses to install
one over another. Uninstall the app first, then install the new version and sign in again.

## Signing in

Enter the server's full address, including `http://` or `https://`. A server at a sub-path works too, such as
`https://example.com/comics`.

### With a password

Enter your username and password. The app sends the password once to get a session token, and never stores it.

A session lasts 24 hours by default (`JwtSettings:ExpirationMinutes` on the server). There is no refresh: when it
ends, sign in again.

### With an API key

An API key lasts until it is revoked, so you do not have to sign in every day. The app only uses a key you already
have; it never creates one, because creating a key replaces the account's previous one.

The web interface has no page for API keys yet, so create one through the API. It needs your current password, and the
key is shown only once:

```bash
TOKEN=$(curl -s https://comics.example.com/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"YOU","password":"YOUR_PASSWORD"}' | jq -r .token)

curl -s https://comics.example.com/api/auth/api-key \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"currentPassword":"YOUR_PASSWORD"}'
```

Then turn on **Sign in with an API key** and paste it. Changing your password or signing out of all sessions revokes
the key. Signing out of the app leaves it valid on the server.

### When sign-in fails or a session ends

The app returns to the sign-in page and says why:

| What you see | What it means |
| --- | --- |
| Invalid username or password | The credentials are wrong, or the account is locked out after repeated failures. The server does not say which. |
| Too many requests. Please try again in N minutes. | Each client gets 10 sign-in attempts per 5 minutes. Wait for the time shown. |
| Your session has ended | The token expired, the password or roles changed, the account was disabled, or you signed out of all sessions elsewhere. |
| The server no longer accepts this API key | The key was revoked or replaced. |
| This server has not been set up yet | Create the administrator in a web browser. |
| The server redirected the app to... | See [Reverse proxies](#reverse-proxies-and-single-sign-on). |
| This device does not trust the server's HTTPS certificate | See [HTTPS certificates](#https-certificates). |

A network problem does not sign you out. The app keeps your session and shows the error.

## Connecting

**Settings > Test Connection** checks that the address really is a ComicMaintainer server, without needing an account.

### Plain HTTP

`http://` addresses work, because self-hosted servers are often reached that way on a home network. The app warns
about them: anyone on the network between the device and the server can read your password and session. Use HTTPS for
anything outside a network you trust.

### HTTPS certificates

The app accepts only certificates that Android trusts by default, which means one from a public authority such as
Let's Encrypt. A self-signed certificate, or one from your own authority, fails with a message saying so, even if you
have installed that authority on the device, because Android apps do not trust user-installed authorities by default.

### Reverse proxies and single sign-on

The app does not follow redirects, so that a credential is never carried to another address. If the server redirects
`http` to `https`, enter the `https` address.

The app cannot complete a sign-on page such as Authelia. If one protects the server, the proxy has to pass the app's
requests through to ComicMaintainer without redirecting them. Then sign in with a password or API key as usual.

## Security

- The session token or API key is kept in Android's SecureStorage, encrypted with a key held by the Android Keystore.
- The app's data is excluded from Android backups, since the encrypted values could not be decrypted on restore.
- A credential is tied to the server that issued it. Changing the server signs you out, and ends the session on the old
  server if it can be reached.
- Signing out ends the session on the server. If the server cannot be reached, the app says so, and the token stays valid
  there until it expires.

## Building

You need the .NET 10 SDK, the Android workload, a JDK and the Android SDK:

```bash
dotnet workload install maui-android

# Installs the JDK and Android SDK, if you do not have them. This accepts the Android SDK license.
dotnet build src/ComicMaintainer.MauiApp -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$HOME/jdk -p:AcceptAndroidSDKLicenses=true
```

The app has its own solution, `ComicMaintainer.Android.sln`, so that `ComicMaintainer.sln` builds without any of this.
Open it in Visual Studio, or from the command line:

```bash
# Debug build
dotnet build ComicMaintainer.Android.sln

# Run on a connected device or emulator
dotnet build src/ComicMaintainer.MauiApp -t:Run -f net10.0-android

# Release APK: bin/Release/net10.0-android/publish/com.comicmaintainer.app-Signed.apk
dotnet publish src/ComicMaintainer.MauiApp -c Release -f net10.0-android -p:AndroidPackageFormat=apk
```

To sign with a key of your own, add `-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=...`,
`-p:AndroidSigningKeyAlias=...`, `-p:AndroidSigningKeyPass=...` and `-p:AndroidSigningStorePass=...`.

### Releases

`.github/workflows/build-android-apk.yml` builds the APK. The release workflow (`version-bump.yml`) calls it with each
new release tag and it attaches the APK to that release. A tag pushed by a workflow cannot start another workflow, so
this workflow has no tag trigger. You can also run it by hand: with a tag to rebuild that release's APK, or without one
to build the selected branch and keep the APK as a workflow artifact.

## Project layout

```
src/ComicMaintainer.MauiApp/
├── Models/              # The API's JSON, as the app reads it
├── Services/            # HTTP, sign-in and session handling (no MAUI types)
│   └── Platform/        # SecureStorage, Preferences, dialogs and navigation (MAUI)
├── ViewModels/          # Page logic (no MAUI types)
├── Views/               # LoginPage, MainPage (the library), SettingsPage
└── Platforms/Android/   # Manifest and Android entry points
```

The app does not reference `ComicMaintainer.Core`, which would pull the server and its database into the APK. JSON is
source-generated (`ApiJsonContext`), because release builds trim the app.

### API endpoints the app uses

| Endpoint | Used for |
| --- | --- |
| `GET /api/version` | Testing the connection (no account needed) |
| `GET /api/auth/setup-required` | Recognising a server with no accounts (no account needed) |
| `POST /api/auth/login` | Signing in with a password |
| `GET /api/auth/user` | Checking a session or API key, and naming the account |
| `POST /api/auth/logout` | Ending the session on the server |
| `GET /api/files` | Listing the library (`page`, `per_page`, `filter`, `search`) |
| `POST /api/files/process?filePath=` | Processing one file |
| `POST /api/jobs/process-selected` | Processing a selection |
| `DELETE /api/files/{base64url path}` | Deleting one file |
| `POST /api/jobs/delete-selected` | Deleting a selection |
| `GET /api/jobs/{id}` | Reading a job's progress |

## Tests

`Models`, `Services` (except `Platform`), `ViewModels` and `Extensions` are free of MAUI types and are compiled into
`tests/ComicMaintainer.Tests` as well, so they run without the Android workload. `AndroidApp/AndroidAppContractTests`
runs that code against the real server pipeline, so a change to a route, parameter or JSON field that the app depends on
fails the test suite:

```bash
dotnet test tests/ComicMaintainer.Tests --filter "FullyQualifiedName~AndroidApp"
```

Keep MAUI types out of those folders, or the test project stops compiling. The pages and `Services/Platform` are not
covered by automated tests.
