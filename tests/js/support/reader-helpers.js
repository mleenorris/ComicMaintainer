'use strict';

// Shared setup for the comic reader tests (reader.html, which runs
// js/shared.js + js/pages/reader.js).
//
// Routes answer the way ComicReaderController, AuthController and
// EmailController do. A "series" is an ordered { filePath: pageCount } map:
// GET /api/ComicReader/info and /adjacent are derived from it, so a test only
// lists the issues it reads through.

const { loadPage, respond } = require('./dom');
const { validToken, currentUser } = require('./app');

const FILE = '/comics/Series/Issue 001.cbz';
const NEXT_FILE = '/comics/Series/Issue 002.cbz';
const THIRD_FILE = '/comics/Series/Issue 003.cbz';
const PREV_FILE = '/comics/Series/Issue 000.cbz';

const baseName = filePath => filePath.split('/').pop();

function readerUrl(file = FILE, hash = '') {
    return `http://localhost/reader.html${file === null ? '' : `?file=${encodeURIComponent(file)}`}${hash}`;
}

/** GET /api/ComicReader/page: the page image bytes (named after the page, for debugging). */
function pageImage(call) {
    return new Response(`${call.query.filePath}#${call.query.page}`, { headers: { 'Content-Type': 'image/jpeg' } });
}

/** GET /api/ComicReader/info body (ComicInfoResponse). */
function comicInfo(filePath, pageCount) {
    return { fileName: baseName(filePath), filePath, pageCount };
}

function adjacentInfo(filePath) {
    return filePath
        ? { hasAdjacent: true, filePath, fileName: baseName(filePath) }
        : { hasAdjacent: false, filePath: null, fileName: null };
}

// `progress` is the page the server holds for every comic until the reader
// saves another one (POSTs are remembered per file, like ReadingProgressService).
function readerRoutes({ series = { [FILE]: 10 }, progress = 1, user = {} } = {}) {
    const order = Object.keys(series);
    const saved = new Map();
    return {
        'GET /api/auth/user': currentUser(user),
        'GET /api/ComicReader/info': call => (call.query.filePath in series
            ? comicInfo(call.query.filePath, series[call.query.filePath])
            : respond(404, { error: 'File not found' })),
        'GET /api/ComicReader/progress': call => ({ currentPage: saved.get(call.query.filePath) ?? progress }),
        'POST /api/ComicReader/progress': call => {
            saved.set(call.query.filePath, Number(call.query.page));
            return 200;
        },
        'GET /api/ComicReader/page': call => (Number(call.query.page) <= (series[call.query.filePath] || 0)
            ? pageImage(call)
            : respond(404, { error: 'Page not found' })),
        'GET /api/ComicReader/adjacent': call => {
            const index = order.indexOf(call.query.filePath);
            if (index < 0) return respond(404, { error: 'Current file not found in library' });
            return adjacentInfo(order[call.query.direction === 'prev' ? index - 1 : index + 1]);
        },
        'POST /api/ComicReader/mark-read': 200
    };
}

/**
 * Load reader.html for `file` without waiting for anything. `token: null`
 * signs out; `series`, `progress` and `user` shape the default routes and
 * `routes` adds to / overrides them. Other options go to loadPage().
 */
function loadReader({ file = FILE, hash = '', token = validToken(), series, progress, user, routes = {}, localStorage = {}, ...options } = {}) {
    return loadPage({
        html: 'reader.html',
        url: readerUrl(file, hash),
        ...options,
        localStorage: { ...(token ? { jwt_token: token } : {}), ...localStorage },
        routes: { ...readerRoutes({ series, progress, user }), ...routes }
    });
}

/** initReader() has run to completion (its last step arms the chrome auto-hide timer). */
const readerReady = page => page.run('chromeIdleTimer') !== null;

/**
 * jsdom never loads images: report every page image the reader is waiting on
 * (waitForImagesToLoad() assigns its onload) as loaded, once.
 */
