'use strict';

// Reading progress in the comic reader (js/pages/reader.js): every page change
// is saved; a save that cannot reach the server is parked in localStorage and
// replayed once the connection is back; finishing an issue marks it read once.

const test = require('node:test');
const assert = require('node:assert/strict');
const { FILE, NEXT_FILE, openReader, press, progressPosts, markReadPosts, waitForShownPage } = require('./support/reader-helpers');

const OTHER_FILE = '/comics/Other/Annual.cbz';
const QUEUE_KEY = 'pendingReadingProgress';

const storedQueue = page => page.window.localStorage.getItem(QUEUE_KEY);
const queuedPages = page => Object.fromEntries(Object.entries(JSON.parse(storedQueue(page)) || {}).map(([file, entry]) => [file, entry.page]));
const logged = (page, type, first) => page.consoleMessages.filter(m => m.type === type && m.args[0] === first);
const postsFor = (page, file) => progressPosts(page).filter(post => post.file === file).map(post => post.page);

async function turnTo(page, key, alt) {
    press(page, key);
    await waitForShownPage(page, alt);
    await page.settle();
}

test('progress that cannot be saved is queued, newest page per comic, until a save gets through', async () => {
    let server = 503;
    const page = await openReader({
        progress: 3,
        routes: {
            'POST /api/ComicReader/progress': () => {
                if (server === 'offline') throw new TypeError('Failed to fetch');
                return server;
            }
        }
    });

    assert.deepEqual(queuedPages(page), { [FILE]: 3 });
    assert.equal(typeof JSON.parse(storedQueue(page))[FILE].updatedAt, 'number');
    assert.equal(logged(page, 'log', 'Queueing reading progress until back online:')[0].args[1].message, 'Server returned 503');

    await turnTo(page, 'ArrowRight', 'Page 4');
    assert.deepEqual(queuedPages(page), { [FILE]: 4 });

    // A network error is just as temporary.
    server = 'offline';
    await turnTo(page, 'ArrowRight', 'Page 5');
    assert.deepEqual(queuedPages(page), { [FILE]: 5 });

    server = 200;
    await turnTo(page, 'ArrowRight', 'Page 6');
    assert.equal(storedQueue(page), null);
    assert.deepEqual(postsFor(page, FILE), [3, 4, 5, 6]);
    assert.ok(logged(page, 'log', 'Saved reading progress:').some(m => m.args[1] === FILE && m.args[3] === 6));

    // A rejected update would be rejected again, so it is dropped rather than queued.
    server = 400;
    await turnTo(page, 'ArrowRight', 'Page 7');
    assert.equal(storedQueue(page), null);
    assert.deepEqual(logged(page, 'error', 'Failed to save reading progress:').map(m => m.args[1]), [400]);
});

test('a queue left by an earlier visit is replayed, stopping at the first save that still fails', async () => {
    let reachable = false;
    const page = await openReader({
        localStorage: {
            [QUEUE_KEY]: JSON.stringify({ [OTHER_FILE]: { page: 5, updatedAt: 1 }, [NEXT_FILE]: { page: 2, updatedAt: 2 } })
        },
        routes: { 'POST /api/ComicReader/progress': call => (call.query.filePath === FILE || reachable ? 200 : 503) }
    });

    // Replayed at start-up and again after the reader's own save, both times
    // giving up at the first entry.
    assert.ok(postsFor(page, OTHER_FILE).length >= 1);
    assert.deepEqual(postsFor(page, NEXT_FILE), []);
    assert.ok(logged(page, 'log', 'Reading progress still pending:').length >= 1);
    assert.deepEqual(queuedPages(page), { [OTHER_FILE]: 5, [NEXT_FILE]: 2 });

    reachable = true;
    const before = progressPosts(page).length;
    page.window.dispatchEvent(new page.window.Event('online'));
    await page.waitFor(() => storedQueue(page) === null);
    assert.deepEqual(progressPosts(page).slice(before), [{ file: OTHER_FILE, page: 5 }, { file: NEXT_FILE, page: 2 }]);
});

test('returning to the tab replays the queue, but only while online', async () => {
    let online = false;
    let hidden = false;
    const page = await openReader({
        progress: 2,
        localStorage: { [QUEUE_KEY]: JSON.stringify({ [OTHER_FILE]: { page: 5, updatedAt: 1 } }) },
        beforeScripts: window => {
            Object.defineProperty(window.navigator, 'onLine', { configurable: true, get: () => online });
            Object.defineProperty(window.document, 'hidden', { configurable: true, get: () => hidden });
        },
        routes: { 'POST /api/ComicReader/progress': () => (online ? 200 : 503) }
    });

    // Offline at start-up: nothing is replayed, and the reader's own save is queued too.
    assert.deepEqual(postsFor(page, OTHER_FILE), []);
    assert.deepEqual(queuedPages(page), { [OTHER_FILE]: 5, [FILE]: 2 });

    const visibilityChange = () => page.document.dispatchEvent(new page.window.Event('visibilitychange'));
    visibilityChange();
    await page.settle();
    assert.deepEqual(postsFor(page, OTHER_FILE), []);

    online = true;
    hidden = true;
    visibilityChange();
    await page.settle();
    assert.deepEqual(postsFor(page, OTHER_FILE), []);

    hidden = false;
    visibilityChange();
    await page.waitFor(() => storedQueue(page) === null);
    assert.deepEqual(postsFor(page, OTHER_FILE), [5]);
    assert.deepEqual(postsFor(page, FILE), [2, 2]);
});

