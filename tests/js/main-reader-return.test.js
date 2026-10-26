'use strict';

// js/main.js — the round trip to the reader: readComic() snapshots where the
// user was (sessionStorage 'comicReaderReturnState') and the next boot
// restores it — search, the open series, the scroll position — once, within
// two hours, ignoring anything malformed.

const test = require('node:test');
const assert = require('node:assert/strict');
const { deferred } = require('./support/app');
const { loadScripts } = require('./support/sandbox');
const { boot, loadApp, requests, consoleLines, overview, seriesCard, seriesIssue, seriesIssues, seriesPage } = require('./support/core-helpers');

const KEY = 'comicReaderReturnState';
const SAGA = seriesCard();
const TWO_HOURS = 2 * 60 * 60 * 1000;

const libraryRoutes = (overrides = {}) => ({
    'GET /api/files/series': seriesPage([SAGA]),
    [`GET /api/files/series/${SAGA.id}/issues`]: seriesIssues(SAGA, [seriesIssue()]),
    ...overrides
});
const snapshot = (overrides = {}) => JSON.stringify({
    originUrl: 'http://localhost/index.html#/library',
    scrollY: 0,
    searchQuery: '',
    filterMode: 'all',
    libraryViewMode: 'series',
    currentSeriesDetailId: null,
    timestamp: Date.now(),
    ...overrides
});
const readerFiles = page => page.navigations
    .filter(nav => new URL(nav.url).pathname === '/reader.html')
    .map(nav => new URL(nav.url).searchParams.get('file'));
const scrollsTo = (page, y) => page.scrolls.filter(scroll => scroll.y === y).length;

test('reading from a series page remembers that series, the search and the scroll position', async () => {
    const page = await boot({ hash: `#/series/${SAGA.id}`, routes: libraryRoutes() });
    page.run("searchQuery = 'saga'");
    Object.defineProperty(page.window, 'scrollY', { value: 420, configurable: true, writable: true });
    const before = Date.now();

    page.click('#seriesDetailPanel .series-issue-cover-button');

    const saved = JSON.parse(page.window.sessionStorage.getItem(KEY));
    assert.ok(saved.timestamp >= before && saved.timestamp <= Date.now());
    delete saved.timestamp;
    assert.deepEqual(saved, {
        originUrl: `http://localhost/index.html#/series/${SAGA.id}`,
        scrollY: 420,
        searchQuery: 'saga',
        filterMode: 'all',
        libraryViewMode: 'series',
        currentSeriesDetailId: SAGA.id
    });
    assert.deepEqual(readerFiles(page), ['Saga/Saga 001.cbz']);
});

test('coming back from the reader restores the series page and its scroll position, once', async () => {
    const reading = await boot({ hash: `#/series/${SAGA.id}`, routes: libraryRoutes() });
    Object.defineProperty(reading.window, 'scrollY', { value: 420, configurable: true, writable: true });
    reading.click('#seriesDetailPanel .series-issue-cover-button');
    const saved = reading.window.sessionStorage.getItem(KEY);

    const page = await boot({ hash: `#/series/${SAGA.id}`, routes: libraryRoutes(), sessionStorage: { [KEY]: saved } });
    await page.waitFor(() => scrollsTo(page, 420) === 1);

    assert.equal(page.evaluate('currentView'), 'series');
    assert.equal(page.evaluate('currentSeriesDetailId'), SAGA.id);
    assert.equal(page.window.sessionStorage.getItem(KEY), null, 'the snapshot is used up');
    assert.deepEqual(page.navigations, [], 'the series was already open; nothing navigated');
});

test('the search the user had is put back and the library reloaded with it', async () => {
    const page = await boot({ hash: '#/library', routes: libraryRoutes(), sessionStorage: { [KEY]: snapshot({ searchQuery: 'saga' }) } });

    assert.equal(page.$('#headerSearchInput').value, 'saga');
    assert.equal(page.evaluate('searchQuery'), 'saga');
    assert.equal(page.$('#headerSearchClear').hidden, false);
    assert.equal(requests(page, 'GET', '/api/files/series').at(-1).query.search, 'saga');
    assert.equal(page.scrolls.length, 0, 'no scroll position was saved');
});

