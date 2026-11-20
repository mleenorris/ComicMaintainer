'use strict';

// Remove Pages (js/app/series.js): the issue menu's dialog that shows every
// page of an issue and takes the ticked ones out of its CBZ.

const test = require('node:test');
const assert = require('node:assert/strict');
const { respond } = require('./support/dom');
const h = require('./support/jobs-helpers');
const { clickIssueAction } = require('./support/email-helpers');

const FILE = h.ISSUE_A;
const PAGES = ['p01.jpg', 'p02.jpg', 'p03.jpg'];
const routes = (extra = {}) => ({
    'GET /api/comicreader/info': { fileName: 'Batman 001.cbz', filePath: FILE, pageCount: 3, version: 'v1' },
    'GET /api/comicreader/pages': { pages: PAGES, count: 3 },
    'GET /api/comicreader/page': new Response('img', { headers: { 'Content-Type': 'image/jpeg' } }),
    ...extra
});

const tiles = page => page.$$('#removePagesGrid .remove-pages-option');
const tick = (page, index, checked = true) => page.input(tiles(page)[index].querySelector('input[type="checkbox"]'), checked);
const applyButton = page => page.$('#removePagesApplyBtn');

async function open(page) {
    page.run(`openRemovePagesModal(${JSON.stringify(FILE)})`);
    await page.settle();
}

test("the issue's menu opens the dialog", async (t) => {
    const page = await h.bootSeriesDetail(t, { routes: routes() });

    clickIssueAction(page, FILE, 'Remove Pages…');
    await page.settle();

    assert.equal(page.$('#removePagesModal').classList.contains('active'), true);
    assert.equal(tiles(page).length, 3);
});

test('the dialog shows every page, with the file version in its address, and counts the ticked ones', async (t) => {
    const page = await h.bootSeriesDetail(t, { routes: routes() });
    await open(page);

    assert.equal(page.$('#removePagesModal').classList.contains('active'), true);
    assert.equal(page.$('#removePagesModalTitle').textContent, 'Remove pages from "Batman 001.cbz"');
    assert.deepEqual(tiles(page).map(tile => tile.textContent.trim()), ['Page 1', 'Page 2', 'Page 3']);
    assert.equal(tiles(page)[1].querySelector('img').dataset.protectedImage,
        `/api/comicreader/page?filePath=${encodeURIComponent(FILE)}&page=2&v=v1`);
    assert.equal(applyButton(page).disabled, true);

    tick(page, 0);
    tick(page, 2);
    assert.equal(applyButton(page).textContent, 'Remove 2 Pages');
    assert.equal(applyButton(page).disabled, false);
    assert.ok(tiles(page)[0].classList.contains('selected'));

    // Every page ticked: a comic keeps at least one.
    tick(page, 1);
    assert.equal(applyButton(page).disabled, true);
    assert.equal(page.$('#removePagesSummary').textContent, 'A comic must keep at least one page.');

    tick(page, 1, false);
    tick(page, 2, false);
    assert.equal(applyButton(page).textContent, 'Remove 1 Page');

    page.run('closeRemovePagesModal()');
    assert.equal(page.$('#removePagesModal').classList.contains('active'), false);
});

test('Remove asks first, sends the page names, and reports what is left', async (t) => {
    const page = await h.bootSeriesDetail(t, { routes: routes({ 'POST /api/pages/remove': { removed: 2, pages_left: 1 } }) });
    await open(page);
    tick(page, 2);
    tick(page, 0);

    // Declined: nothing is sent.
    h.answerConfirms(page, false);
    page.click(applyButton(page));
    await page.settle();
    assert.deepEqual(h.takeConfirms(page), [
        'Remove 2 pages (1, 3) from "Batman 001.cbz"?\n\nThe archive is written again without them. This cannot be undone.'
    ]);
    assert.equal(h.requests(page, 'POST', '/api/pages/remove').length, 0);

    h.answerConfirms(page, true);
    page.click(applyButton(page));
    await page.settle();

    const [request] = h.requests(page, 'POST', '/api/pages/remove');
    assert.deepEqual(request.body, { FilePath: FILE, Pages: ['p01.jpg', 'p03.jpg'] });
    assert.equal(page.$('#removePagesModal').classList.contains('active'), false);
    assert.deepEqual(h.takeMessages(page), [{ type: 'success', text: 'Removed 2 pages; 1 left' }]);
});

test('a refusal is shown and the dialog stays open to try again', async (t) => {
    const page = await h.bootSeriesDetail(t, {
        routes: routes({ 'POST /api/pages/remove': respond(400, { error: "Only a CBZ's pages can be removed; convert this file to CBZ first" }) })
    });
    await open(page);
    tick(page, 0);
    h.answerConfirms(page, true);

    page.click(applyButton(page));
    await page.settle();

    assert.deepEqual(h.takeMessages(page), [{ type: 'error', text: "Failed to remove the pages: Only a CBZ's pages can be removed; convert this file to CBZ first" }]);
    assert.equal(page.$('#removePagesModal').classList.contains('active'), true);
    assert.equal(applyButton(page).disabled, false);
});

test('pages that cannot be read, or none at all, are said so', async (t) => {
    const failing = await h.bootSeriesDetail(t, { routes: routes({ 'GET /api/comicreader/pages': respond(404, { error: 'File not found' }) }) });
    await open(failing);
    assert.equal(failing.$('#removePagesGrid').textContent, 'The pages could not be read: File not found');

    const empty = await h.bootSeriesDetail(t, { routes: routes({ 'GET /api/comicreader/pages': { pages: [], count: 0 } }) });
    await open(empty);
    assert.equal(empty.$('#removePagesGrid').textContent, 'The archive has no pages.');
});