test('queue entries without a page are dropped without being sent', async () => {
    const page = await openReader({
        localStorage: { [QUEUE_KEY]: JSON.stringify({ [OTHER_FILE]: { updatedAt: 1 }, [NEXT_FILE]: null }) }
    });

    await page.waitFor(() => storedQueue(page) === null);
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 1 }]);
});

test('a damaged queue is ignored and replaced by the next one written', async () => {
    for (const damaged of ['{not json', '42', 'null']) {
        let server = 200;
        const page = await openReader({
            progress: 4,
            localStorage: { [QUEUE_KEY]: damaged },
            routes: { 'POST /api/ComicReader/progress': () => server }
        });
        assert.equal(page.$('#readerContent img.page-image').alt, 'Page 4', damaged);
        assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }], damaged);

        server = 503;
        await turnTo(page, 'ArrowRight', 'Page 5');
        assert.deepEqual(queuedPages(page), { [FILE]: 5 }, damaged);
    }
});

test('a page saved while the queue is being replayed is kept for the next replay', async () => {
    let releaseReplay;
    let server = 200;
    const page = await openReader({
        localStorage: { [QUEUE_KEY]: JSON.stringify({ [FILE]: { page: 7, updatedAt: 1 } }) },
        routes: {
            'POST /api/ComicReader/progress': () => {
                // The start-up replay is the first save; hold it.
                if (!releaseReplay) return new Promise(resolve => { releaseReplay = () => resolve(200); });
                return server;
            }
        }
    });
    // The queued page is where the reader opens; saving it succeeds while
    // the replay is still in flight.
    assert.equal(page.$('#readerContent img.page-image').alt, 'Page 7');
    assert.equal(storedQueue(page), null);

    server = 503;
    await turnTo(page, 'ArrowRight', 'Page 8');
    assert.deepEqual(queuedPages(page), { [FILE]: 8 });

    releaseReplay();
    await page.settle(5);
    assert.deepEqual(queuedPages(page), { [FILE]: 8 });
    assert.deepEqual(postsFor(page, FILE), [7, 7, 8]);
});

test('a queue that cannot be stored is given up with a note in the console', async () => {
    const page = await openReader({
        progress: 2,
        beforeScripts: window => {
            const setItem = window.Storage.prototype.setItem;
            window.Storage.prototype.setItem = function (key, value) {
                if (key === QUEUE_KEY) throw new window.DOMException('Quota exceeded', 'QuotaExceededError');
                return setItem.call(this, key, value);
            };
        },
        routes: { 'POST /api/ComicReader/progress': 503 }
    });

    assert.equal(storedQueue(page), null);
    assert.equal(logged(page, 'log', 'Unable to persist pending reading progress:')[0].args[1].name, 'QuotaExceededError');
    await turnTo(page, 'ArrowRight', 'Page 3');
});

test('reaching the last page marks the issue read once, retrying attempts that failed', async () => {
    let server = 500;
    const page = await openReader({
        progress: 9,
        routes: {
            'POST /api/ComicReader/mark-read': () => {
                if (server === 'offline') throw new TypeError('Failed to fetch');
                return server;
            }
        }
    });
    assert.deepEqual(markReadPosts(page), []);

    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.deepEqual(markReadPosts(page), [FILE]);
    assert.deepEqual(logged(page, 'error', 'Failed to mark file as read:').map(m => m.args[1]), [500]);

    server = 'offline';
    await turnTo(page, 'ArrowLeft', 'Page 9');
    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.equal(markReadPosts(page).length, 2);
    assert.equal(logged(page, 'error', 'Error marking file as read:')[0].args[1].message, 'Failed to fetch');

    server = 200;
    await turnTo(page, 'ArrowLeft', 'Page 9');
    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.equal(markReadPosts(page).length, 3);
    assert.ok(logged(page, 'log', 'Marked file as read:').some(m => m.args[1] === FILE));

    await turnTo(page, 'ArrowLeft', 'Page 9');
    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.equal(markReadPosts(page).length, 3);
});

test('an issue is not marked read again while the first request is still on its way', async () => {
    let release;
    const page = await openReader({
        progress: 9,
        routes: { 'POST /api/ComicReader/mark-read': () => new Promise(resolve => { release = () => resolve(200); }) }
    });

    await turnTo(page, 'ArrowRight', 'Page 10');
    await turnTo(page, 'ArrowLeft', 'Page 9');
    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.deepEqual(markReadPosts(page), [FILE]);

    release();
    await page.settle();
    await turnTo(page, 'ArrowLeft', 'Page 9');
    await turnTo(page, 'ArrowRight', 'Page 10');
    assert.deepEqual(markReadPosts(page), [FILE]);
});