test('a series missing from the URL is reopened once the library has loaded it', async () => {
    const list = deferred();
    const timestamp = Date.now();
    const page = loadApp({
        hash: '#/library',
        routes: libraryRoutes({ 'GET /api/files/series': () => list.promise }),
        sessionStorage: { [KEY]: snapshot({ currentSeriesDetailId: SAGA.id, timestamp }) }
    });
    // Stop the page's clock so the 2.5 s wait for the series can't run out on a busy machine.
    page.run(`Date.now = () => ${timestamp}`);
    await page.settle();
    assert.equal(page.evaluate('currentView'), 'library', 'waiting for the library');

    list.resolve(seriesPage([SAGA]));
    await page.waitFor(() => page.evaluate('currentView') === 'series');

    assert.equal(page.window.location.hash, `#/series/${SAGA.id}`);
    assert.equal(page.evaluate('currentSeriesDetailId'), SAGA.id);
    assert.ok(page.$('#seriesDetailPanel'));
});

test('a series that no longer exists is given up on after 2.5 seconds, and the scroll position still restored', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    const now = Date.now();
    const page = loadApp({
        hash: '#/library',
        routes: libraryRoutes(),
        sessionStorage: { [KEY]: snapshot({ currentSeriesDetailId: 'series-gone', scrollY: 300, timestamp: now }) }
    });
    page.run(`{ let offset = 0; Date.now = () => ${now} + offset; globalThis.advanceClock = ms => { offset += ms; }; }`);
    await page.settle();
    const poll = ms => {
        page.run(`advanceClock(${ms})`);
        t.mock.timers.tick(ms);
    };

    for (let elapsed = 80; elapsed <= 2480; elapsed += 80) poll(80);
    await page.settle();
    t.mock.timers.tick(17);
    assert.equal(scrollsTo(page, 300), 0, 'still waiting for the series after 2.48 s');

    poll(80); // 2.56 s: gives up
    await page.settle();
    t.mock.timers.tick(17); // next animation frame
    await page.settle();

    assert.equal(scrollsTo(page, 300), 1);
    assert.equal(page.window.location.hash, '#/library');
    assert.equal(page.evaluate('currentView'), 'library');
});

test('restoring the scroll position retries while the page grows, and gives up after about 1.5 seconds', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    for (const growsAfter of [3, Infinity]) {
        const attempts = [];
        let pageHeight = 200;
        const page = await boot({
            hash: '#/library',
            routes: libraryRoutes(),
            sessionStorage: { [KEY]: snapshot({ scrollY: 900 }) },
            beforeScripts(window) {
                const scrollTo = window.scrollTo;
                // Like a browser, clamp to what the page can scroll to so far.
                window.scrollTo = (x, y) => {
                    attempts.push(y);
                    scrollTo(x, Math.min(y, pageHeight));
                    if (attempts.length === growsAfter) pageHeight = 5000;
                };
            }
        });

        t.mock.timers.tick(17); // first attempt on the next animation frame
        for (let i = 0; i < 40; i++) t.mock.timers.tick(50);

        if (growsAfter === 3) {
            assert.deepEqual(attempts, [900, 900, 900, 900], 'the attempt after the page grew reached the target');
            assert.equal(page.window.scrollY, 900);
        } else {
            assert.equal(attempts.length, 31);
            assert.equal(page.window.scrollY, 200);
        }
    }
});

test('where only the root element reports the scroll offset, the restore checks that instead', async t => {
    t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
    for (const [name, rootScrolls, expectedAttempts] of [['the root element scrolls', true, 1], ['nothing scrolls', false, 31]]) {
        const attempts = [];
        const page = await boot({
            hash: '#/library',
            routes: libraryRoutes(),
            sessionStorage: { [KEY]: snapshot({ scrollY: 640 }) },
            beforeScripts(window) {
                let rootScrollTop = 0;
                Object.defineProperty(window.document.documentElement, 'scrollTop', { get: () => rootScrollTop, configurable: true });
                // window.scrollY is never updated.
                window.scrollTo = (x, y) => {
                    attempts.push(y);
                    if (rootScrolls) rootScrollTop = y;
                };
            }
        });

        t.mock.timers.tick(17);
        for (let i = 0; i < 40; i++) t.mock.timers.tick(50);

        assert.equal(attempts.length, expectedAttempts, name);
        assert.deepEqual([...new Set(attempts)], [640], name);
        assert.equal(page.window.scrollY, 0, name);
    }
});

