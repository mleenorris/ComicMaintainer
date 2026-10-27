'use strict';

// Manga-mode navigation in the comic reader (js/pages/reader.js): footer
// buttons, keyboard shortcuts, tap zones, the page slider, the go-to-page
// prompt, the help dialog, the page cache and moving on to the adjacent issue.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const {
    FILE, NEXT_FILE, PREV_FILE, readerUrl, pageImage, adjacentInfo, openReader, press, toasts, pageRequests,
    progressPosts, adjacentRequests, shownPage, waitForShownPage
} = require('./support/reader-helpers');

/**
 * Press `key` once focus has moved on from a closed prompt (jsdom keeps it on
 * the hidden input, where shortcuts are ignored as typing).
 */
function pressOnBody(page, key, init) {
    page.$('#fullscreenBtn').focus();
    return press(page, key, init);
}

test('the footer buttons and the arrow / A / D keys turn the page', async () => {
    const page = await openReader({ progress: 5 });

    page.click('#nextBtn');
    await waitForShownPage(page, 'Page 6');
    page.click('#prevBtn');
    await waitForShownPage(page, 'Page 5');

    for (const [key, expected] of [['ArrowRight', 6], ['d', 7], ['D', 8], ['ArrowLeft', 7], ['a', 6], ['A', 5]]) {
        assert.equal(press(page, key).defaultPrevented, true, key);
        await waitForShownPage(page, `Page ${expected}`);
    }

    assert.equal(page.$('#pageInfo').textContent, 'Page 5 / 10');
    assert.equal(page.$('#footerPageInfo').textContent, '5 / 10');
    assert.equal(page.$('#pageSlider').value, '5');
    assert.equal(page.$('#readerProgressBar').style.width, '50%');
    // Every page reached is saved as the reading position.
    assert.deepEqual(progressPosts(page).map(post => post.page), [5, 6, 5, 6, 7, 8, 7, 6, 5]);
    assert.deepEqual(page.errors, []);
});

test('Space pages forward, Shift+Space back, and Home / End jump to the covers', async () => {
    const page = await openReader({ progress: 5 });

    assert.equal(press(page, ' ').defaultPrevented, true);
    await waitForShownPage(page, 'Page 6');
    press(page, ' ', { shiftKey: true });
    await waitForShownPage(page, 'Page 5');

    assert.equal(press(page, 'End').defaultPrevented, true);
    await waitForShownPage(page, 'Page 10');
    assert.equal(press(page, 'Home').defaultPrevented, true);
    await waitForShownPage(page, 'Page 1');

    // Keys without a shortcut are left alone.
    assert.equal(press(page, 'x').defaultPrevented, false);
});

test('tapping the left and right edges turns the page', async () => {
    const page = await openReader({ progress: 5 });

    page.click('#tapZoneRight');
    await waitForShownPage(page, 'Page 6');
    // Every render replaces the zones; the new ones are wired as well.
    page.click('#tapZoneRight');
    await waitForShownPage(page, 'Page 7');
    page.click('#tapZoneLeft');
    await waitForShownPage(page, 'Page 6');
    assert.equal(page.$$('#tapZones').length, 1);
});

test('right-to-left reading swaps the arrows, tap zones and footer buttons, but not Space', async () => {
    const page = await openReader({ progress: 5, localStorage: { readingDirection: 'rtl' } });

    press(page, 'ArrowLeft');
    await waitForShownPage(page, 'Page 6');
    press(page, 'ArrowRight');
    await waitForShownPage(page, 'Page 5');
    page.click('#tapZoneLeft');
    await waitForShownPage(page, 'Page 6');
    page.click('#tapZoneRight');
    await waitForShownPage(page, 'Page 5');
    page.click('#nextBtn');
    await waitForShownPage(page, 'Page 4');
    page.click('#prevBtn');
    await waitForShownPage(page, 'Page 5');
    press(page, ' ');
    await waitForShownPage(page, 'Page 6');
});

