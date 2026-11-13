        // ComicMaintainer SPA — API plumbing: failure references, auth/JWT handling, apiFetch(), preferences, showMessage().
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // =================================================================
        // API failure references
        // =================================================================
        //
        // Every request the server handles is tagged with a correlation id and
        // echoes it back in `X-Correlation-Id` (see RequestCorrelationMiddleware);
        // every log line written while that request is in flight carries the same
        // id. Without surfacing it, a user reporting "it said 'Failed to load
        // series'" gives an operator nothing to search for — the message is
        // identical for a permissions problem, a corrupt archive and a database
        // lock.
        //
        // The id travels with the failure it belongs to: a caller that turns a
        // failed response into an Error builds it with apiResponseError(), which
        // keeps that response's id as `error.correlationId`, and reports the
        // caught error with `{ reference: error }` so showMessage() quotes that
        // id and no other. A failure without one — a request that never reached
        // the server, or a proxy error page without the header — is reported
        // without a reference: quoting whichever request happened to fail before
        // it would send an operator to the wrong log line. Decoration is opt-in
        // per message for the same reason; a clipboard denial, a validation
        // message or a JavaScript error has no request of its own to point at.

        /**
         * The correlation id of a failed API response, or null when it has none
         * worth reporting.
         */
        function apiFailureReference(response) {
            try {
                // 401/403 are routine — an expired session or a read-only user —
                // and are already explained by their own handling, so a support
                // reference would be noise.
                if (response.status === 401 || response.status === 403) return null;
                return response.headers.get('X-Correlation-Id') || null;
            } catch {
                // Opaque (cross-origin) responses disallow header access; there
                // is nothing to report.
                return null;
            }
        }

        /**
         * The Error to throw for a failed API response: `message` (by default
         * the HTTP status) with the response's `status` and `correlationId`
         * attached, so reporting it quotes this request's reference.
         */
        function apiResponseError(response, message) {
            const error = new Error(message ?? `HTTP error! status: ${response.status}`);
            error.status = response.status;
            error.correlationId = apiFailureReference(response);
            return error;
        }

        /**
         * Append a correlation reference to an error message. `reference` is
         * either an explicit id or the error being reported, whose own
         * `correlationId` (see apiResponseError) is used; an error without one,
         * such as a network failure, leaves the message as it is.
         */
        function withApiFailureReference(message, reference) {
            const ref = typeof reference === 'string' ? reference : reference?.correlationId;
            if (typeof ref !== 'string' || !ref) return message;
            if (String(message).includes(ref)) return message;
            return `${message} (ref: ${ref})`;
        }

        // Helper function to decode JWT token and extract expiration
        function decodeJwtToken(token) {
            try {
                // JWT format: header.payload.signature
                const parts = token.split('.');
                if (parts.length !== 3) {
                    return null;
                }
                
                // Decode the payload (second part)
                const payload = parts[1];
                // Add padding if needed for base64 decode
                const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
                const paddedBase64 = base64.padEnd(base64.length + (4 - base64.length % 4) % 4, '=');
                const jsonPayload = decodeURIComponent(atob(paddedBase64).split('').map(function(c) {
                    return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
                }).join(''));
                
                return JSON.parse(jsonPayload);
            } catch (error) {
                console.error('Error decoding JWT token:', error);
                return null;
            }
        }
        
        // Helper function to check if JWT token is expired
        function isTokenExpired(token) {
            const decoded = decodeJwtToken(token);
            if (!decoded || !decoded.exp) {
                return true; // Treat invalid token as expired
            }
            
            // JWT exp claim is in seconds, Date.now() is in milliseconds
            const expirationTime = decoded.exp * 1000;
            const currentTime = Date.now();
            
            return currentTime >= expirationTime;
        }
        
        // Set once the page is on its way to the login screen. Whatever was
        // in flight then fails, and telling the user about each of those
        // failures on the way out would only be noise.
        let loginRedirectStarted = false;

        function isRedirectingToLogin() {
            return loginRedirectStarted && !localStorage.getItem('jwt_token');
        }

        // Helper function to redirect to login and clear auth data
        function redirectToLogin() {
            loginRedirectStarted = true;
            localStorage.removeItem('jwt_token');
            localStorage.removeItem('username');
            localStorage.removeItem('authelia_authenticated');
            localStorage.removeItem('client_cache_session_id');
            clearCachedImages();
            window.location.href = apiUrl('/login.html');
        }

        /**
         * Opaque, per-login identifier used to scope the service worker's
         * image cache to the current session. It is created the first time a
         * request is made after login and cleared on logout (redirectToLogin
         * above), so the cache key changes whenever the signed-in identity
         * changes - even for Authelia's cookie-based auth, where the service
         * worker cannot read the session cookie itself to tell sessions apart.
         */
        function getOrCreateClientCacheSessionId() {
            let id = localStorage.getItem('client_cache_session_id');
            if (!id) {
                id = (crypto.randomUUID ? crypto.randomUUID() : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`);
                localStorage.setItem('client_cache_session_id', id);
            }
            return id;
        }

        /**
         * Ask the service worker to drop its cached comic covers and pages.
         * Those responses are authenticated content, so they must not survive a
         * sign-out and be served to whoever signs in next on the same device.
         */
        function clearCachedImages() {
            try {
                if ('serviceWorker' in navigator && navigator.serviceWorker.controller) {
                    navigator.serviceWorker.controller.postMessage({ type: 'CLEAR_IMAGE_CACHE' });
                }
            } catch (error) {
                console.log('PWA: Failed to request image cache clear', error);
            }
        }
        
        // Authentication check - check server auth status first (supports Authelia)
        // Returns a promise that resolves when authentication is verified or rejects if auth fails
        async function checkAuth() {
            try {
                // First check server auth status (includes Authelia check)
                const response = await apiFetch('/api/auth/status', { auth: false, json: false });
                
                if (response.ok) {
                    const authStatus = await response.json();
                    console.log('[AUTH] Server auth status:', authStatus);
                    
                    // If Authelia is enabled and user is authenticated, no need for JWT token
                    if (authStatus.autheliaEnabled && authStatus.isAuthenticated) {
                        console.log('[AUTH] Authenticated via Authelia as:', authStatus.username);
                        localStorage.setItem('authelia_authenticated', 'true');
                        localStorage.setItem('username', authStatus.username);
                        return true; // User is authenticated via Authelia
                    }
                    
                    // If Authelia is not enabled or user not authenticated, check JWT token
                    const token = localStorage.getItem('jwt_token');
                    if (!token) {
                        console.log('[AUTH] No JWT token and not authenticated via Authelia');
                        redirectToLogin();
                        return false;
                    }
                    
                    // Check if JWT token is expired
                    if (isTokenExpired(token)) {
                        console.log('[AUTH] JWT token has expired, redirecting to login');
                        redirectToLogin();
                        return false;
                    }
                    
                    console.log('[AUTH] Authenticated via JWT token');
                    return true;
                } else {
                    // Fallback to JWT token check if status endpoint fails
                    const token = localStorage.getItem('jwt_token');
                    if (!token) {
                        redirectToLogin();
                        return false;
                    }
                    
                    if (isTokenExpired(token)) {
                        console.log('[AUTH] JWT token has expired, redirecting to login');
                        redirectToLogin();
                        return false;
                    }
                    return true;
                }
            } catch (error) {
                console.error('[AUTH] Error checking auth status:', error);
                // Fallback to JWT token check on error
                const token = localStorage.getItem('jwt_token');
                if (!token) {
                    redirectToLogin();
                    return false;
                }
                
                if (isTokenExpired(token)) {
                    console.log('[AUTH] JWT token has expired, redirecting to login');
                    redirectToLogin();
                    return false;
                }
                return true;
            }
        }
        
        // Helper function to get auth headers
        function getAuthHeaders({ allowExpiredToken = false } = {}) {
            const isAutheliaAuth = isAutheliaAuthenticated();
            
            // If authenticated via Authelia, don't need Authorization header
            // The cookies will be sent automatically with credentials: 'include'
            if (isAutheliaAuth) {
                apiDebugLog('Using Authelia authentication (cookies)');
                return {
                    'Content-Type': 'application/json',
                    'X-Client-Session': getOrCreateClientCacheSessionId()
                };
            }
            
            const token = localStorage.getItem('jwt_token');
            
            // Proactively check token expiry before making API calls
            if (token && !allowExpiredToken && isTokenExpired(token)) {
                console.log('[AUTH] JWT token expired, redirecting to login');
                redirectToLogin();
                return {};
            }
            
            const headers = {
                'Content-Type': 'application/json',
                'X-Client-Session': getOrCreateClientCacheSessionId()
            };
            if (token) {
                headers['Authorization'] = `Bearer ${token}`;
                apiDebugLog('Adding authorization header (token present)');
            } else {
                console.warn('[AUTH] No JWT token found in localStorage');
            }
            return headers;
        }
        
        // Per-request logging is off by default: a large library makes one
        // request per cover, and a console full of these hides the real
        // errors. Run localStorage.setItem('debug_api', 'true') in the browser
        // console to turn it on.
        function isApiDebugEnabled() {
            try {
                return localStorage.getItem('debug_api') === 'true';
            } catch {
                return false;
            }
        }

        function apiDebugLog(...details) {
            if (isApiDebugEnabled()) console.debug('[API]', ...details);
        }

        /**
         * What a failed response says went wrong, or undefined when it says
         * nothing a user could read. The API answers with ProblemDetails plus
         * an `error` string, older endpoints with `error` or `message` alone,
         * and BadRequest("...") with the bare text. A 5xx body that is not
         * JSON comes from a proxy or an error page, so it is left out.
         */
        async function apiErrorMessage(response) {
            let text = '';
            try {
                text = await response.text();
            } catch {
                return undefined;
            }

            let body;
            try {
                body = JSON.parse(text);
            } catch {
                const isPlainText = /^text\/plain/i.test(response.headers.get('Content-Type') || '');
                return response.status < 500 && isPlainText && text.trim() ? text.trim() : undefined;
            }

            const candidates = typeof body === 'string'
                ? [body]
                : [body?.error, body?.detail, body?.message, body?.title];
            return candidates.find(value => typeof value === 'string' && value.trim());
        }

        /**
         * The error for a session that has ended. The user is already on the
         * way to the login page, so `authHandled` tells callers there is
         * nothing left to report.
         */
        function apiSessionEndedError(status = 401) {
            const error = new Error('Authentication required');
            error.status = status;
            error.correlationId = null;
            error.authHandled = true;
            return error;
        }

        /**
         * Sends a request to the app's API. Every request goes through here,
         * so credentials, the session headers and the handling of an ended
         * session are the same everywhere.
         *
         * Options, besides what fetch() itself takes (signal, keepalive, ...):
         *  - method: GET unless given.
         *  - body: an object or array is sent as JSON; a string, FormData or
         *    Blob is sent as it is.
         *  - json: true (the default) parses the answer and returns it (`{}`
         *    when the answer is empty), and throws apiResponseError() for a
         *    failed one, with the server's own message when it gave one
         *    (`fromServer` says so).
         *    false returns the Response, for binary answers and for callers
         *    that treat some statuses specially.
         *  - errorMessage: the message for a failure the server did not explain.
         *  - forbiddenMessage: the message for a 403 the server did not explain.
         *    A bare "Forbidden", which is all the sign-in layer sends, explains
         *    nothing; a controller's own reason is kept.
         *  - auth: false sends no session headers and leaves a 401 to the
         *    caller, for the requests that are made without a session.
         *  - redirectOn401: false leaves a 401 to the caller but still sends
         *    the session headers.
         *  - allowExpiredToken: true sends the request even when the stored
         *    token has expired, for a page that also works offline: the
         *    service worker can still answer it, and a server that is reachable
         *    answers with a 401.
         *
         * A 401 means the session has ended: the user is sent to the login
         * page and this throws an error flagged `authHandled`.
         */
        async function apiFetch(path, options = {}) {
            const {
                method = 'GET',
                body,
                json = true,
                auth = true,
                redirectOn401 = true,
                allowExpiredToken = false,
                errorMessage,
                forbiddenMessage = 'You do not have permission to do that.',
                headers,
                ...fetchOptions
            } = options;

            const sessionHeaders = auth ? getAuthHeaders({ allowExpiredToken }) : {};
            // getAuthHeaders() answers with nothing only when it found an
            // expired token and started the redirect: sending the request
            // would just earn a 401.
            if (auth && Object.keys(sessionHeaders).length === 0) throw apiSessionEndedError();
            const requestHeaders = { ...sessionHeaders, ...headers };

            const request = { method, credentials: 'include', ...fetchOptions, headers: requestHeaders };
            if (body !== undefined && body !== null) {
                const sentAsIs = typeof body === 'string' || body instanceof FormData || body instanceof Blob;
                request.body = sentAsIs ? body : JSON.stringify(body);
                if (body instanceof FormData) {
                    // The browser adds the multipart boundary itself.
                    delete requestHeaders['Content-Type'];
                } else if (!sentAsIs && !requestHeaders['Content-Type']) {
                    requestHeaders['Content-Type'] = 'application/json';
                }
            }

            const url = apiUrl(path);
            apiDebugLog(method, url);
            const response = await fetch(url, request);

            if (auth && redirectOn401 && response.status === 401) {
                console.log('Received 401 Unauthorized, redirecting to login');
                redirectToLogin();
                throw apiSessionEndedError(response.status);
            }
            if (!json) return response;

            if (!response.ok) {
                const reason = await apiErrorMessage(response);
                const isBareRefusal = response.status === 403 && reason?.toLowerCase() === 'forbidden';
                const serverMessage = isBareRefusal ? undefined : reason;
                const fallback = response.status === 403
                    ? forbiddenMessage
                    : errorMessage ?? `HTTP error! status: ${response.status}`;
                const error = apiResponseError(response, serverMessage ?? fallback);
                error.fromServer = serverMessage !== undefined;
                throw error;
            }

            const text = await response.text();
            if (!text) return {};
            try {
                return JSON.parse(text);
            } catch {
                // An endpoint that answers 200 with plain text has nothing more
                // to say. An HTML page instead of JSON is a proxy's sign-in or
                // error page, which must not pass for an empty result.
                if (/html/i.test(response.headers.get('Content-Type') || '')) {
                    throw apiResponseError(response, errorMessage ?? 'The server sent an unexpected response.');
                }
                return {};
            }
        }

        // Helper function to encode filepath for RESTful URL
        function encodeFilePathForUrl(filePath) {
            // Convert to base64 URL-safe encoding
            // Use TextEncoder for proper UTF-8 encoding
            const encoder = new TextEncoder();
            const data = encoder.encode(filePath);
            // Convert Uint8Array to regular array and then to base64
            const base64 = btoa(String.fromCharCode(...data));
            return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        }
        
        // Signs out: the server revokes this session's token (POST /api/auth/logout), so a copy of it
        // stops working too. keepalive lets the request finish while the page goes to the login screen.
        function logout() {
            if (confirm('Are you sure you want to logout?')) {
                const token = localStorage.getItem('jwt_token');
                if (token && !isAutheliaAuthenticated()) {
                    apiFetch('/api/auth/logout', {
                        method: 'POST',
                        keepalive: true,
                        auth: false,
                        json: false,
                        headers: { 'Authorization': `Bearer ${token}` }
                    }).catch(error => console.warn('[AUTH] Sign-out request failed:', error));
                }
                redirectToLogin();
            }
        }
        
        // API helper functions for server-side preferences
        async function getPreferences() {
            try {
                return await apiFetch('/api/preferences');
            } catch (error) {
                console.error('Error getting preferences:', error);
                return {};
            }
        }
        
        async function setPreferences(prefs) {
            rememberLibraryQuery(prefs);
            try {
                await apiFetch('/api/preferences', { method: 'PUT', body: prefs });
            } catch (error) {
                console.error('Error setting preferences:', error);
            }
        }

        // The library's filter and sort preferences, kept on this device too so
        // the next start can request the library with them straight away
        // instead of waiting for /api/preferences (the server's copy still wins
        // when it arrives).
        const LIBRARY_QUERY_KEY = 'libraryQuery';
        const LIBRARY_QUERY_FIELDS = ['filterMode', 'sortMode', 'sortDirection'];

        function rememberedLibraryQuery() {
            try {
                const stored = JSON.parse(localStorage.getItem(LIBRARY_QUERY_KEY) || '{}');
                return stored && typeof stored === 'object' ? stored : {};
            } catch (_e) {
                return {};
            }
        }

        function rememberLibraryQuery(prefs) {
            if (!prefs || !LIBRARY_QUERY_FIELDS.some(field => typeof prefs[field] === 'string')) return;
            const query = rememberedLibraryQuery();
            for (const field of LIBRARY_QUERY_FIELDS) {
                if (typeof prefs[field] === 'string') query[field] = prefs[field];
            }
            try {
                localStorage.setItem(LIBRARY_QUERY_KEY, JSON.stringify(query));
            } catch (_e) { /* storage full or unavailable: the server's copy still applies */ }
        }
        
        function showMessage(message, type = 'info', options = {}) {
            // On the way to the login page, the requests still in flight fail
            // and their callers report it; the user just needs the login page.
            if ((type === 'error' || type === 'warning') && isRedirectingToLogin()) {
                return document.createElement('div');
            }
            // An open dialog covers #messageContainer, so a dialog that stays
            // open after an action passes its own live region as `container`.
            const container = options.container || document.getElementById('messageContainer');
            const messageEl = document.createElement('div');
            messageEl.className = `message ${type}`;
            // #messageContainer is a polite live region, which is right for
            // routine progress chatter but too easy to miss for a failure the
            // user has to act on. Errors get role="alert" so assistive tech
            // interrupts instead of queueing behind whatever is being read.
            if (type === 'error') {
                messageEl.setAttribute('role', 'alert');
            }
            // An error the user may have to report is only actionable if it can
            // be tied back to the server log, so a caller reporting a failed API
            // call passes the caught error as `reference` (or an explicit id) to
            // have the correlation id of that call appended.
            messageEl.textContent = options.reference
                ? withApiFailureReference(message, options.reference)
                : message;
            
            container.appendChild(messageEl);
            
            // Persistent messages (autoDismiss === false) stay until the caller
            // removes the returned element, e.g. to show progress for a
            // long-running operation. The element is always returned so callers
            // can update or dismiss it.
            if (options.autoDismiss !== false) {
                setTimeout(() => {
                    messageEl.remove();
                }, options.duration || 5000);
            }
            return messageEl;
        }
