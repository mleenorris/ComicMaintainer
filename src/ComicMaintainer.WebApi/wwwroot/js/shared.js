// Helpers shared by index.html (the library SPA) and reader.html.
//
// Classic (non-module) script loaded before each page's own code, so its
// top-level declarations are globals. Keep it free of page-specific state and
// of anything that runs at load time: it only declares pure helpers.

const HTML_ESCAPES = {
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
};

/**
 * Escape a value for interpolation into markup, in either text or
 * quoted-attribute position.
 *
 * Quotes must be escaped too: this is used inside attributes such as
 * aria-label="...", and titles really do contain double quotes (see the
 * example in escapeJs in /js/app/templating.js), which would otherwise
 * terminate the attribute and let the rest of the value be parsed as markup.
 */
function escapeHtml(text) {
    return String(text ?? '').replace(/[&<>"']/g, ch => HTML_ESCAPES[ch]);
}

/**
 * Whether the user signed in through Authelia (cookie-based auth) rather than
 * with a JWT. Authelia sessions ride on cookies sent via
 * `credentials: 'include'`, so no Authorization header is attached for them.
 */
function isAutheliaAuthenticated() {
    return localStorage.getItem('authelia_authenticated') === 'true';
}

/**
 * Fetch options that always send cookies and, for JWT sessions, attach the
 * bearer token. `extra` is merged in (its headers are preserved).
 */
function buildCredentialedFetchOptions(extra, bearerToken) {
    const opts = Object.assign({ credentials: 'include' }, extra || {});
    if (!isAutheliaAuthenticated() && bearerToken) {
        opts.headers = Object.assign({}, opts.headers || {}, {
            'Authorization': 'Bearer ' + bearerToken
        });
    }
    return opts;
}
