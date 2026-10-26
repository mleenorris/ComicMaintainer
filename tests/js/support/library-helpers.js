'use strict';

// Shared setup for the library view tests (js/app/library.js and
// js/app/file-list.js): fixtures shaped like the FilesController responses
// (DTOs in src/ComicMaintainer.Core) and a boot helper that opens #/library
// with the library endpoints routed.

const { bootApp } = require('./app');
const { respond } = require('./dom');

// The auto series layout is 'list' while this matches, 'compact' otherwise.
const PORTRAIT_QUERY = '(max-width: 600px) and (orientation: portrait)';
// isMobileLibraryViewport() (shell.js).
const MOBILE_QUERY = '(max-width: 768px)';

const EMPTY_COUNTS = { total: 0, processed: 0, unprocessed: 0, duplicates: 0, combinableFolders: 0 };

/** SeriesSummaryDto, as GET /api/files/series lists it. */
function makeSeries(n, overrides = {}) {
    const title = `Series ${String(n).padStart(3, '0')}`;
    return {
        id: `series-${n}`,
        title,
        canonical_title: title,
        aliases: [],
        metadata_source: null,
        issue_count: 3,
        total_size: 3 * 1024 * 1024,
        latest_modified: 1700000000,
        cover_file_path: `${title}/${title} #001.cbz`,
        has_external_image: false,
        external_image_url: null,
        lookup_status: null,
        last_lookup_utc: null,
        synopsis: null,
        ...overrides
    };
}

function makeSeriesList(count, start = 1) {
    return Array.from({ length: count }, (_, index) => makeSeries(start + index));
}

/**
 * Handler for GET /api/files/series that pages `all` by the request's
 * offset/limit, like the server does. `extra` overrides response fields.
 */
function seriesPages(all, extra = {}) {
    return call => {
        const offset = Number(call.query.offset || 0);
        const limit = Number(call.query.limit || 60);
        return {
            series: all.slice(offset, offset + limit),
            page: Math.floor(offset / limit) + 1,
            total_pages: Math.max(1, Math.ceil(all.length / limit)),
            total_series: all.length,
            offset,
            limit,
            unmarked_count: 0,
            ...extra
        };
    };
}

/** FolderSummaryDto, as GET /api/files/folders lists it. */
function makeFolder(path, overrides = {}) {
    return { path, file_count: 2, total_size: 2048, last_modified: 1700000000, unmarked_count: 0, duplicate_count: 0, ...overrides };
}

/** FileDto, as GET /api/files/folders/{b64}/files lists it. */
function makeFile(relativePath, overrides = {}) {
    return {
        relative_path: relativePath,
        name: relativePath.split('/').pop(),
        size: 1024,
        modified: Math.floor(Date.now() / 1000) - 3 * 3600,
        processed: false,
        renamed: false,
        normalized: false,
        duplicate: false,
        read: false,
        ...overrides
    };
}

/** URL-safe base64 without padding, as the server's DecodeBase64UrlSafe expects. */
function encodeFolder(path) {
    return Buffer.from(path, 'utf8').toString('base64url');
}

/**
 * Route matcher for GET /api/files/folders/{b64}/files; the handler gets
 * `call.folder` decoded. (The library root, path '', encodes to an empty
 * segment: /api/files/folders//files.)
 */
function folderFilesMatcher(call) {
    const match = /^\/api\/files\/folders\/([^/]*)\/files$/.exec(call.path);
    if (!match || call.method !== 'GET') return false;
    call.folder = Buffer.from(match[1], 'base64url').toString('utf8');
    return true;
}

/** Handler for GET /api/files/folders that pages `all` by offset/limit like the server. */
function folderPages(all, extra = {}) {
    return call => {
        const offset = Number(call.query.offset || 0);
        const limit = Number(call.query.limit || 100);
        return {
            folders: all.slice(offset, offset + limit),
            offset,
            limit,
            total_folders: all.length,
            unmarked_count: 0,
            ...extra
        };
    };
}

