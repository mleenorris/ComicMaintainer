'use strict';

// Webcomic (continuous vertical scroll) mode of the comic reader
// (js/pages/reader.js): the buffered page stream, tracking the page on screen,
// the keys that behave differently here and switching between modes.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    FILE, pageImage, openReader, openWebcomicReader, waitWithImages, press, progressPosts,
    adjacentRequests, shownPage, waitForShownPage, streamPages, streamImage, pageObserver, setLayout,
    contentLayout, scrollContent
} = require('./support/reader-helpers');

const range = (from, to) => Array.from({ length: to - from + 1 }, (_, i) => from + i);
const pagesOf = (file, pages) => pages.map(pageNum => `${file}#${pageNum}`);

/** Lay the stream's images out 1000px tall, one under the other, viewed from `scrollTop`. */
function layOutImages(page, scrollTop) {
    page.$$('#webcomicContainer img[data-page]').forEach((img, index) => {
        setLayout(img, { rect: { top: index * 1000 - scrollTop, bottom: (index + 1) * 1000 - scrollTop } });
    });
}

test('webcomic mode opens a buffer of pages around the saved page and scrolls to it', async () => {
    const page = await openWebcomicReader({ series: { [FILE]: 30 }, progress: 8, beforeScripts: contentLayout(40000) });

    assert.equal(page.$('#modeToggleLabel').textContent, 'Webcomic');
    assert.ok(page.$('#readerContent').classList.contains('webcomic-mode'));
    assert.ok(page.$('#readerContainer').classList.contains('reader-mode-webcomic'));
    assert.equal(page.$('#readerContainer').classList.contains('reader-mode-manga'), false);
    // The footer only holds manga navigation.
    assert.equal(page.$('#readerFooter').style.display, 'none');
    assert.equal(page.$('#pageInfo').style.display, 'inline');

    // Five pages either side of page 8, in order, each one observed.
    assert.deepEqual(streamPages(page).slice(0, 11), pagesOf(FILE, range(3, 13)));
    const images = page.$$('#webcomicContainer img[data-page]');
    assert.ok(images.every(img => pageObserver(page).targets.has(img)));
    assert.ok(images.every(img => img.src.startsWith('blob:')));
    assert.equal(pageObserver(page).options.root, page.$('#readerContent'));
    // Page fetches carry the session like every other reader request.
    const pageFetch = page.fetchCalls.find(call => call.path === '/api/ComicReader/page');
    assert.match(pageFetch.headers.authorization, /^Bearer /);
    assert.equal(pageFetch.init.credentials, 'include');

    await page.waitFor(() => page.scrolledIntoView.length > 0);
    assert.deepEqual(page.scrolledIntoView.map(img => img.dataset.page), ['8']);
    assert.equal(page.run('isScrolling'), false);
    assert.deepEqual(progressPosts(page), []);
    assert.equal(page.$('#readerContent img.page-image'), null);
});

test('the most visible page becomes the current page, is saved, and pulls in its neighbours', async () => {
    const page = await openWebcomicReader({ series: { [FILE]: 30 }, progress: 8, beforeScripts: contentLayout(40000) });

    pageObserver(page).trigger(true, [streamImage(page, FILE, 11)]);
    assert.equal(page.$('#pageInfo').textContent, 'Page 11 / 30');
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 11 }]);
    // Pages up to 16 are fetched and slot in after the ones already shown.
    await page.waitFor(() => streamImage(page, FILE, 16));
    assert.deepEqual(streamPages(page).filter(key => key !== `${FILE}#30`), pagesOf(FILE, range(3, 16)));

    // Seeing the same page again, or a page leaving the view, changes nothing.
    pageObserver(page).trigger(true, [streamImage(page, FILE, 11)]);
    pageObserver(page).trigger(false, [streamImage(page, FILE, 12)]);
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 11 }]);

    // Targets without a usable page number are ignored...
    const cover = page.document.createElement('img');
    cover.dataset.page = 'cover';
    pageObserver(page).trigger(true, [cover]);
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 11 }]);
    // ...and one without a comic is taken to belong to the open comic.
    const legacy = page.document.createElement('img');
    legacy.dataset.page = '12';
    pageObserver(page).trigger(true, [legacy]);
    assert.equal(page.$('#pageInfo').textContent, 'Page 12 / 30');
    assert.deepEqual(progressPosts(page).at(-1), { file: FILE, page: 12 });
    assert.equal(page.$('#comicTitle').textContent, 'Issue 001.cbz');
});

