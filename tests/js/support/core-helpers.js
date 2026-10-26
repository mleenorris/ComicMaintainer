'use strict';

// Shared helpers for the app-core tests (api, events, shell, modals, main,
// router, pwa, error-reporting): realistic boot responses, a layout shim for
// the parts of the page jsdom cannot lay out, and small query helpers.

const { bootApp, validToken } = require('./app');

// GET /api/files/counts (FilesController.Browse GetCounts).
function fileCounts(overrides = {}) {
    return { total: 120, processed: 100, unprocessed: 20, duplicates: 3, combinableFolders: 2, ...overrides };
}

// GET /api/preferences (PreferencesController.BuildResponse).
function preferences(overrides = {}) {
    return {
        theme: null,
        perPage: 100,
        filenameFormat: '{series} #{issue}',
        issueNumberPadding: 3,
        watcherEnabled: true,
        readingMode: 'manga',
        libraryViewMode: 'series',
        filterMode: 'all',
        sortMode: 'name',
        ...overrides
    };
}

// A series summary card (SeriesSummaryDto / OverviewSeriesCard).
function seriesCard(overrides = {}) {
    return {
        id: 'series-1',
        title: 'Saga',
        canonical_title: 'Saga',
        aliases: [],
        metadata_source: null,
        issue_count: 3,
        total_size: 3 * 1024 * 1024,
        latest_modified: 1700000000,
        cover_file_path: 'Saga/Saga 001.cbz',
        has_external_image: false,
        external_image_url: null,
        lookup_status: null,
        ...overrides
    };
}

// One issue of a series (SeriesIssueDto).
function seriesIssue(overrides = {}) {
    return {
        file_path: 'Saga/Saga 001.cbz',
        file_name: 'Saga 001.cbz',
        title: 'Chapter One',
        issue: '1',
        volume: '2012',
        publisher: 'Image',
        year: 2012,
        size: 1024 * 1024,
        modified: 1700000000,
        processed: true,
        renamed: true,
        normalized: true,
        duplicate: false,
        read: false,
        ...overrides
    };
}

// GET /api/files/series/{id}/issues (FilesController.Browse GetSeriesIssues).
function seriesIssues(series, issues = [seriesIssue()]) {
    return {
        id: series.id,
        title: series.title,
        canonical_title: series.canonical_title,
        aliases: series.aliases,
        metadata_source: series.metadata_source,
        cover_file_path: series.cover_file_path,
        has_external_image: series.has_external_image,
        external_image_url: series.external_image_url,
        issue_count: issues.length,
        total_size: series.total_size,
        issues,
        page: 1,
        per_page: Math.max(issues.length, 1),
        total_pages: 1
    };
}

// GET /api/files/series (offset mode, FilesController.Browse GetSeries).
function seriesPage(series = [], overrides = {}) {
    return { series, page: 1, total_pages: 1, total_series: series.length, offset: 0, limit: 60, unmarked_count: 0, ...overrides };
}

// GET /api/overview (OverviewResult).
function overview(overrides = {}) {
    return { continue_reading: [], series_updates: [], newly_added_series: [], ...overrides };
}

/** Every request the boot makes, answered with realistic, quiet responses. */
function coreRoutes(overrides = {}) {
    return {
        'GET /api/version': { version: '9.8.7', platform: 'linux' },
        'GET /api/preferences': preferences(),
        'GET /api/active-job': { active: false },
        'GET /api/files/counts': fileCounts(),
        'GET /api/files/series': seriesPage(),
        'GET /api/watcher/status': { running: true, enabled: true },
        'GET /api/overview': overview(),
        'GET /api/suwayomi/status': { enabled: false, connected: false },
        ...overrides
    };
}

/**
 * index.html's stylesheet (css/main.css) is not loaded in jsdom, so every
 * `.modal` would compute as display:block — i.e. all 20 dialogs "open" — and
 * jsdom reports `offsetParent` as null for every element. Mirror the two
 * main.css rules that decide whether a dialog is shown, and derive
 * offsetParent from the hidden attribute / computed display of the element and
 * its ancestors (null when not rendered, as in a browser).
 */
function installLayout(window) {
    const style = window.document.createElement('style');
    style.textContent = '.modal { display: none; } .modal.active { display: flex; }';
    window.document.head.appendChild(style);
    Object.defineProperty(window.HTMLElement.prototype, 'offsetParent', {
        configurable: true,
        get() {
            for (let element = this; element; element = element.parentElement) {
                if (element.hidden) return null;
                if (window.getComputedStyle(element).display === 'none') return null;
            }
            return this.parentElement || null;
        }
    });
}

/**
 * bootApp() with every boot request routed (see coreRoutes) and, unless
 * `layout: false`, the layout shim above. `beforeScripts` still runs.
 */
function boot({ routes = {}, layout = true, beforeScripts, ...options } = {}) {
    return bootApp({
        ...options,
        routes: coreRoutes(routes),
        beforeScripts(window, page) {
            if (layout) installLayout(window);
            if (beforeScripts) beforeScripts(window, page);
        }
    });
}

/** Recorded fetch calls for METHOD + path (query ignored). */
function requests(page, method, path) {
    return page.fetchCalls.filter(call => call.method === method && call.path === path);
}

/** The messages showMessage() has put on screen: [{ type, text, role }]. */
function messages(page) {
    return page.$$('#messageContainer .message').map(element => ({
        type: element.className.replace(/^message\s*/, ''),
        text: element.textContent,
        role: element.getAttribute('role')
    }));
}

/** Console output of one kind (log/warn/error/...) joined into strings. */
function consoleLines(page, type) {
    return page.consoleMessages
        .filter(message => !type || message.type === type)
        .map(message => message.args.map(arg => (arg && arg.message !== undefined ? arg.message : String(arg))).join(' '));
}

/** Deliver a server event ({ type, data }, as EventBroadcasterService sends it) on the newest stream. */
function serverEvent(page, type, data = {}) {
    const source = page.eventSources.at(-1);
    if (!source) throw new Error('No EventSource was opened');
    source.emit('message', { type, data });
}

/** Dispatch a keydown on `target` (default: the focused element or document). */
function press(page, key, init = {}, target) {
    const element = target || page.document.activeElement || page.document;
    const event = new page.window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
    element.dispatchEvent(event);
    return event;
}

function wentToLogin(page) {
    return page.navigations.some(navigation => navigation.url === 'http://localhost/login.html');
}

module.exports = {
    boot,
    coreRoutes,
    consoleLines,
    fileCounts,
    installLayout,
    messages,
    overview,
    preferences,
    press,
    requests,
    serverEvent,
    seriesCard,
    seriesIssue,
    seriesIssues,
    seriesPage,
    validToken,
    wentToLogin
};
