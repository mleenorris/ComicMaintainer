'use strict';

// Shared setup for the settings page tests (tests/js/settings-*.test.js):
// a GET /api/settings fixture shaped like SettingsController.GetAllSettings,
// a boot helper for #/settings and readers for the toasts and requests the
// page produces.

const { bootApp } = require('./app');

const MB = 1048576;

// GET /api/settings as SettingsController.GetAllSettings returns it. Secrets
// (the ComicVine key, SMTP/Suwayomi passwords, the GitHub token) are only
// reported as `*_set`.
function settingsResponse(overrides = {}) {
    return {
        filename_format: '{series} v{volume} #{issue}',
        issue_number_padding: 3,
        watcher_enable_rename: true,
        watcher_enable_normalize: false,
        log_max_bytes: 10 * MB,
        database_cleanup_interval_hours: 12,
        enable_external_series_metadata: true,
        comicvine_api_key_set: true,
        comicvine_base_url: 'https://comicvine.example/api',
        enable_mangadex_metadata: true,
        mangadex_base_url: 'https://mangadex.example',
        enable_anilist_metadata: false,
        anilist_base_url: 'https://anilist.example/graphql',
        enable_suwayomi: true,
        suwayomi_base_url: 'http://suwayomi.local:4567',
        suwayomi_username: 'reader',
        suwayomi_password_set: true,
        default_library_view: 'series',
        allow_registration: true,
        default_preferred_language: 'ja',
        write_cover_to_first_archive: true,
        write_cover_to_series_folder: false,
        download_external_series_images: true,
        series_image_max_bytes: 5 * MB,
        series_image_max_download_bytes: 20 * MB,
        series_image_max_dimension: 1600,
        smtp_host: 'smtp.example.com',
        smtp_port: 2525,
        smtp_username: 'mailer',
        smtp_password_set: true,
        smtp_use_ssl: false,
        smtp_allow_insecure: true,
        email_from_address: 'comics@example.com',
        email_from_name: 'Comic Maintainer',
        email_max_attachment_mb: 30,
        error_reporting_enabled: true,
        error_reporting_github_owner: 'mleenorris',
        error_reporting_github_repo: 'ComicMaintainer',
        error_reporting_github_token_set: true,
        error_reporting_assignee: 'octocat',
        error_reporting_max_issues_per_day: 5,
        error_reporting_dedupe_window_hours: 48,
        error_reporting_comment_on_recurrence: false,
        ...overrides
    };
}

// Requests the settings page makes besides the boot ones: the settings
// themselves, the backups list, the email status summary (loadSettings) and
// the Suwayomi probe (after a save). The (empty) series list is routed only so
// the boot's background library load does not toast a 404.
function settingsRoutes(settings = {}) {
    return {
        'GET /api/files/series': { series: [], total_series: 0 },
        'GET /api/settings': settingsResponse(settings),
        'GET /api/backups': { backups: [], restore_pending: false },
        'GET /api/email/status': { configured: true, from_address: 'comics@example.com', max_attachment_mb: 30 },
        'GET /api/suwayomi/status': { enabled: false, connected: false }
    };
}

// Boots the signed-in SPA on #/settings. `settings` overrides fields of the
// GET /api/settings fixture; `routes` override any route.
async function bootSettings({ settings = {}, routes = {}, ...options } = {}) {
    return bootApp({ hash: '#/settings', ...options, routes: { ...settingsRoutes(settings), ...routes } });
}

// Toasts shown through showMessage(), oldest first, as { type, text }.
function messages(page) {
    return page.$$('#messageContainer .message').map(element => ({
        type: [...element.classList].filter(name => name !== 'message').join(' '),
        text: element.textContent
    }));
}

function lastMessage(page) {
    return messages(page).at(-1);
}

function clearMessages(page) {
    page.$('#messageContainer').replaceChildren();
}

// Recorded fetch calls matching "METHOD /path" (query ignored), optionally only
// those made after `since` (a page.fetchCalls.length taken earlier).
function callsTo(page, matcher, since = 0) {
    const [method, path] = matcher.split(' ', 2);
    return page.fetchCalls.slice(since).filter(call => call.method === method && call.path === path);
}

// "METHOD /path?query" of every call made after `since`, for asserting order.
function requestLog(page, since = 0) {
    return page.fetchCalls.slice(since).map(call => `${call.method} ${call.url}`);
}

// Answer every later confirm() with `answer`, still recording each prompt in page.dialogs.
function answerConfirm(page, answer) {
    page.window.confirm = message => {
        page.dialogs.push({ kind: 'confirm', message: String(message) });
        return answer;
    };
}

// Click an element rendered through renderHtml(). That markup is parsed in an
// inert <template> document, where jsdom (unlike browsers) never compiles inline
// on* handlers, so the onclick attribute is re-set once the element is in the
// page, as a browser would compile it on first use.
function clickRendered(page, target) {
    const element = typeof target === 'string' ? page.$(target) : target;
    if (!element) throw new Error(`No element to click: ${target}`);
    const handler = element.getAttribute('onclick');
    if (handler !== null && element.onclick === null) element.setAttribute('onclick', handler);
    element.click();
    return element;
}

module.exports = {
    MB,
    settingsResponse,
    settingsRoutes,
    bootSettings,
    messages,
    lastMessage,
    clearMessages,
    callsTo,
    requestLog,
    answerConfirm,
    clickRendered
};
