'use strict';

// Comic reader start-up (initReader in js/pages/reader.js): which page opens,
// what the header shows, how auth is sent, and the error states.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const { validToken } = require('./support/app');
const {
    FILE, NEXT_FILE, loadReader, openReader, readerReady, pageRequests, progressPosts, markReadPosts,
    adjacentRequests, shownPage, waitForShownPage, toasts
} = require('./support/reader-helpers');

const infoCall = page => page.fetchCalls.find(call => call.path === '/api/ComicReader/info');

test('opens the comic at its saved page with the header, footer and progress bar filled in', async () => {
    const token = validToken();
    const page = await openReader({ token, progress: 4 });

    assert.equal(page.$('#comicTitle').textContent, 'Issue 001.cbz');
    assert.equal(page.document.title, 'Issue 001.cbz - Comic Reader');
    assert.equal(shownPage(page).alt, 'Page 4');
    assert.equal(page.$('#pageInfo').textContent, 'Page 4 / 10');
    assert.equal(page.$('#footerPageInfo').textContent, '4 / 10');
    const slider = page.$('#pageSlider');
    assert.deepEqual([slider.min, slider.max, slider.value], ['1', '10', '4']);
    assert.equal(page.$('#readerProgressBar').style.width, '40%');
    assert.equal(page.$('#prevBtn').disabled, false);
    assert.equal(page.$('#nextBtn').disabled, false);

    // Manga mode is the default: footer navigation, no webcomic stream.
    assert.equal(page.$('#modeToggleLabel').textContent, 'Manga');
    assert.ok(page.$('#readerContainer').classList.contains('reader-mode-manga'));
    assert.equal(page.$('#readerFooter').style.display, 'flex');
    assert.equal(page.$('#webcomicContainer'), null);

    // Every request carries the session: cookies plus the bearer token.
    const info = infoCall(page);
    assert.equal(info.query.filePath, FILE);
    assert.equal(info.headers.authorization, `Bearer ${token}`);
    assert.equal(info.init.credentials, 'include');

    // The page is saved as progress, its neighbours are preloaded and the next
    // issue is looked up for a seamless hand-over.
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }]);
    assert.deepEqual(pageRequests(page).map(request => request.page), [4, 5, 6, 3]);
    assert.deepEqual(adjacentRequests(page), [{ file: FILE, direction: 'next' }]);
    assert.deepEqual(markReadPosts(page), []);

    // The new page fades in once painted.
    await page.waitFor(() => !shownPage(page).classList.contains('page-fade'));
    assert.deepEqual(page.errors, []);
    assert.deepEqual(page.navigations, []);
});

test('a page queued while offline wins over the server copy until it has been replayed', async () => {
    const queued = JSON.stringify([[FILE, { page: 7, updatedAt: 1 }]]);
    // The server is still unreachable for writes, so the queue cannot drain.
    const stale = await openReader({
        progress: 2,
        localStorage: { pendingReadingProgress: queued },
        routes: { 'POST /api/ComicReader/progress': 503 }
    });
    assert.equal(shownPage(stale).alt, 'Page 7');
    assert.equal(new Map(JSON.parse(stale.window.localStorage.getItem('pendingReadingProgress'))).get(FILE).page, 7);

    // Same when the saved position cannot be read at all.
    const offline = await openReader({
        localStorage: { pendingReadingProgress: queued },
        routes: {
            'GET /api/ComicReader/progress': () => { throw new TypeError('Failed to fetch'); },
            'POST /api/ComicReader/progress': 503
        }
    });
    assert.equal(shownPage(offline).alt, 'Page 7');

    const failing = await openReader({
        localStorage: { pendingReadingProgress: queued },
        routes: { 'GET /api/ComicReader/progress': 500, 'POST /api/ComicReader/progress': 503 }
    });
    assert.equal(shownPage(failing).alt, 'Page 7');
});

test('saved progress past the end opens the last page, which marks the issue read', async () => {
    const page = await openReader({ series: { [FILE]: 10, [NEXT_FILE]: 8 }, progress: 25 });

    assert.equal(shownPage(page).alt, 'Page 10');
    assert.equal(page.$('#readerProgressBar').style.width, '100%');
    assert.deepEqual(markReadPosts(page), [FILE]);
    // Opening on the last page warms the next issue's opening pages.
    await page.waitFor(() => pageRequests(page, NEXT_FILE).length === 5);
    assert.deepEqual(pageRequests(page, NEXT_FILE).map(request => request.page), [1, 2, 3, 4, 5]);
});

