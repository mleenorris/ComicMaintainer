'use strict';

// Shared fixtures for the series detail / series metadata tests
// (tests/js/series-*.test.js): API payloads shaped like the C# DTOs, and a
// bootSeries() that signs in straight onto a series page.

const { bootApp } = require('./app');

// One series card of GET /api/files/series (SeriesSummaryDto).
function seriesCard(overrides = {}) {
    return {
        id: 'saga',
        title: 'Saga',
        canonical_title: 'Saga',
        aliases: [],
        metadata_source: null,
        issue_count: 3,
        total_size: 3 * 1048576,
        latest_modified: 1700000000,
        cover_file_path: '/comics/Saga/Saga 001.cbz',
        has_external_image: false,
        external_image_url: null,
        lookup_status: null,
        last_lookup_utc: null,
        synopsis: null,
        ...overrides
    };
}

// One issue of GET /api/files/series/{id}/issues (SeriesIssueDto).
function issue(number, overrides = {}) {
    const text = String(number);
    const label = /^\d+$/.test(text) ? text.padStart(3, '0') : text;
    return {
        file_path: `/comics/Saga/Saga ${label}.cbz`,
        file_name: `Saga ${label}.cbz`,
        title: `Saga #${text}`,
        issue: text,
        volume: null,
        publisher: 'Image',
        year: 2012,
        size: 1048576,
        modified: 1700000000,
        processed: true,
        renamed: true,
        normalized: true,
        duplicate: false,
        read: false,
        ...overrides
    };
}

// GET /api/files/series/{id}/issues as FilesController.GetSeriesIssues returns
// it for per_page=-1 (everything on one page).
function issuesBody(series, issues, overrides = {}) {
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
        total_size: issues.reduce((sum, item) => sum + item.size, 0),
        issues,
        page: 1,
        per_page: issues.length,
        total_pages: 1,
        ...overrides
    };
}

// GET /api/files/series (offset mode) for the given cards.
function libraryBody(series) {
    return {
        series,
        page: 1,
        total_pages: 1,
        total_series: series.length,
        offset: 0,
        limit: 60,
        unmarked_count: 0
    };
}

// GET /api/metadata/series/{title} (SeriesMetadataCacheRecord, snake_case).
function metadataRecord(overrides = {}) {
    return {
        series_id: 'saga',
        canonical_title: 'Saga',
        aliases: [],
        user_aliases: [],
        series_name: null,
        resolved_series_name: 'Saga',
        source: null,
        last_lookup_utc: null,
        lookup_status: null,
        preferred_language: null,
        localized_titles: [],
        has_image: false,
        is_user_image: false,
        image_downloaded_utc: null,
        synopsis: null,
        ...overrides
    };
}

const issuesPath = id => `/api/files/series/${encodeURIComponent(id)}/issues`;

// Signs in onto #/series/<id>. `library` is the series list the boot loads
// (default: just `series`); `issues` answers the series' issue request;
// `suwayomi` is the GET /api/suwayomi/status answer. `routes` win over these.
async function bootSeries({
    series = seriesCard(),
    library,
    issues = [issue(1), issue(2), issue(3)],
    suwayomi = { enabled: false, configured: false, connected: false },
    routes = {},
    hash,
    ...options
} = {}) {
    return bootApp({
        hash: hash || `#/series/${encodeURIComponent(series.id)}`,
        routes: {
            'GET /api/files/series': libraryBody(library || [series]),
            [`GET ${issuesPath(series.id)}`]: issuesBody(series, issues),
            'GET /api/suwayomi/status': suwayomi,
            ...routes
        },
        ...options
    });
}

// The texts and kinds of the toasts currently in #messageContainer.
function messages(page) {
    return page.$$('#messageContainer .message').map(el => ({
        text: el.textContent,
        type: [...el.classList].find(name => name !== 'message')
    }));
}

const lastMessage = page => messages(page).at(-1);

// Fetch calls made to `path` (optionally only with `method`).
function callsTo(page, path, method) {
    return page.fetchCalls.filter(call => call.path === path && (!method || call.method === method));
}

// An element's text with whitespace runs collapsed.
const text = element => (element ? element.textContent.replace(/\s+/g, ' ').trim() : null);

// The issue cards of the open series detail, in display order.
const issueCards = page => page.$$('#seriesDetailPanel .series-issues-grid > .series-issue-card');

// Clicks the open series page's Actions menu item labelled `label`.
function seriesAction(page, label) {
    const item = page.$$('#seriesDetailPanel .series-actions-dropdown .dropdown-item').find(el => text(el) === label);
    if (!item) throw new Error(`No series action "${label}"`);
    return page.click(item);
}

// Replaces the page's global function `name` with a wrapper that records the
// arguments of every call and then runs the original. Classic-script
// functions are writable globals, so every caller goes through the wrapper.
function spyOn(page, name) {
    const original = page.window[name];
    const calls = [];
    page.window[name] = function (...args) {
        calls.push(args);
        return original.apply(this, args);
    };
    return calls;
}

// Library reloads that bypass the server cache (loadSeriesLibrary with refresh).
const libraryRefreshes = page =>
    page.fetchCalls.filter(call => call.path === '/api/files/series' && call.query.refresh === 'true').length;

// A route handler that answers with each of `responses` in turn and then
// keeps repeating the last one.
function inTurn(...responses) {
    let index = 0;
    return () => responses[Math.min(index++, responses.length - 1)];
}

// Opens Manage Names from the series page's Actions menu, answering the
// metadata request for `title` with `record` (any route handler).
async function openManageNames(page, record = metadataRecord(), title = 'Saga') {
    page.route(`GET /api/metadata/series/${encodeURIComponent(title)}`, record);
    seriesAction(page, 'Manage Names');
    await page.settle();
    return page.$('#manageSeriesNamesModal');
}

// The option labels of a <select>, in order.
const optionTexts = select => [...select.options].map(option => option.textContent);

// The button labelled `label` inside the element matching `scope`.
function buttonIn(page, scope, label) {
    const button = page.$$(`${scope} button`).find(el => text(el) === label);
    if (!button) throw new Error(`No "${label}" button in ${scope}`);
    return button;
}

// From now on the page's confirm() answers `answer` (dialogs are still recorded
// in page.dialogs).
function answerConfirm(page, answer) {
    const record = page.window.confirm;
    page.window.confirm = message => {
        record(message);
        return answer;
    };
}

// The URL the reader is opened with for `filePath` (see readComic in main.js).
const readerUrl = filePath => `http://localhost/reader.html?file=${encodeURIComponent(filePath)}`;

module.exports = {
    seriesCard,
    issue,
    issuesBody,
    libraryBody,
    metadataRecord,
    issuesPath,
    bootSeries,
    messages,
    lastMessage,
    callsTo,
    text,
    issueCards,
    readerUrl,
    seriesAction,
    spyOn,
    libraryRefreshes,
    inTurn,
    openManageNames,
    optionTexts,
    buttonIn,
    answerConfirm
};
