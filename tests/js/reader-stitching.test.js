'use strict';

// Webcomic mode reading on through a series (js/pages/reader.js): the next
// issue is appended to the bottom of the stream before the reader gets there,
// the stream follows the reader across the issue boundary, and the end of the
// series (or a failed append) is reported at the bottom.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    FILE, NEXT_FILE, THIRD_FILE, pageImage, comicInfo, adjacentInfo, openWebcomicReader, progressPosts,
    markReadPosts, adjacentRequests, streamPages, streamImage, pageObserver, setLayout, contentLayout,
    scrollContent
} = require('./support/reader-helpers');

const RETRY = 'Could not load the next issue. Scroll again to retry.';
// A 3-page issue in a 5000px tall stream seen through an 800px viewport:
// scrolling to 2500 is within the stitch-ahead distance (2000px) of the end,
// 4200 is the very bottom.
const NEAR_END = 2500;
const BOTTOM = 4200;

const pagesOf = (file, pages) => pages.map(pageNum => `${file}#${pageNum}`);

/** #webcomicContainer's children: page images as 'file#page', then the stitching furniture by role. */
function streamLayout(page) {
    return [...page.$('#webcomicContainer').children].map(el => {
        if (el.matches('img[data-page]')) return `${el.dataset.comic}#${el.dataset.page}`;
        if (el.classList.contains('comic-transition')) return 'transition';
        if (el.classList.contains('new-comic-start-marker')) return 'marker';
        if (el.id === 'webcomicStreamEndMessage') return 'end';
        return el.outerHTML;
    });
}

const endMessage = page => page.$('#webcomicStreamEndMessage')?.textContent ?? null;

/** Scroll the stream and wait for whatever the scroll set off (stitching, marking read) to finish. */
async function scrollAndSettle(page, scrollTop) {
    await page.waitFor(() => !page.run('isLoadingNext'));
    scrollContent(page, scrollTop);
    await page.waitFor(() => !page.run('isLoadingNext'));
    await page.settle();
}

function openSeries(series, options = {}) {
    return openWebcomicReader({ series, beforeScripts: contentLayout(5000), ...options });
}

test('the next issue is appended below the open one, behind a transition, without moving the view', async () => {
    const oddName = '/comics/Series/Issue <em>2<em> & co.cbz';
    const page = await openSeries({ [FILE]: 3, [oddName]: 4 });

    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, oddName, 4));

    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'transition', 'marker', ...pagesOf(oddName, [1, 2, 3, 4])]);
    const transition = page.$('.comic-transition');
    assert.equal(transition.querySelector('.comic-transition-leaving').textContent, 'Finished: Issue 001.cbz');
    // File names are text, never markup.
    assert.equal(transition.querySelector('.comic-transition-entering').textContent, 'Continue: Issue <em>2<em> & co.cbz');
    assert.equal(transition.querySelector('em'), null);
    const marker = page.$('.new-comic-start-marker');
    assert.equal(marker.id, `newComicStartMarker-${encodeURIComponent(oddName)}`);
    assert.equal(marker.dataset.comicPath, oddName);

    // The new pages are tracked like the first issue's.
    assert.ok(page.$$('#webcomicContainer img[data-page]').every(img => pageObserver(page).targets.has(img)));
    // The pointer is moved on to the issue after the new tail.
    assert.deepEqual(adjacentRequests(page).at(-1), { file: oddName, direction: 'next' });

    // The reader is still where they were, in the first issue, with nothing marked read.
    assert.equal(page.$('#readerContent').scrollTop, NEAR_END);
    assert.deepEqual(page.scrolledIntoView, []);
    assert.equal(page.$('#comicTitle').textContent, 'Issue 001.cbz');
    assert.deepEqual(markReadPosts(page), []);
    assert.equal(endMessage(page), null);

    // Nothing more to append: further scrolling doesn't stitch anything again.
    await scrollAndSettle(page, NEAR_END + 100);
    assert.equal(page.$$('.comic-transition').length, 1);
    assert.equal(page.fetchCalls.filter(call => call.path === '/api/ComicReader/info' && call.query.filePath === oddName).length, 1);
});

