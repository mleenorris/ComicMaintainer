// Helpers shared by every page of the app.
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
 * The path the app is served under: '' at the domain root, '/comics' in a
 * subfolder. The server writes it into each page's <html> element (see
 * HtmlVersionInjectionMiddleware).
 */
const BASE_PATH = (() => {
    const configured = document.documentElement.dataset.basePath;
    return configured?.startsWith('/') ? configured : '';
})();

/**
 * The URL of a path of this app. Every URL the scripts build goes through
 * here, so that the app works under a subfolder.
 */
function apiUrl(path) {
    return BASE_PATH + (path.startsWith('/') ? path : '/' + path);
}

/**
 * Whether the user signed in through Authelia (cookie-based auth) rather than
 * with a JWT. Authelia sessions ride on cookies sent via
 * `credentials: 'include'`, so no Authorization header is attached for them.
 */
function isAutheliaAuthenticated() {
    return localStorage.getItem('authelia_authenticated') === 'true';
}
