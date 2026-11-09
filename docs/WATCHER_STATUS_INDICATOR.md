# Watcher Status Indicator

The header, the sidebar and the settings menu show whether the file watcher is running.

| Shown | Meaning |
| --- | --- |
| **Watcher Running** | The watcher is monitoring the watched folder |
| **Watcher Stopped** | The watcher is not running |
| **Status Unknown** | The status could not be fetched |

On small screens the header shows only the coloured dot.

The watcher runs while **rename** or **normalize** is on at **Settings > Watcher**. If both are off, it shows as stopped.
It also shows as stopped if the watched folder is unavailable and the watcher has not been able to start.

## How it works

The page calls `GET /api/watcher/status` when it loads and each time the event stream reconnects. In between, the
`watcher_status` event on the [event stream](EVENT_BROADCASTING_SYSTEM.md) keeps it current, so the page does not poll.

The response is:

```json
{ "running": true, "enabled": true }
```

`enabled` has the same value as `running`. `GET /api/watcher` returns the same thing.

To turn the watcher's actions on or off, use **Settings > Watcher**, or `PUT /api/settings/watcher-enable-rename` and
`PUT /api/settings/watcher-enable-normalize`.

If comics are not being picked up, see [Background Processing](ASYNC_PROCESSING.md#the-watcher).