test('the direction toggle flips the reading direction and remembers it', async () => {
    const page = await openReader({ progress: 5 });

    page.click('#directionToggleBtn');
    assert.equal(page.$('#directionToggleLabel').textContent, 'RTL');
    assert.equal(page.$('#directionToggleBtn').title, 'Reading right-to-left (R)');
    assert.equal(page.window.localStorage.getItem('readingDirection'), 'rtl');
    press(page, 'ArrowLeft');
    await waitForShownPage(page, 'Page 6');

    assert.equal(press(page, 'r').defaultPrevented, true);
    assert.equal(page.$('#directionToggleLabel').textContent, 'LTR');
    assert.equal(page.window.localStorage.getItem('readingDirection'), 'ltr');
    press(page, 'R');
    assert.equal(page.$('#directionToggleLabel').textContent, 'RTL');

    assert.deepEqual(toasts(page), [
        { message: 'Reading direction: RTL', type: 'info' },
        { message: 'Reading direction: LTR', type: 'info' },
        { message: 'Reading direction: RTL', type: 'info' }
    ]);
});

test('a direction that cannot be saved still applies for this visit', async () => {
    const page = await openReader({
        progress: 5,
        beforeScripts(window) {
            const setItem = window.Storage.prototype.setItem;
            window.Storage.prototype.setItem = function (key, value) {
                if (key === 'readingDirection') throw new window.DOMException('full', 'QuotaExceededError');
                return setItem.call(this, key, value);
            };
        }
    });

    page.click('#directionToggleBtn');
    assert.equal(page.$('#directionToggleLabel').textContent, 'RTL');
    assert.equal(page.window.localStorage.getItem('readingDirection'), null);
    press(page, 'ArrowLeft');
    await waitForShownPage(page, 'Page 6');
    assert.deepEqual(page.errors, []);
});

test('dragging the slider previews the page number and releasing it jumps there', async () => {
    const page = await openReader();
    const slider = page.$('#pageSlider');

    slider.value = '7';
    slider.dispatchEvent(new page.window.Event('input', { bubbles: true }));
    assert.equal(page.$('#footerPageInfo').textContent, '7 / 10');
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 1');

    slider.dispatchEvent(new page.window.Event('change', { bubbles: true }));
    await waitForShownPage(page, 'Page 7');
    assert.equal(page.$('#pageInfo').textContent, 'Page 7 / 10');

    // A value that is not a page number is ignored.
    page.run("handleSliderInput({ target: { value: 'abc' } }); handleSliderChange({ target: { value: '' } });");
    await page.settle(2);
    assert.equal(page.$('#footerPageInfo').textContent, '7 / 10');
    assert.equal(shownPage(page).alt, 'Page 7');

    // goToPage() is still callable (e.g. from the console).
    page.run("goToPage('4')");
    await waitForShownPage(page, 'Page 4');
});

test('G opens the go-to-page prompt: Enter jumps, Escape just closes it', async () => {
    const page = await openReader({ progress: 3 });
    const prompt = page.$('#gotoPrompt');
    const input = page.$('#gotoPromptInput');

    assert.equal(press(page, 'g').defaultPrevented, true);
    assert.ok(prompt.classList.contains('show'));
    assert.equal(input.max, '10');
    assert.equal(input.value, '3');
    await page.waitFor(() => page.document.activeElement === input);

    // While the prompt is open, page keys belong to it.
    press(page, 'ArrowRight');
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 3');
    assert.ok(prompt.classList.contains('show'));

    input.value = '8';
    assert.equal(press(page, 'Enter').defaultPrevented, true);
    assert.equal(prompt.classList.contains('show'), false);
    await waitForShownPage(page, 'Page 8');

    pressOnBody(page, 'G');
    assert.equal(input.value, '8');
    assert.equal(press(page, 'Escape').defaultPrevented, true);
    assert.equal(prompt.classList.contains('show'), false);
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 8');
    assert.deepEqual(page.navigations, []);
});