test('scrolling onto a stitched issue makes it the open comic and marks the one before it read', async () => {
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 });
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));

    pageObserver(page).trigger(true, [streamImage(page, NEXT_FILE, 2)]);
    assert.equal(page.$('#comicTitle').textContent, 'Issue 002.cbz');
    assert.equal(page.$('#pageInfo').textContent, 'Page 2 / 4');
    assert.deepEqual(progressPosts(page), [{ file: NEXT_FILE, page: 2 }]);
    assert.deepEqual(markReadPosts(page), [FILE]);

    // Scrolling back up into the earlier issue re-opens it but marks nothing else.
    pageObserver(page).trigger(true, [streamImage(page, FILE, 3)]);
    assert.equal(page.$('#comicTitle').textContent, 'Issue 001.cbz');
    assert.equal(page.$('#pageInfo').textContent, 'Page 3 / 3');
    assert.deepEqual(progressPosts(page).at(-1), { file: FILE, page: 3 });
    await page.settle();
    assert.deepEqual(markReadPosts(page), [FILE]);
});

test('when scrolling stops inside a stitched issue, that issue and page become current', async t => {
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 });
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));
    t.mock.timers.enable({ apis: ['setTimeout'] });

    // Images 1000px tall in stream order: issue 2 page 3 is the sixth.
    page.$$('#webcomicContainer img[data-page]').forEach((img, index) => {
        setLayout(img, { rect: { top: index * 1000 - 5000, bottom: (index + 1) * 1000 - 5000 } });
    });
    scrollContent(page, 3000);
    t.mock.timers.tick(150);

    assert.equal(page.$('#comicTitle').textContent, 'Issue 002.cbz');
    assert.equal(page.$('#pageInfo').textContent, 'Page 3 / 4');
    assert.deepEqual(progressPosts(page), [{ file: NEXT_FILE, page: 3 }]);
    assert.deepEqual(markReadPosts(page), [FILE]);
});

test('reaching the bottom of the last issue marks it read and says the series has ended, once', async () => {
    const page = await openSeries({ [FILE]: 3 });

    await scrollAndSettle(page, NEAR_END);
    assert.deepEqual(markReadPosts(page), []);
    assert.equal(endMessage(page), null);

    await scrollAndSettle(page, BOTTOM);
    assert.deepEqual(markReadPosts(page), [FILE]);
    assert.equal(endMessage(page), 'End of series');

    await scrollAndSettle(page, BOTTOM - 50);
    await scrollAndSettle(page, BOTTOM);
    assert.deepEqual(markReadPosts(page), [FILE]);
    assert.equal(page.$$('#webcomicStreamEndMessage').length, 1);
    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'end']);
});

test('an issue whose details cannot be loaded leaves a retry hint until a later scroll succeeds', async () => {
    let infoFails = true;
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 }, {
        routes: {
            'GET /api/ComicReader/info': call => (call.query.filePath === NEXT_FILE
                ? (infoFails ? respond(503, { error: 'busy' }) : comicInfo(NEXT_FILE, 4))
                : comicInfo(FILE, 3))
        }
    });

    await scrollAndSettle(page, NEAR_END);
    assert.equal(endMessage(page), RETRY);
    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'end']);

    infoFails = false;
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));
    assert.equal(endMessage(page), null);
    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'transition', 'marker', ...pagesOf(NEXT_FILE, [1, 2, 3, 4])]);
});

test('a stitched issue whose opening pages fail is taken out again and retried later', async () => {
    let pageFails = true;
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 }, {
        routes: {
            'GET /api/ComicReader/page': call => (pageFails && call.query.filePath === NEXT_FILE && call.query.page === '3'
                ? respond(500, { error: 'Failed to read page' })
                : pageImage(call))
        }
    });

    await scrollAndSettle(page, NEAR_END);
    // Pages 1, 2 and 4 arrived but are removed with the rest of the issue.
    assert.equal(endMessage(page), RETRY);
    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'end']);
    assert.ok([...pageObserver(page).targets].every(img => img.dataset.comic === FILE));
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error loading page 3:'));
    // The reader still treats the first issue as the end of the stream.
    assert.equal(page.run('streamTailPath'), FILE);

    pageFails = false;
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));
    assert.equal(endMessage(page), null);
    assert.deepEqual(streamLayout(page), [...pagesOf(FILE, [1, 2, 3]), 'transition', 'marker', ...pagesOf(NEXT_FILE, [1, 2, 3, 4])]);
});

test('a network error while stitching is reported with a retry hint', async () => {
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 }, {
        routes: {
            'GET /api/ComicReader/info': call => {
                if (call.query.filePath === NEXT_FILE) throw new TypeError('Failed to fetch');
                return comicInfo(FILE, 3);
            }
        }
    });

    await scrollAndSettle(page, NEAR_END);
    assert.equal(endMessage(page), RETRY);
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error stitching next comic:' && m.args[1].message === 'Failed to fetch'));
    assert.equal(page.$('.comic-transition'), null);
});