function loadWaitingImages(page) {
    const fired = page.firedImageLoads || (page.firedImageLoads = new WeakSet());
    for (const img of page.$$('img[data-page]')) {
        if (typeof img.onload === 'function' && !fired.has(img)) {
            fired.add(img);
            img.dispatchEvent(new page.window.Event('load'));
        }
    }
}

/** Poll `predicate`, letting waiting page images load in between. */
function waitWithImages(page, predicate, options) {
    return page.waitFor(() => {
        loadWaitingImages(page);
        return predicate();
    }, options);
}

/** Load the reader (manga mode unless localStorage says otherwise) and wait for it to finish starting. */
async function openReader(options = {}) {
    const page = loadReader(options);
    await waitWithImages(page, () => readerReady(page));
    await page.settle();
    return page;
}

/** openReader() in webcomic (continuous scroll) mode. */
function openWebcomicReader(options = {}) {
    return openReader({ ...options, localStorage: { readingMode: 'webcomic', ...(options.localStorage || {}) } });
}

/** Press a key with focus on the page body (the reader listens on document). */
function press(page, key, init = {}) {
    const event = new page.window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
    (page.document.activeElement || page.document.body).dispatchEvent(event);
    return event;
}

/** Toasts shown so far, oldest first. */
function toasts(page) {
    return page.$$('#toastContainer .toast').map(toast => ({
        message: toast.textContent,
        type: [...toast.classList].find(name => name !== 'toast' && name !== 'show')
    }));
}

const isCall = (call, method, path) => call.method === method && call.path === path;

/** GET /api/ComicReader/page requests as { file, page }. */
function pageRequests(page, file) {
    return page.fetchCalls
        .filter(call => isCall(call, 'GET', '/api/ComicReader/page') && (file === undefined || call.query.filePath === file))
        .map(call => ({ file: call.query.filePath, page: Number(call.query.page) }));
}

/** POST /api/ComicReader/progress requests as { file, page }. */
function progressPosts(page) {
    return page.fetchCalls
        .filter(call => isCall(call, 'POST', '/api/ComicReader/progress'))
        .map(call => ({ file: call.query.filePath, page: Number(call.query.page) }));
}

/** Files POSTed to /api/ComicReader/mark-read, in order. */
function markReadPosts(page) {
    return page.fetchCalls
        .filter(call => isCall(call, 'POST', '/api/ComicReader/mark-read'))
        .map(call => call.query.filePath);
}

/** GET /api/ComicReader/adjacent requests as { file, direction }. */
function adjacentRequests(page) {
    return page.fetchCalls
        .filter(call => isCall(call, 'GET', '/api/ComicReader/adjacent'))
        .map(call => ({ file: call.query.filePath, direction: call.query.direction }));
}

/** The manga-mode page image currently shown. */
const shownPage = page => page.$('#readerContent img.page-image');

/** Wait until manga mode shows `alt` (e.g. 'Page 4'). */
function waitForShownPage(page, alt) {
    return page.waitFor(() => shownPage(page)?.alt === alt);
}

/** Webcomic-mode page images in stream order as 'file#page'. */
function streamPages(page) {
    return page.$$('#webcomicContainer img[data-page]').map(img => `${img.dataset.comic}#${img.dataset.page}`);
}

/** The webcomic-mode image of page `pageNum` of `file`. */
function streamImage(page, file, pageNum) {
    return page.$$('#webcomicContainer img[data-page]')
        .find(img => img.dataset.comic === file && img.dataset.page === String(pageNum));
}

/** The IntersectionObserver tracking webcomic pages (the newest one). */
const pageObserver = page => page.intersectionObservers.at(-1);

/**
 * Give an element a layout: jsdom reports 0 for scrollHeight/clientHeight and
 * zero-sized client rects.
 */