test('the go-to-page buttons ignore numbers outside the comic', async () => {
    const page = await openReader({ progress: 3 });
    const prompt = page.$('#gotoPrompt');
    const input = page.$('#gotoPromptInput');
    const go = page.$('#gotoPrompt button[onclick="submitGotoPage()"]');

    for (const value of ['0', '11', 'abc']) {
        pressOnBody(page, 'g');
        input.value = value;
        page.click(go);
        assert.equal(prompt.classList.contains('show'), false, value);
    }
    pressOnBody(page, 'g');
    page.click('#gotoPrompt button[onclick="hideGotoPrompt()"]');
    assert.equal(prompt.classList.contains('show'), false);
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 3');
    assert.deepEqual(progressPosts(page).map(post => post.page), [3]);

    pressOnBody(page, 'g');
    input.value = '6';
    page.click(go);
    await waitForShownPage(page, 'Page 6');
});

test('without its number input the go-to-page prompt stays closed and Go does nothing', async () => {
    const page = await openReader({ progress: 3 });
    page.$('#gotoPromptInput').remove();
    const requests = pageRequests(page).length;

    press(page, 'g');
    assert.equal(page.$('#gotoPrompt').classList.contains('show'), false);
    page.click('#gotoPrompt button[onclick="submitGotoPage()"]');
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 3');
    assert.equal(pageRequests(page).length, requests);
    assert.deepEqual(page.errors, []);
});

test('? opens the shortcuts help, which swallows other keys until it is closed', async () => {
    const page = await openReader({ progress: 3 });
    const help = page.$('#shortcutsHelp');
    const backdrop = page.$('#shortcutsHelpBackdrop');
    const helpButton = page.$('button[onclick="showHelp()"]');

    helpButton.focus();
    page.click(helpButton);
    assert.ok(help.classList.contains('show'));
    assert.ok(backdrop.classList.contains('show'));
    assert.equal(page.document.activeElement, help);

    press(page, 'ArrowRight');
    press(page, 'g');
    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 3');
    assert.equal(page.$('#gotoPrompt').classList.contains('show'), false);

    // Escape closes the help (not the reader) and gives focus back.
    press(page, 'Escape');
    assert.equal(help.classList.contains('show'), false);
    assert.equal(backdrop.classList.contains('show'), false);
    assert.equal(page.document.activeElement, helpButton);
    assert.deepEqual(page.navigations, []);

    assert.equal(press(page, '?').defaultPrevented, true);
    assert.ok(help.classList.contains('show'));
    page.click(backdrop);
    assert.equal(help.classList.contains('show'), false);
    assert.equal(page.document.activeElement, helpButton);

    press(page, '?');
    page.click('#shortcutsHelp .close-help');
    assert.equal(help.classList.contains('show'), false);
    // Closing again has no focus to restore.
    page.click('#shortcutsHelp .close-help');
    assert.equal(page.document.activeElement, helpButton);
});

test('shortcuts are ignored while a form control has focus', async () => {
    const page = await openReader({ progress: 3 });

    page.$('#pageSlider').focus();
    assert.equal(press(page, 'ArrowRight').defaultPrevented, false);
    const notes = page.document.createElement('textarea');
    page.document.body.append(notes);
    notes.focus();
    assert.equal(press(page, 'd').defaultPrevented, false);
    page.$('#emailPromptFormat').focus();
    assert.equal(press(page, 'End').defaultPrevented, false);

    await page.settle(2);
    assert.equal(shownPage(page).alt, 'Page 3');
});

test('preloaded neighbours appear instantly while other pages show a spinner', async () => {
    const page = await openReader({ progress: 3 });
    assert.deepEqual(pageRequests(page).map(request => request.page), [3, 4, 5, 2]);

    press(page, 'ArrowRight');
    // Page 4 is already cached: the current page stays up until it swaps.
    assert.equal(page.$('#readerContent .loading-state'), null);
    await waitForShownPage(page, 'Page 4');
    assert.equal(pageRequests(page).filter(request => request.page === 4).length, 1);

    press(page, 'End');
    assert.equal(page.$('#readerContent .loading-state').textContent, 'Loading page…');
    assert.ok(page.$('#readerContent #tapZoneRight'));
    await waitForShownPage(page, 'Page 10');
});

