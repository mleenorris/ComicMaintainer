# Real-Time Events

The server pushes changes to the web interface with Server-Sent Events (SSE). There is one stream,
`GET /api/events/stream`, and it is one-way: the server sends and clients listen.

## Message format

Each event is a `data:` line holding JSON with a `type` and a `data` object:

```text
data: {"type":"job_updated","data":{"job_id":"...","status":"running","progress":{"processed":3,"total":10,"success":3,"errors":0,"percentage":30}}}
```

| Type | Sent when | `data` |
| --- | --- | --- |
| `job_updated` | A batch job changes state or progresses | `job_id`, `status`, `progress` with `processed`, `total`, `success`, `errors`, `percentage` |
| `file_processed` | A file in a batch job finishes, and after single-file metadata edits | `filename`, `success`, `error` |
| `file_list_updated` | Files are added to or removed from the library, or after a batch of moves | Empty |
| `watcher_status` | The watcher starts or stops | `running`, `enabled` |

Every 30 seconds the server also sends a named `heartbeat` event, so a client can tell a live stream from one a proxy has
dropped:

```text
event: heartbeat
data: {}
```

## Connecting

The stream accepts:

- **A stream token.** `EventSource` cannot send headers, so the browser calls `POST /api/auth/stream-token` and passes the
  result as `?access_token=<token>`. A stream token works once, lasts 60 seconds and is refused everywhere else. Session
  tokens are never accepted in a URL.
- **An API key** in the `X-API-Key` header, for scripts.
- **Authelia**, through the reverse proxy.

```bash
curl -N -H "X-API-Key: YOUR_KEY" http://localhost:5000/api/events/stream
```

A stream opened with a token or an API key ends at the first heartbeat after its session or key stops being valid.

A reverse proxy that buffers responses holds events back, so turn buffering off for this route.

## The server

`EventBroadcasterService` (`IEventBroadcaster`) holds the connected clients. Broadcasts never throw to the caller. Writes
to one client are serialized, and a client that does not take an event within 10 seconds is disconnected, so it cannot
hold up the others. Its browser reconnects.

## The web interface

`js/app/events.js` manages the connection:

- Each connection gets a fresh stream token, so the browser's own reconnect, which reuses the URL, cannot work. Every
  error closes the stream, and the app reconnects by itself, starting at 5 seconds and backing off to 60.
- A stream silent for 75 seconds counts as dead and is replaced.
- While the stream is down, the active job's status is polled every 5 seconds.
- After 20 consecutive failures the user is told, and the app keeps retrying. It reconnects at once when the browser comes
  back online or the tab becomes visible.
- A reconnected stream catches up on the job, the watcher status and the library, since events sent while it was down are
  lost.
- Under Authelia, the app returns to the login page only on a definite answer that the session has ended. An outage is
  retried like any other failure.

## Adding an event

1. Add a method to `IEventBroadcaster` and implement it in `EventBroadcasterService`.
2. Call it from the service that makes the change, after the change is committed.
3. Handle the new type in `handleServerEvent` in `js/app/events.js`.
4. Add tests on both sides.