function setLayout(element, { scrollHeight, clientHeight, scrollTop, rect } = {}) {
    if (scrollHeight !== undefined) Object.defineProperty(element, 'scrollHeight', { configurable: true, get: () => (typeof scrollHeight === 'function' ? scrollHeight() : scrollHeight) });
    if (clientHeight !== undefined) Object.defineProperty(element, 'clientHeight', { configurable: true, value: clientHeight });
    if (scrollTop !== undefined) element.scrollTop = scrollTop;
    if (rect !== undefined) element.getBoundingClientRect = () => ({ top: rect.top, bottom: rect.bottom, left: 0, right: 0, width: 0, height: rect.bottom - rect.top });
}

/**
 * beforeScripts hook giving #readerContent a scrollable height, so the reader
 * is not permanently "at the bottom" of the stream (jsdom reports 0).
 */
function contentLayout(scrollHeight, clientHeight = 800) {
    return window => setLayout(window.document.getElementById('readerContent'), { scrollHeight, clientHeight });
}

/** Scroll the reader content to `scrollTop` and fire its scroll event. */
function scrollContent(page, scrollTop) {
    const content = page.$('#readerContent');
    content.scrollTop = scrollTop;
    content.dispatchEvent(new page.window.Event('scroll'));
}

/**
 * MessageChannel stand-in (jsdom has none): a message posted on one port is
 * delivered asynchronously to the other port's onmessage, like a real channel.
 */
class FakeMessageChannel {
    constructor() {
        const deliver = target => data => {
            Promise.resolve().then(() => {
                if (typeof target().onmessage === 'function') target().onmessage({ data });
            });
        };
        this.port1 = { onmessage: null, postMessage: deliver(() => this.port2) };
        this.port2 = { onmessage: null, postMessage: deliver(() => this.port1) };
    }
}

/**
 * A controlling service worker that records postMessage calls and answers
 * LIST_OFFLINE_COMICS / REMOVE_OFFLINE_COMIC on the transferred port the way
 * sw.js replyToMessage() does (`offline: [filePaths]` are the saved comics;
 * `reply: false` never answers, as a busy or dead worker would).
 */
function serviceWorkerController({ offline = [], reply = true } = {}) {
    const saved = new Set(offline);
    const controller = {
        messages: [],
        postMessage(message, transfer = []) {
            // postMessage clones its message; a plain copy also compares
            // cleanly against test literals (the page's objects come from
            // another realm).
            controller.messages.push(JSON.parse(JSON.stringify(message)));
            const port = transfer[0];
            if (!reply || !port) return;
            if (message.type === 'LIST_OFFLINE_COMICS') {
                port.postMessage({ type: 'OFFLINE_COMICS', comics: [...saved].map(filePath => ({ filePath, pageCount: 1 })) });
            } else if (message.type === 'REMOVE_OFFLINE_COMIC') {
                saved.delete(message.filePath);
                port.postMessage({ type: 'OFFLINE_COMIC_REMOVED', filePath: message.filePath });
            }
        }
    };
    return controller;
}

/** beforeScripts hook installing `controller` as navigator.serviceWorker.controller. */
function withController(controller) {
    return window => {
        window.MessageChannel = FakeMessageChannel;
        window.navigator.serviceWorker.controller = controller;
    };
}

module.exports = {
    FILE,
    NEXT_FILE,
    THIRD_FILE,
    PREV_FILE,
    baseName,
    readerUrl,
    pageImage,
    comicInfo,
    adjacentInfo,
    readerRoutes,
    loadReader,
    readerReady,
    loadWaitingImages,
    waitWithImages,
    openReader,
    openWebcomicReader,
    press,
    toasts,
    pageRequests,
    progressPosts,
    markReadPosts,
    adjacentRequests,
    shownPage,
    waitForShownPage,
    streamPages,
    streamImage,
    pageObserver,
    setLayout,
    contentLayout,
    scrollContent,
    FakeMessageChannel,
    serviceWorkerController,
    withController
};