test('a slow page never replaces a page the reader has already moved on to', async () => {
    const page = await openReader({ progress: 3 });
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    page.route('GET /api/ComicReader/page', async call => {
        if (call.query.page === '8') await gate;
        return pageImage(call);
    });

    page.input('#pageSlider', '8');
    assert.ok(page.$('#readerContent .loading-state'));
    press(page, 'Home');
    await waitForShownPage(page, 'Page 1');

    release();
    await page.waitFor(() => page.run('mangaPageCache.has(8)'));
    await page.settle();
    assert.equal(shownPage(page).alt, 'Page 1');
    assert.equal(page.$('#pageInfo').textContent, 'Page 1 / 10');
    assert.deepEqual(progressPosts(page).map(post => post.page), [3, 1]);
});

test('the page cache only keeps pages near the current one', async () => {
    const page = await openReader({ series: { [FILE]: 30 } });
    const turnTo = async last => {
        while (page.run('currentPage') < last) {
            press(page, 'ArrowRight');
            await waitForShownPage(page, `Page ${page.run('currentPage')}`);
            await page.settle();
        }
    };

    await turnTo(12);
    let cache = page.evaluate('[...mangaPageCache.entries()]');
    assert.ok(cache.every(([pageNum]) => pageNum >= 7 && pageNum <= 14), JSON.stringify(cache));
    // Evicted pages had their blob URLs revoked; cached ones are still live.
    assert.deepEqual([...page.objectUrls.keys()].sort(), cache.map(([, url]) => url).sort());

    // Eviction does not depend on revoking succeeding.
    page.window.URL.revokeObjectURL = () => { throw new Error('revoke failed'); };
    await turnTo(20);
    cache = page.evaluate('[...mangaPageCache.keys()]');
    assert.ok(cache.every(pageNum => pageNum >= 15 && pageNum <= 22), JSON.stringify(cache));

    // An evicted page is fetched again when revisited.
    press(page, 'Home');
    await waitForShownPage(page, 'Page 1');
    assert.equal(pageRequests(page).filter(request => request.page === 1).length, 2);
    assert.deepEqual(page.errors, []);
});

test('Next on the last page opens the following issue in place, reusing the prefetched pointer', async () => {
    const page = await openReader({ series: { [FILE]: 3, [NEXT_FILE]: 4 }, progress: 3 });
    assert.deepEqual(adjacentRequests(page), [{ file: FILE, direction: 'next' }]);

    page.click('#nextBtn');
    await page.waitFor(() => page.navigations.length > 0);
    assert.deepEqual(page.navigations, [{ url: readerUrl(NEXT_FILE), replace: true }]);
    assert.equal(adjacentRequests(page).length, 1);
});

test('Next on the last page looks the next issue up again when the prefetch failed', async () => {
    let lookups = 0;
    const page = await openReader({
        series: { [FILE]: 3, [NEXT_FILE]: 4 },
        progress: 3,
        routes: { 'GET /api/ComicReader/adjacent': () => (++lookups === 1 ? respond(500, { error: 'busy' }) : adjacentInfo(NEXT_FILE)) }
    });

    press(page, 'ArrowRight');
    await page.waitFor(() => page.navigations.length > 0);
    assert.deepEqual(page.navigations, [{ url: readerUrl(NEXT_FILE), replace: true }]);
    assert.equal(adjacentRequests(page).length, 2);
});

test('a next-issue pointer that still names this comic is neither warmed nor followed', async () => {
    let lookups = 0;
    const page = await openReader({
        series: { [FILE]: 3, [NEXT_FILE]: 4 },
        progress: 3,
        routes: { 'GET /api/ComicReader/adjacent': () => adjacentInfo(++lookups === 1 ? FILE : NEXT_FILE) }
    });
    assert.deepEqual(pageRequests(page).map(request => `${request.file}#${request.page}`), [`${FILE}#3`, `${FILE}#2`]);

    page.click('#tapZoneRight');
    await page.waitFor(() => page.navigations.length > 0);
    assert.deepEqual(page.navigations, [{ url: readerUrl(NEXT_FILE), replace: true }]);
});

