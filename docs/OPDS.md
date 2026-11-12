# OPDS Catalog

ComicMaintainer can serve your library as an OPDS 1.2 catalog with OPDS-PSE page streaming. A comic app that supports OPDS can then browse the library, search it, download comics and read them page by page. Your reading position is shared with the web reader.

Apps name these things differently, so the steps below are general. Look for "add catalog", "add server" or "OPDS" in your app's settings.

## Enable the Catalog

The catalog is **off by default**, because it adds a way to sign in. An administrator can turn it on in either of these ways:

- **Settings > OPDS Catalog > Enable the OPDS catalog**, then save. It takes effect at once, with no restart.
- The environment variable `OPDS_ENABLED=true`. The checkbox then shows as read-only in Settings.

While it is off, every catalog address answers 404, as if it did not exist.

## The Feed URL

Add this address to your comic app. **Settings > OPDS Catalog** shows it, already filled in for your server:

```
https://comics.example.com/opds/v1.2/catalog
```

For a [subfolder install](REVERSE_PROXY.md#serving-under-a-subfolder-base_path), the folder comes first:

```
https://example.com/comics/opds/v1.2/catalog
```

## Signing In

Your app asks for a username and a password. The catalog uses HTTP Basic sign-in, and it accepts:

| Username | Password field | When |
|---|---|---|
| Your username | Your API key | Always. Recommended for apps |
| Your username | Your account password | Only when Authelia is not enabled |

An app stores whatever you give it, so an API key is the better choice: you can replace it without touching your password.

### Create an API Key

1. Sign in to the web interface and open **Settings > OPDS Catalog**.
2. Enter your current password. If you signed in through Authelia, no password is asked for.
3. Select **Create API key**.
4. Copy the key straight away: it is shown only once.
5. In your comic app, enter your username, and the key as the password.

The key acts as your account, with your current role, and each account has one key. **Replace API key** on the same card makes a new one and ends the old one. These also end a key: changing your password, and signing out of all sessions.

A device still holding an old key gets 401 and uses up that address's sign-in attempts, but it never locks your account. Enter the new key on that device.

### With Authelia

Comic apps cannot follow Authelia's login page, so the catalog has to bypass Authelia and ComicMaintainer signs those requests in itself. While Authelia is enabled:

- The catalog accepts **only** a username with an API key. Local passwords are refused, and accounts Authelia created have no usable local password.
- Create the key at **Settings > OPDS Catalog** while signed in through Authelia.
- Add a `bypass` rule for the catalog to your Authelia configuration, **before** any other rule for the same domain, because Authelia uses the first rule that matches:

```yaml
access_control:
  rules:
    - domain: comics.example.com
      resources:
        - '^/opds/.*$'
      policy: bypass
```

For a subfolder install, include the folder: `'^/comics/opds/.*$'`. See the [Authelia Integration Guide](AUTHELIA.md#opds-catalog-and-comic-apps).

### Use HTTPS

HTTP Basic credentials are only base64-encoded, not encrypted, and the app sends them with every request. Over plain HTTP, anyone on the network path can read your password or key. Use HTTPS for any access from outside your own network: see the [Reverse Proxy Guide](REVERSE_PROXY.md) or the [HTTPS Setup Guide](HTTPS_SETUP.md).

## What the Catalog Offers

The start of the catalog lists:

| Feed | Contents |
|---|---|
| Continue Reading | The issue to resume in each series you are partway through |
| Series Updates | Series that gained an issue recently |
| Newly Added | Series added recently |
| All Series | Every series, 50 to a page |

Each series lists its issues, 50 to a page. Apps that support OpenSearch can also search the library, through `/opds/v1.2/opensearch.xml`.

Each issue offers:

- **A download** of the original file, which can be resumed where the app supports it.
- **Page streaming** (OPDS-PSE), so the app can show pages without downloading the whole comic. Page numbers in these addresses start at 0. An issue whose pages cannot be listed is offered for download only.

Pages are sent in their own format when every page in the comic is the same JPEG, PNG or GIF type. Any other comic, including those with WebP pages, is sent as JPEG, because that is what the OPDS page-streaming format allows.

### Reading Progress

Only page requests record your reading position. Reaching the last page marks the issue as read. Downloading a file, or a cover being shown, does not count as reading.

Apps that fetch pages ahead of the one on screen move your position as they fetch.

## Behind a Reverse Proxy

Links in the catalog are path-absolute and start with `BASE_PATH`, so they work whether the proxy forwards or strips the prefix.

Set [`TRUSTED_PROXIES`](REVERSE_PROXY.md#trusted-proxies-and-rate-limiting) to your proxy. Without it, every client behind the proxy shares one sign-in limit.

## Troubleshooting

| Symptom | Cause |
|---|---|
| 404 on every address | The catalog is off, or the address is missing `/opds/v1.2` or the `BASE_PATH` folder. |
| 401, or the app keeps asking for a password | Wrong username, password or key. An old key stops working when it is replaced, when the password changes and when you sign out of all sessions. With Authelia, only an API key works. |
| 429 Too Many Requests | Sign-in attempts are limited to 10 per 5 minutes per client address, shared with the web sign-in. Wait a few minutes. Behind a proxy, also check `TRUSTED_PROXIES`. |
| The app gets a login page or an HTML error | The request reached Authelia, so the bypass rule is not matching. Check the domain, the path and the rule's order. |
| Sign-in works but the app shows nothing | Check that the web interface shows the library, then open the feed address in a browser and sign in to see what the catalog returns. |
| 422 when opening a comic | The archive is over a read limit, listed in the [Performance Tuning Guide](PERFORMANCE_TUNING.md#archive-limits). The error names the entry and the limit. |