test('a failed next-issue lookup is retried as the reader nears the end again', async () => {
    let lookup = 'busy';
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 }, {
        routes: {
            'GET /api/ComicReader/adjacent': call => {
                if (lookup === 'busy') return respond(503, { error: 'busy' });
                if (lookup === 'offline') throw new TypeError('Failed to fetch');
                return adjacentInfo(call.query.filePath === FILE ? NEXT_FILE : null);
            }
        }
    });

    await scrollAndSettle(page, NEAR_END);
    const lookups = adjacentRequests(page).length;
    assert.equal(streamImage(page, NEXT_FILE, 1), undefined);

    lookup = 'offline';
    await scrollAndSettle(page, NEAR_END);
    assert.equal(adjacentRequests(page).length, lookups + 1);
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error prefetching next comic:'));
    assert.equal(streamImage(page, NEXT_FILE, 1), undefined);

    lookup = 'ok';
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));
    assert.deepEqual(streamPages(page), [...pagesOf(FILE, [1, 2, 3]), ...pagesOf(NEXT_FILE, [1, 2, 3, 4])]);
});

test('the final page of the open issue is fetched again before the issue can count as finished', async () => {
    let finalPageFails = true;
    const page = await openSeries({ [FILE]: 3 }, {
        routes: {
            'GET /api/ComicReader/page': call => (finalPageFails && call.query.page === '3'
                ? respond(500, { error: 'Failed to read page' })
                : pageImage(call))
        }
    });

    await scrollAndSettle(page, BOTTOM);
    assert.deepEqual(streamPages(page), pagesOf(FILE, [1, 2]));
    // At the bottom of what is there, but the issue has not been read through.
    assert.deepEqual(markReadPosts(page), []);

    finalPageFails = false;
    await scrollAndSettle(page, BOTTOM);
    assert.deepEqual(streamPages(page), pagesOf(FILE, [1, 2, 3]));

    await scrollAndSettle(page, BOTTOM);
    assert.deepEqual(markReadPosts(page), [FILE]);
    assert.equal(endMessage(page), 'End of series');
});

test('a next-issue pointer naming an issue already in the stream is looked up again, never duplicated', async () => {
    // What the library answers for the issue after NEXT_FILE.
    let afterNext = FILE;
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4, [THIRD_FILE]: 2 }, {
        routes: {
            'GET /api/ComicReader/adjacent': call => adjacentInfo(call.query.filePath === FILE ? NEXT_FILE : afterNext)
        }
    });
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));
    const lookups = adjacentRequests(page).length;

    await scrollAndSettle(page, NEAR_END);
    assert.deepEqual(adjacentRequests(page).slice(lookups), [{ file: NEXT_FILE, direction: 'next' }]);
    assert.deepEqual(streamPages(page), [...pagesOf(FILE, [1, 2, 3]), ...pagesOf(NEXT_FILE, [1, 2, 3, 4])]);

    afterNext = THIRD_FILE;
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, THIRD_FILE, 2));
    assert.deepEqual(streamLayout(page), [
        ...pagesOf(FILE, [1, 2, 3]), 'transition', 'marker',
        ...pagesOf(NEXT_FILE, [1, 2, 3, 4]), 'transition', 'marker',
        ...pagesOf(THIRD_FILE, [1, 2])
    ]);
    assert.equal(page.$$('.comic-transition-leaving')[1].textContent, 'Finished: Issue 002.cbz');
    assert.equal(page.$$('.comic-transition-entering')[1].textContent, 'Continue: Issue 003.cbz');
});

test('a stale pointer that resolves to no further issue ends the series', async () => {
    let afterNext = NEXT_FILE;
    const page = await openSeries({ [FILE]: 3, [NEXT_FILE]: 4 }, {
        routes: {
            'GET /api/ComicReader/adjacent': call => adjacentInfo(call.query.filePath === FILE ? NEXT_FILE : afterNext)
        }
    });
    await scrollAndSettle(page, NEAR_END);
    await page.waitFor(() => streamImage(page, NEXT_FILE, 4));

    afterNext = null;
    await scrollAndSettle(page, BOTTOM);
    await scrollAndSettle(page, BOTTOM);
    assert.equal(endMessage(page), 'End of series');
    assert.deepEqual(markReadPosts(page), [NEXT_FILE]);
    assert.equal(page.$$('.comic-transition').length, 1);
});