test('a page that fails to load is skipped and fetched again when it is next needed', async () => {
    let failing = true;
    const page = await openWebcomicReader({
        series: { [FILE]: 30 },
        beforeScripts: contentLayout(40000),
        routes: {
            'GET /api/ComicReader/page': call => (failing && ['3', '9'].includes(call.query.page) ? respond(500, { error: 'busy' }) : pageImage(call))
        }
    });

    assert.deepEqual(streamPages(page).slice(0, 5), pagesOf(FILE, [1, 2, 4, 5, 6]));
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error loading page 3:'));

    // Page 9 fails while prefetching around page 4...
    pageObserver(page).trigger(true, [streamImage(page, FILE, 4)]);
    await page.waitFor(() => streamImage(page, FILE, 8));
    await page.waitFor(() => page.consoleMessages.some(m => m.args[0] === 'Error loading page 9:'));
    assert.equal(streamImage(page, FILE, 9), undefined);

    // ...and both gaps fill in, in order, once the server recovers.
    failing = false;
    pageObserver(page).trigger(true, [streamImage(page, FILE, 5)]);
    await page.waitFor(() => streamImage(page, FILE, 3) && streamImage(page, FILE, 10));
    assert.deepEqual(streamPages(page).filter(key => key !== `${FILE}#30`), pagesOf(FILE, range(1, 10)));
});

test('when scrolling stops, the page nearest the top of the view becomes current', async t => {
    const page = await openWebcomicReader({ series: { [FILE]: 30 }, beforeScripts: contentLayout(40000) });
    t.mock.timers.enable({ apis: ['setTimeout'] });

    layOutImages(page, 3100);
    scrollContent(page, 3100);
    t.mock.timers.tick(149);
    assert.deepEqual(progressPosts(page), []);
    t.mock.timers.tick(1);
    assert.equal(page.$('#pageInfo').textContent, 'Page 4 / 30');
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }]);

    // Scroll events in quick succession settle once, on the final position.
    layOutImages(page, 1050);
    scrollContent(page, 1050);
    t.mock.timers.tick(100);
    layOutImages(page, 2010);
    scrollContent(page, 2010);
    t.mock.timers.tick(100);
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }]);
    t.mock.timers.tick(50);
    assert.equal(page.$('#pageInfo').textContent, 'Page 3 / 30');
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }, { file: FILE, page: 3 }]);

    // Stopping on the page already current saves nothing new.
    scrollContent(page, 2010);
    t.mock.timers.tick(150);
    assert.equal(progressPosts(page).length, 2);

    // Nothing in view (e.g. on a transition): the current page stands.
    layOutImages(page, -5000);
    scrollContent(page, 0);
    t.mock.timers.tick(150);
    assert.equal(page.$('#pageInfo').textContent, 'Page 3 / 30');
    assert.equal(progressPosts(page).length, 2);
});

test('Home and End scroll the stream; page-turn keys are left to the browser', async () => {
    const page = await openWebcomicReader({ series: { [FILE]: 30 }, beforeScripts: contentLayout(40000) });
    const content = page.$('#readerContent');

    press(page, 'End');
    assert.equal(content.scrollTop, 40000);
    press(page, 'Home');
    assert.equal(content.scrollTop, 0);
    for (const key of ['ArrowRight', 'ArrowLeft', 'd', 'a', ' ']) {
        assert.equal(press(page, key).defaultPrevented, false, key);
    }
    await page.settle(2);
    assert.deepEqual(progressPosts(page), []);
    assert.equal(shownPage(page), null);
});

