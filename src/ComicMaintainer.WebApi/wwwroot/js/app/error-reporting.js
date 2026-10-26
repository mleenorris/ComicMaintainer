        // ComicMaintainer SPA — last-resort client error reporting helpers (the listeners are registered in main.js).
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // =================================================================
        // Last-resort error reporting
        // =================================================================
        //
        // A rejected promise or a thrown error that no handler catches stops
        // the surrounding code silently: spinners keep spinning and stale data
        // stays on screen with nothing to explain why. These handlers do not
        // attempt recovery; they exist so a bug is visible to the user rather
        // than only to whoever has DevTools open.
        //
        // They are also the only place a front-end defect can be captured for
        // the automated issue tracker. Automated reporting hangs off the
        // server's logging pipeline, so a bug living entirely in the browser —
        // a dead button, a handler that throws — never reached it: the user saw
        // the banner below and nobody else learned anything. Each report is
        // therefore also posted to /api/client-errors, which feeds the same
        // dedupe, rate limit and GitHub issue path as a server-side failure.
        //
        // Reports are rate limited because a failure inside a loop or a render
        // path can fire continuously, and a flood of identical banners would
        // bury the rest of the UI.

        let lastUnexpectedErrorReport = 0;
        const UNEXPECTED_ERROR_REPORT_INTERVAL_MS = 10000;

        // Mirrors the server's per-field caps so an oversized stack is trimmed
        // rather than rejected as a malformed payload and lost.
        const CLIENT_ERROR_FIELD_LIMITS = { name: 100, message: 500, stack: 8000, url: 500 };

        function reportUnexpectedError(detail, kind, source) {
            console.error('Unhandled error:', detail);

            // Offline is already explained by its own banner; reporting the
            // resulting fetch failures on top of it is just noise.
            if (!navigator.onLine) return;

            // Only a report that is actually sent opens the window, so a
            // failure while offline cannot hide the first one after reconnecting.
            const now = Date.now();
            if (now - lastUnexpectedErrorReport < UNEXPECTED_ERROR_REPORT_INTERVAL_MS) return;
            lastUnexpectedErrorReport = now;

            sendClientErrorReport(detail, kind, source);

            // showMessage() needs #messageContainer, which only exists on the
            // library page; nothing to do elsewhere.
            if (typeof showMessage !== 'function' || !document.getElementById('messageContainer')) return;

            showMessage('Something went wrong. If the page is not behaving as expected, reload it.', 'error');
        }

        /**
         * Posts one uncaught failure to the server. Fire-and-forget, and silent
         * on failure by design: anything thrown or rejected in here would
         * arrive straight back at the handlers below and loop.
         */
        function sendClientErrorReport(detail, kind, source) {
            try {
                const described = describeClientError(detail);
                const body = JSON.stringify({
                    name: clipClientErrorField(described.name, CLIENT_ERROR_FIELD_LIMITS.name),
                    message: clipClientErrorField(described.message, CLIENT_ERROR_FIELD_LIMITS.message),
                    stack: clipClientErrorField(described.stack, CLIENT_ERROR_FIELD_LIMITS.stack),
                    source: clipClientErrorField(source, CLIENT_ERROR_FIELD_LIMITS.url),
                    // Path only: a query string can carry a search term or a
                    // file path, and the server needs neither to locate a bug.
                    url: clipClientErrorField(window.location?.pathname, CLIENT_ERROR_FIELD_LIMITS.url),
                    kind: kind === 'unhandledrejection' ? 'UnhandledRejection' : 'Error'
                });

                // getAuthHeaders() redirects to the login page when the token
                // has expired. That is right for a user-initiated request and
                // wrong here, where it would navigate away from the very state
                // the failure needs to be reproduced in, so the one header that
                // matters is attached directly.
                const headers = { 'Content-Type': 'application/json' };
                const token = localStorage.getItem('jwt_token');
                if (token) headers.Authorization = 'Bearer ' + token;

                fetch(apiUrl('/api/client-errors'), {
                    method: 'POST',
                    credentials: 'include',
                    // Lets the report survive being filed as the page unloads.
                    keepalive: true,
                    headers,
                    body
                }).catch(() => {});
            } catch (_) {
                // Reporting is best effort and must never mask the failure it
                // was trying to describe.
            }
        }

        /**
         * Pulls name/message/stack off whatever was thrown or rejected.
         * `instanceof Error` is not usable on its own: a rejection reason is
         * often a plain error-like object, and a real Error from another realm
         * (an iframe, a worker) fails the check. Stringifying those yields
         * "[object Object]", which loses the stack and makes every unrelated
         * failure share one fingerprint.
         */
        function describeClientError(detail) {
            if (detail === null || detail === undefined) {
                return { name: '', message: String(detail), stack: '' };
            }

            if (typeof detail === 'object' || typeof detail === 'function') {
                const message = typeof detail.message === 'string' && detail.message
                    ? detail.message
                    : String(detail);
                return {
                    name: typeof detail.name === 'string' ? detail.name : '',
                    message,
                    stack: typeof detail.stack === 'string' ? detail.stack : ''
                };
            }

            return { name: '', message: String(detail), stack: '' };
        }

        function clipClientErrorField(value, maxLength) {
            if (value === null || value === undefined) return '';
            const text = String(value);
            return text.length > maxLength ? text.slice(0, maxLength) : text;
        }
