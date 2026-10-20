        // ComicMaintainer SPA — API plumbing: failure references, auth/JWT handling, fetch options, preferences, showMessage().
        //
        // Classic (non-module) script: its top-level declarations are globals
        // shared with the other app scripts and with inline onclick handlers.
        // Load order is fixed by index.html (see the comment above the
        // <script> tags there); this file must not run code at load time that
        // depends on a script loaded after it.

        // BASE_PATH and apiUrl are defined in an inline script in the main HTML file
        // (before this external JS is loaded) to support Flask template variable injection.
        // They are available globally when this script executes.
        
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
        // Rather than thread the id through the hundreds of individual fetch call
        // sites, the most recent failing API response is remembered here.
        // Decoration is opt-in per message — a caller passes
        // `{ reference: true }` to showMessage() — because only the caller knows
        // whether the failure it is reporting is the API call that produced the
        // id: a clipboard denial, a validation message or a JavaScript error that
        // merely happens to follow an API failure would otherwise be stamped with
        // an unrelated id and send an operator to the wrong log line.

        const API_FAILURE_REFERENCE_WINDOW_MS = 15000;
        let lastApiFailure = null;

        function recordApiFailure(response) {
            try {
                // 401/403 are routine — an expired session or a read-only user —
                // and are already explained by their own handling, so a support
                // reference would be noise.
                if (response.status === 401 || response.status === 403) return;
                const reference = response.headers.get('X-Correlation-Id');
                if (!reference) return;
                lastApiFailure = { reference, at: Date.now() };
            } catch {
                // Opaque (cross-origin) responses disallow header access; there
                // is nothing to record and nothing to report.
            }
        }

        /**
         * The reference of a *recent* API failure, or null. The time window
         * matters: a stale id pointing at an unrelated request is worse than no
         * id at all.
         */
        function recentApiFailureReference() {
            if (!lastApiFailure) return null;
            if (Date.now() - lastApiFailure.at > API_FAILURE_REFERENCE_WINDOW_MS) return null;
            return lastApiFailure.reference;
        }

        /**
         * Append a correlation reference to an error message. `reference` is
         * either an explicit id or `true`, meaning "the API call that just
         * failed" — callers only pass it when the message they are reporting
         * came from a failed API request.
         */
        function withApiFailureReference(message, reference) {
            const ref = reference === true ? recentApiFailureReference() : reference;
            if (!ref) return message;
            if (String(message).includes(ref)) return message;
            return `${message} (ref: ${ref})`;
        }

        (function instrumentApiFailures() {
            if (typeof window.fetch !== 'function') return;
            const nativeFetch = window.fetch.bind(window);
            window.fetch = function (input, init) {
                return nativeFetch(input, init).then(response => {
                    if (response && !response.ok) recordApiFailure(response);
                    return response;
                });
            };
        })();

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
        
        // Helper function to redirect to login and clear auth data
        function redirectToLogin() {
            localStorage.removeItem('jwt_token');
            localStorage.removeItem('username');
            localStorage.removeItem('authelia_authenticated');
            localStorage.removeItem('client_cache_session_id');
            clearCachedImages();
            window.location.href = '/login.html';
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
                const response = await fetch(apiUrl('/api/auth/status'), {
                    credentials: 'include' // Important for Authelia cookies
                });
                
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
        function getAuthHeaders() {
            const isAutheliaAuth = isAutheliaAuthenticated();
            
            // If authenticated via Authelia, don't need Authorization header
            // The cookies will be sent automatically with credentials: 'include'
            if (isAutheliaAuth) {
                console.log('[AUTH] Using Authelia authentication (cookies)');
                return {
                    'Content-Type': 'application/json',
                    'X-Client-Session': getOrCreateClientCacheSessionId()
                };
            }
            
            const token = localStorage.getItem('jwt_token');
            
            // Proactively check token expiry before making API calls
            if (token && isTokenExpired(token)) {
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
                console.log('[AUTH] Adding authorization header (token present)');
            } else {
                console.warn('[AUTH] No JWT token found in localStorage');
            }
            return headers;
        }
        
        // Helper function to handle auth errors
        function handleAuthError(response) {
            if (response.status === 401) {
                // Token expired or invalid, redirect to login
                console.log('Received 401 Unauthorized, redirecting to login');
                redirectToLogin();
                return true;
            }
            return false;
        }
        
        // Helper function to get fetch options with proper credentials
        function getFetchOptions(method = 'GET', body = null) {
            const options = {
                method: method,
                headers: getAuthHeaders(),
                credentials: 'include' // Always include credentials for Authelia support
            };
            
            if (body) {
                options.body = typeof body === 'string' ? body : JSON.stringify(body);
            }
            
            return options;
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
        
        // Add logout function
        function logout() {
            if (confirm('Are you sure you want to logout?')) {
                redirectToLogin();
            }
        }
        
        // API helper functions for server-side preferences
        async function getPreferences() {
            try {
                const response = await fetch(apiUrl('/api/preferences'), {
                    headers: getAuthHeaders()
                });
                if (handleAuthError(response)) return {};
                if (!response.ok) {
                    console.error('Failed to get preferences:', response.status);
                    return {};
                }
                return await response.json();
            } catch (error) {
                console.error('Error getting preferences:', error);
                return {};
            }
        }
        
        async function setPreferences(prefs) {
            try {
                const response = await fetch(apiUrl('/api/preferences'), {
                    method: 'PUT',
                    headers: {
                        ...getAuthHeaders(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(prefs)
                });
                if (handleAuthError(response)) return;
                if (!response.ok) {
                    console.error('Failed to set preferences:', response.status);
                }
            } catch (error) {
                console.error('Error setting preferences:', error);
            }
        }
        
        function showMessage(message, type = 'info', options = {}) {
            const container = document.getElementById('messageContainer');
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
            // call passes `reference: true` (or an explicit id) to have the
            // correlation id of that call appended.
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