test('unreadable saved progress starts the comic at page 1', async () => {
    const failed = await openReader({ routes: { 'GET /api/ComicReader/progress': respond(500, { error: 'boom' }) } });
    assert.equal(shownPage(failed).alt, 'Page 1');
    assert.ok(failed.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Failed to load reading progress:' && m.args[1] === 500));

    const offline = await openReader({
        routes: { 'GET /api/ComicReader/progress': () => { throw new TypeError('Failed to fetch'); } }
    });
    assert.equal(shownPage(offline).alt, 'Page 1');
    assert.ok(offline.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error loading reading progress:'));
});

test('#lastpage (from "previous issue") opens the final page and drops the hash', async () => {
    const page = await openReader({ hash: '#lastpage', progress: 3 });

    assert.equal(shownPage(page).alt, 'Page 10');
    assert.equal(page.window.location.hash, '');
    assert.equal(page.window.location.href, `http://localhost/reader.html?file=${encodeURIComponent(FILE)}`);
    // The saved position is irrelevant here, so it is not even fetched.
    assert.equal(page.fetchCalls.some(call => call.method === 'GET' && call.path === '/api/ComicReader/progress'), false);
});

test('without ?file= the reader explains the problem and offers a way back', async () => {
    const page = loadReader({ file: null });
    await page.settle();

    assert.equal(page.$('#readerContent .error-message').textContent, 'No comic file specified');
    assert.equal(page.$('#readerContent .error-title').textContent, "Couldn't load this comic");
    assert.equal(page.fetchCalls.some(call => call.path === '/api/ComicReader/info'), false);

    page.click('#readerContent .error button');
    assert.deepEqual(page.navigations, [{ url: 'http://localhost/', replace: false }]);

    // The header's email action has no comic to send.
    page.click('#emailComicBtn');
    await page.settle(2);
    assert.deepEqual(toasts(page), [{ message: 'No comic is open.', type: 'error' }]);
    assert.equal(page.$('#emailPrompt').classList.contains('show'), false);
});

test('a signed-out visitor is sent to the login page', async () => {
    const page = loadReader({ token: null });
    await page.settle();

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
    assert.equal(page.fetchCalls.some(call => call.path === '/api/ComicReader/info'), false);
    assert.equal(readerReady(page), false);
});

test('an Authelia session reads with cookies only, never a bearer header', async () => {
    const page = await openReader({
        token: null,
        series: { [FILE]: 3, [NEXT_FILE]: 3 },
        progress: 3,
        localStorage: { authelia_authenticated: 'true' }
    });

    assert.equal(shownPage(page).alt, 'Page 3');
    assert.deepEqual(markReadPosts(page), [FILE]);
    assert.deepEqual(adjacentRequests(page), [{ file: FILE, direction: 'next' }]);
    for (const call of page.fetchCalls) {
        assert.equal(call.headers.authorization, undefined, `${call.method} ${call.url}`);
        assert.equal(call.init.credentials, 'include', `${call.method} ${call.url}`);
    }
});

test('an expired session while opening the comic goes back to login', async () => {
    const page = loadReader({ routes: { 'GET /api/ComicReader/info': 401 } });
    await page.settle();

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
    assert.equal(page.fetchCalls.some(call => call.path === '/api/ComicReader/progress'), false);
});

test('a comic that cannot be opened shows the error state', async () => {
    const missing = loadReader({ file: '/comics/missing.cbz' });
    await missing.waitFor(() => missing.$('#readerContent .error-message'));
    assert.equal(missing.$('#readerContent .error-message').textContent, 'Failed to load comic: Failed to load comic info');
    assert.ok(missing.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error initializing reader:'));
    assert.equal(readerReady(missing), false);

    // Server text in the message is shown as text, never parsed as markup.
    const offline = loadReader({ routes: { 'GET /api/ComicReader/info': () => { throw new TypeError('<img src=x onerror=alert(1)>'); } } });
    await offline.waitFor(() => offline.$('#readerContent .error-message'));
    assert.equal(offline.$('#readerContent .error-message').textContent, 'Failed to load comic: <img src=x onerror=alert(1)>');
    assert.equal(offline.$('#readerContent img'), null);
});

test('a comic without pages asks for none and records no progress, in either mode', async () => {
    for (const readingMode of ['manga', 'webcomic']) {
        const page = await openReader({ series: { [FILE]: 0 }, localStorage: { readingMode } });

        assert.deepEqual(pageRequests(page), [], readingMode);
        assert.deepEqual(progressPosts(page), [], readingMode);
        assert.equal(page.$('#readerContent img[data-page]'), null, readingMode);
        assert.deepEqual(page.errors, [], readingMode);
    }
});

test('a page that fails to load shows the error state', async () => {
    const page = loadReader({ routes: { 'GET /api/ComicReader/page': respond(500, { error: 'Error reading page' }) } });
    await page.waitFor(() => page.$('#readerContent .error-message'));

    assert.equal(page.$('#readerContent .error-message').textContent, 'Failed to load page: Failed to load page');
    assert.deepEqual(page.navigations, []);
    // Nothing was read, so no progress is recorded.
    assert.deepEqual(progressPosts(page), []);
});

test('a page request rejected for auth sends the reader to login', async () => {
    const page = loadReader({ routes: { 'GET /api/ComicReader/page': 401 } });
    await page.waitFor(() => page.navigations.length > 0);

    assert.deepEqual(page.navigations, [{ url: 'http://localhost/login.html', replace: false }]);
});

test('a page fetch that throws is logged and shown as a failed page', async () => {
    const page = loadReader({ routes: { 'GET /api/ComicReader/page': () => { throw new TypeError('Failed to fetch'); } } });
    await page.waitFor(() => page.$('#readerContent .error-message'));

    assert.equal(page.$('#readerContent .error-message').textContent, 'Failed to load page: Failed to load page');
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'fetchMangaPageUrl failed for page' && m.args[1] === 1));
});

test('the saved reading direction is restored', async () => {
    const rtl = await openReader({ localStorage: { readingDirection: 'rtl' } });
    assert.equal(rtl.$('#directionToggleLabel').textContent, 'RTL');
    assert.equal(rtl.$('#directionToggleBtn').title, 'Reading right-to-left (R)');

    const bogus = await openReader({ localStorage: { readingDirection: 'sideways' } });
    assert.equal(bogus.$('#directionToggleLabel').textContent, 'LTR');
    assert.equal(bogus.$('#directionToggleBtn').title, 'Reading left-to-right (R)');
});

test('a direction preference that cannot be read leaves the reader left-to-right', async () => {
    const page = await openReader({
        beforeScripts(window) {
            const getItem = window.Storage.prototype.getItem;
            window.Storage.prototype.getItem = function (key) {
                if (key === 'readingDirection') throw new window.DOMException('denied', 'SecurityError');
                return getItem.call(this, key);
            };
        }
    });

    assert.equal(page.$('#directionToggleLabel').textContent, 'LTR');
    assert.equal(shownPage(page).alt, 'Page 1');
    assert.deepEqual(page.errors, []);
});

test('the email action is hidden from read-only users', async () => {
    const readOnly = await openReader({ user: { canModifyLibrary: false, canAdminister: false } });
    assert.equal(readOnly.$('#emailComicBtn').hidden, true);

    const editor = await openReader();
    assert.equal(editor.$('#emailComicBtn').hidden, false);
    assert.equal(editor.fetchCalls.find(call => call.path === '/api/auth/user').init.credentials, 'include');

    // When the user cannot be looked up the button stays; the API still enforces.
    const unknown = await openReader({ routes: { 'GET /api/auth/user': 500 } });
    assert.equal(unknown.$('#emailComicBtn').hidden, false);
    const unreachable = await openReader({ routes: { 'GET /api/auth/user': () => { throw new TypeError('Failed to fetch'); } } });
    assert.equal(unreachable.$('#emailComicBtn').hidden, false);
});

test('the reader still works when optional chrome is missing from the markup', async () => {
    const optional = [
        '#pageSlider', '#modeToggleLabel', '#pageInfo', '#footerPageInfo', '#readerProgressBar',
        '#directionToggleLabel', '#directionToggleBtn', '#emailComicBtn'
    ];
    const page = await openReader({
        beforeScripts(window) {
            for (const selector of optional) window.document.querySelector(selector).remove();
        }
    });

    assert.equal(shownPage(page).alt, 'Page 1');
    page.click('#nextBtn');
    await waitForShownPage(page, 'Page 2');
    assert.deepEqual(progressPosts(page).map(post => post.page), [1, 2]);
    // Without the email action there is no reason to look the user up.
    assert.equal(page.fetchCalls.some(call => call.path === '/api/auth/user'), false);
    assert.deepEqual(page.errors, []);
});