test('Next on the last page of the last issue says there is nothing more', async () => {
    const page = await openReader({ series: { [FILE]: 3 }, progress: 3 });

    page.click('#nextBtn');
    await page.waitFor(() => toasts(page).length > 0);
    assert.deepEqual(toasts(page), [{ message: 'No more issues in this series', type: 'info' }]);
    assert.deepEqual(page.navigations, []);
});

test('a failed next-issue lookup is reported', async () => {
    const page = await openReader({ series: { [FILE]: 3 }, progress: 3, routes: { 'GET /api/ComicReader/adjacent': 503 } });

    press(page, ' ');
    await page.waitFor(() => toasts(page).length > 0);
    assert.deepEqual(toasts(page), [{ message: 'Failed to load next comic', type: 'error' }]);
    assert.ok(page.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error navigating to next comic:'));
    assert.deepEqual(page.navigations, []);
});

test('Previous on page 1 opens the previous issue at its last page', async () => {
    const page = await openReader({ series: { [PREV_FILE]: 4, [FILE]: 3 } });

    page.click('#prevBtn');
    await page.waitFor(() => page.navigations.length > 0);
    assert.deepEqual(page.navigations, [{ url: readerUrl(PREV_FILE, '#lastpage'), replace: true }]);
    assert.deepEqual(adjacentRequests(page).at(-1), { file: FILE, direction: 'prev' });
});

test('Previous on page 1 of the first issue says so, and a failed lookup is reported', async () => {
    const first = await openReader({ series: { [FILE]: 3 } });
    press(first, 'ArrowLeft');
    await first.waitFor(() => toasts(first).length > 0);
    assert.deepEqual(toasts(first), [{ message: 'This is the first issue in this series', type: 'info' }]);

    const failing = await openReader({
        routes: { 'GET /api/ComicReader/adjacent': call => (call.query.direction === 'prev' ? respond(500, {}) : adjacentInfo(null)) }
    });
    press(failing, ' ', { shiftKey: true });
    await failing.waitFor(() => toasts(failing).length > 0);
    assert.deepEqual(toasts(failing), [{ message: 'Failed to load previous comic', type: 'error' }]);
    assert.ok(failing.consoleMessages.some(m => m.type === 'error' && m.args[0] === 'Error navigating to previous comic:'));
    assert.deepEqual(failing.navigations, []);
});

test('the next issue is warmed once, from a few pages before the end, until its pages run out', async () => {
    const page = await openReader({ series: { [FILE]: 10, [NEXT_FILE]: 2 }, progress: 7 });
    assert.deepEqual(pageRequests(page, NEXT_FILE), []);

    press(page, 'ArrowRight');
    await waitForShownPage(page, 'Page 8');
    await page.waitFor(() => pageRequests(page, NEXT_FILE).length === 3);
    // The next issue only has two pages: the 404 for page 3 ends the warm-up.
    assert.deepEqual(pageRequests(page, NEXT_FILE).map(request => request.page), [1, 2, 3]);
    // Warming uses fetch (not <img>) so the session goes along.
    const warm = page.fetchCalls.find(call => call.query.filePath === NEXT_FILE);
    assert.match(warm.headers.authorization, /^Bearer /);

    press(page, 'ArrowRight');
    await waitForShownPage(page, 'Page 9');
    press(page, 'End');
    await waitForShownPage(page, 'Page 10');
    await page.settle();
    assert.equal(pageRequests(page, NEXT_FILE).length, 3);
});

test('a network error while warming the next issue ends the warm-up', async () => {
    const page = await openReader({
        series: { [FILE]: 3, [NEXT_FILE]: 8 },
        progress: 3,
        routes: {
            'GET /api/ComicReader/page': call => {
                if (call.query.filePath === NEXT_FILE && call.query.page === '2') throw new TypeError('Failed to fetch');
                return pageImage(call);
            }
        }
    });

    await page.waitFor(() => page.consoleMessages.some(m => m.args[0] === 'Error preloading next comic page'));
    assert.deepEqual(pageRequests(page, NEXT_FILE).map(request => request.page), [1, 2]);
});