test('stale, malformed or foreign snapshots are discarded without effect', async () => {
    const cases = {
        expired: snapshot({ searchQuery: 'saga', scrollY: 300, timestamp: Date.now() - TWO_HOURS - 1000 }),
        'no timestamp': snapshot({ searchQuery: 'saga', scrollY: 300, timestamp: undefined }),
        'not JSON': '{"searchQuery":',
        null: 'null',
        'a number': '42'
    };
    for (const [name, stored] of Object.entries(cases)) {
        const page = await boot({ hash: '#/library', routes: libraryRoutes(), sessionStorage: { [KEY]: stored } });
        await page.settle();

        assert.equal(page.window.sessionStorage.getItem(KEY), null, name);
        assert.equal(page.evaluate('searchQuery'), '', name);
        assert.equal(page.$('#headerSearchInput').value, '', name);
        assert.deepEqual(page.scrolls, [], name);
        assert.equal(requests(page, 'GET', '/api/files/series').length, 1, name);
    }
});

test('a failure while restoring is logged and the app carries on', async () => {
    const page = loadApp({ hash: '#/library', routes: libraryRoutes(), sessionStorage: { [KEY]: snapshot({ scrollY: 300 }) } });
    page.run("restoreScrollPosition = () => Promise.reject(new Error('layout went away'))");
    await page.settle();

    assert.ok(consoleLines(page, 'error').includes('Failed to restore reader return state: layout went away'));
    assert.deepEqual(requests(page, 'POST', '/api/client-errors'), []);
    assert.ok(page.$('#fileList .series-card'));
});

test('the reader still opens when the snapshot cannot be stored', async () => {
    const SAGA_READING = { ...SAGA, resume_file_path: 'Saga/Saga 002.cbz', resume_page: 3, last_read_utc: '2024-05-01T10:00:00Z' };
    const page = await boot({ hash: '#/home', routes: { 'GET /api/overview': overview({ continue_reading: [SAGA_READING] }) } });
    page.run(`{
        const setItem = Storage.prototype.setItem;
        Storage.prototype.setItem = function (key, value) {
            if (this === sessionStorage) throw new DOMException('The quota has been exceeded.', 'QuotaExceededError');
            return setItem.call(this, key, value);
        };
    }`);

    page.click('#overviewRows .overview-resume-btn');

    assert.deepEqual(readerFiles(page), ['Saga/Saga 002.cbz']);
    assert.equal(page.window.sessionStorage.getItem(KEY), null);
});

test('readComic falls back to safe values when the library state is not there', () => {
    const noop = () => {};
    const sandbox = loadScripts(['js/main.js'], {
        onViewportResize: noop,
        applyMobileLibraryView: noop,
        updateSettingsNavActive: noop,
        initModalAccessibility: noop,
        initDropdownAriaExpandedSync: noop,
        initConnectivityAwareness: noop,
        initLibraryKeyboardShortcuts: noop,
        scrollY: 0,
        document: { readyState: 'loading', addEventListener: noop, documentElement: { scrollTop: 75 } }
    });

    sandbox.run("readComic('Saga/Saga 001.cbz')");

    const saved = JSON.parse(sandbox.context.sessionStorage.getItem(KEY));
    delete saved.timestamp;
    assert.deepEqual(saved, {
        originUrl: 'http://localhost/',
        scrollY: 75,
        searchQuery: '',
        filterMode: 'all',
        libraryViewMode: 'files',
        currentSeriesDetailId: null
    });
    assert.equal(sandbox.context.location.href, '/reader.html?file=Saga%2FSaga%20001.cbz');
});