test('go-to-page and the slider scroll to a page that is in the stream', async () => {
    const page = await openWebcomicReader({ series: { [FILE]: 30 }, beforeScripts: contentLayout(40000) });

    press(page, 'g');
    page.$('#gotoPromptInput').value = '5';
    press(page, 'Enter');
    assert.deepEqual(page.scrolledIntoView, [streamImage(page, FILE, 5)]);

    // A page outside the loaded buffer cannot be scrolled to.
    page.$('#fullscreenBtn').focus();
    press(page, 'g');
    page.$('#gotoPromptInput').value = '25';
    press(page, 'Enter');
    assert.equal(page.scrolledIntoView.length, 1);

    page.input('#pageSlider', '6');
    assert.deepEqual(page.scrolledIntoView.at(-1), streamImage(page, FILE, 6));
    page.input('#pageSlider', '24');
    assert.equal(page.scrolledIntoView.length, 2);
    assert.equal(shownPage(page), null);
});

test('switching to webcomic mode and back keeps the reader on the same page', async () => {
    const page = await openReader({ series: { [FILE]: 30 }, progress: 4 });
    const mangaUrls = [...page.objectUrls.keys()];
    const mangaTapZone = page.$('#tapZoneRight');

    page.click('#modeToggleBtn');
    assert.equal(page.window.localStorage.getItem('readingMode'), 'webcomic');
    assert.equal(page.$('#modeToggleLabel').textContent, 'Webcomic');
    // The manga page cache is released.
    assert.ok(mangaUrls.length > 0);
    assert.ok(mangaUrls.every(url => !page.objectUrls.has(url)));

    await waitWithImages(page, () => page.run('allImagesLoaded'));
    assert.deepEqual(streamPages(page).slice(0, 9), pagesOf(FILE, range(1, 9)));
    // Until the view has been moved to page 4, visibility reports are ignored.
    pageObserver(page).trigger(true, [streamImage(page, FILE, 7)]);
    assert.deepEqual(progressPosts(page), [{ file: FILE, page: 4 }]);
    await page.waitFor(() => page.scrolledIntoView.length > 0);
    assert.deepEqual(page.scrolledIntoView.map(img => img.dataset.page), ['4']);

    pageObserver(page).trigger(true, [streamImage(page, FILE, 6)]);
    assert.deepEqual(progressPosts(page).at(-1), { file: FILE, page: 6 });
    // A manga tap zone left over from before the switch does nothing now.
    mangaTapZone.click();
    await page.settle(2);
    assert.equal(shownPage(page), null);

    assert.equal(press(page, 'M').defaultPrevented, true);
    assert.equal(page.window.localStorage.getItem('readingMode'), 'manga');
    assert.equal(page.$('#modeToggleLabel').textContent, 'Manga');
    assert.equal(page.$('#readerFooter').style.display, 'flex');
    await waitForShownPage(page, 'Page 6');
    assert.equal(page.$('#webcomicContainer'), null);
    // The next-issue pointer is resolved again for the comic on screen.
    await page.waitFor(() => adjacentRequests(page).length >= 2);
    assert.deepEqual(adjacentRequests(page).at(-1), { file: FILE, direction: 'next' });

    // Scrolling the manga view does not touch the (gone) stream.
    const before = page.fetchCalls.length;
    scrollContent(page, 0);
    await page.settle();
    assert.equal(page.fetchCalls.length, before);

    // ...and into webcomic mode once more, at the same page.
    press(page, 'm');
    await waitWithImages(page, () => page.scrolledIntoView.length > 1);
    assert.deepEqual(page.scrolledIntoView.map(img => img.dataset.page), ['4', '6']);
    assert.equal(page.intersectionObservers.length, 2);
    assert.equal(page.intersectionObservers[0].targets.size, 0);
});

test('switching modes survives blob URLs that cannot be revoked', async () => {
    const page = await openReader({ series: { [FILE]: 30 }, progress: 4 });
    page.window.URL.revokeObjectURL = () => { throw new Error('revoke failed'); };

    page.click('#modeToggleBtn');
    await waitWithImages(page, () => page.scrolledIntoView.length > 0);
    assert.equal(page.run('mangaPageCache.size'), 0);
    assert.deepEqual(page.errors, []);
});