/**
 * Boot index.html at #/library (or `hash`) with the library endpoints routed:
 * preferences (GET answers `prefs`, PUT echoes), library counts and the
 * paged series list built from `series`. `routes` add to / override these.
 */
async function bootLibrary({ series = [], prefs = {}, routes = {}, ...options } = {}) {
    return bootApp({
        hash: '#/library',
        ...options,
        routes: {
            'GET /api/preferences': prefs,
            'PUT /api/preferences': call => call.body,
            'GET /api/files/counts': EMPTY_COUNTS,
            'GET /api/files/series': seriesPages(series),
            ...routes
        }
    });
}

/**
 * Boot the library and switch it to the legacy Files (folder) view. No control
 * selects that view any more (preferences and the view buttons coerce it to
 * 'series'), so the mode is set directly before loading the folder list the
 * way loadActiveLibraryView() does. `folders` are served paged; `files` maps a
 * folder path to the FileDto list GET /api/files/folders/{b64}/files returns.
 */
async function bootFilesView({ folders = [], files = {}, routes = {}, ...options } = {}) {
    const page = await bootLibrary({ ...options, routes: { 'GET /api/files/folders': folderPages(folders), ...routes } });
    page.route(folderFilesMatcher, call => ({ path: call.folder, files: files[call.folder] || [] }));
    page.run("libraryViewMode = 'files'");
    await page.run('loadActiveLibraryView()');
    await page.settle();
    return page;
}

/** Requests to `path`, in order. */
function callsTo(page, path, method = 'GET') {
    return page.fetchCalls.filter(call => call.path === path && call.method === method);
}

const seriesCalls = page => callsTo(page, '/api/files/series');
const folderCalls = page => callsTo(page, '/api/files/folders');
/** GET /api/files/folders/{b64}/files requests, each with `folder` decoded. */
const folderFileCalls = page => page.fetchCalls.filter(folderFilesMatcher);

/** Bodies of the PUT /api/preferences requests, in order. */
const savedPreferences = page => callsTo(page, '/api/preferences', 'PUT').map(call => call.body);

/** The toast messages currently shown, as { type, text }. */
function messages(page) {
    return page.$$('#messageContainer .message').map(element => ({
        type: element.className.replace(/^message\s*/, ''),
        text: element.textContent
    }));
}

/** library.js's lazy cover-image observer (created first, at script load). */
function imageObserver(page) {
    return page.intersectionObservers.find(observer => observer.options && observer.options.rootMargin === '400px 0px');
}

/** The current infinite-scroll observer: the newest one rooted at #fileList. */
function scrollObserver(page) {
    const fileList = page.$('#fileList');
    return [...page.intersectionObservers].reverse().find(observer => observer.options && observer.options.root === fileList);
}

/** Series titles as rendered in any layout. */
function renderedTitles(page) {
    return page.$$('#fileList .series-title, #fileList .series-list-title').map(element => element.textContent);
}

/** A successful protected-image response. */
const imageResponse = () => respond(200, 'image-bytes', { 'Content-Type': 'image/jpeg' });

/**
 * Dispatch a bubbling, cancelable keyboard (key*) or mouse event of `type`
 * on `target` (an element or a selector); returns the event.
 */
function dispatch(page, target, type, init = {}) {
    const element = typeof target === 'string' ? page.$(target) : target;
    if (!element) throw new Error(`No element for ${target}`);
    const EventType = type.startsWith('key') ? page.window.KeyboardEvent : page.window.MouseEvent;
    const event = new EventType(type, { bubbles: true, cancelable: true, ...init });
    element.dispatchEvent(event);
    return event;
}

module.exports = {
    PORTRAIT_QUERY,
    MOBILE_QUERY,
    EMPTY_COUNTS,
    makeSeries,
    makeSeriesList,
    seriesPages,
    makeFolder,
    makeFile,
    encodeFolder,
    folderFilesMatcher,
    folderPages,
    bootLibrary,
    bootFilesView,
    callsTo,
    seriesCalls,
    folderCalls,
    folderFileCalls,
    savedPreferences,
    messages,
    imageObserver,
    scrollObserver,
    renderedTitles,
    imageResponse,
    dispatch
};
