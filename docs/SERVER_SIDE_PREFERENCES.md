# User Preferences

Each account's preferences are stored on the server, so they follow the user across browsers and devices.

## Stored on the server

The `UserPreferences` table holds one row per user. Unset values fall back to the defaults below.

| Preference | Values | Default |
| --- | --- | --- |
| `theme` | `light`, `dark` | Follows the browser's dark mode setting |
| `perPage` | 1 to 1000 | 100 |
| `readingMode` | `manga`, `webcomic` | `manga` |
| `filterMode` | `all`, `unmarked`, `marked`, `duplicates`, `renamed`, `normalized`, `read`, `unread`, `matched`, `unmatched`, `missing` | `all` |
| `sortMode` | `name`, `date`, `size` | `name` |
| `sortDirection` | `asc`, `desc` | `asc` |

`GET /api/preferences` returns these, merged with the defaults. `PUT /api/preferences` saves the fields you send and
leaves the rest alone. A value outside the list is refused with `400`.

```bash
curl -X PUT http://localhost:5000/api/preferences \
  -H "X-API-Key: YOUR_KEY" -H "Content-Type: application/json" \
  -d '{"theme":"dark","perPage":50}'
```

Read-only accounts can save their own preferences.

The reader keeps its own per-user settings, such as the default mode and reading direction, in the `ReaderPreferences`
table. They are read and written with `GET` and `PUT /api/reader/preferences`.

## Stored in the browser

These stay in the browser's `localStorage`, and are not shared between devices:

| Key | Use |
| --- | --- |
| `jwt_token`, `username`, `authelia_authenticated` | The current session |
| `theme` | The theme on the sign-in and setup pages, before an account is known |
| `readingMode`, `readingDirection` | The reader's most recent choice |
| `seriesLayout` | Grid or list in the library |
| `progressModalMinimized` | Whether the job progress window is minimized |
| `debug_api` | Set to `true` to log each API request in the console |

Which job is running is known to the server. The page finds it through `GET /api/active-job`.

## Related settings

Server-wide settings, such as the filename format, are not preferences. They are stored in `user-settings.json` and
changed at **Settings** by administrators. See [Configuration](../README.md#configuration).
